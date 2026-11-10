namespace Ferry.Core.Remote;

/// <summary>アップロードのしかた（呼び出し側が分かっていること）</summary>
public sealed record UploadOptions
{
    /// <summary>
    /// 一時的な名前（".&lt;名前&gt;.ferrypart"）へ送ってから置き換える。送り先に同名のファイルがあると分かっているときだけ使う
    /// （途中で切れても、本番のファイルが書きかけにならないように）
    /// </summary>
    public bool ViaTemporaryFile { get; init; }
    /// <summary>置き換える前のファイルのパーミッション（"rwxr-xr-x" 形式）。置き換えた後に同じにする。分からなければ null</summary>
    public string? Permissions { get; init; }
}

/// <summary>
/// 一時ファイルへ送ってから置き換えるアップロードの手順（FTP・SFTP 共通）。
/// 1 回のアップロードにつき 1 つ作り、接続が切れたときのやり直し（RunAsync の再試行）でも同じものを使う。
/// やり直しでは、済んだところ（一時ファイルを送り終えた、元のファイルを消した）から続ける。
/// </summary>
internal sealed class ReplacingUpload
{
    /// <summary>接続ごとの操作。例外は各ライブラリのものをそのまま投げてよい（再試行の判断に使うため）</summary>
    internal interface ISteps
    {
        Task UploadAsync(string remotePath, CancellationToken cancellation);
        /// <summary>名前を変える。置き換え先があると失敗するサーバーもある</summary>
        Task RenameAsync(string from, string to, CancellationToken cancellation);
        Task DeleteAsync(string path, CancellationToken cancellation);
        Task<bool> FileExistsAsync(string path, CancellationToken cancellation);
        /// <summary>失敗は無視してよい</summary>
        Task ChangePermissionsAsync(string path, string mode, CancellationToken cancellation);
        /// <summary>更新日時をローカルのファイルに合わせる。失敗は無視してよい</summary>
        Task SetModifiedTimeAsync(string path, CancellationToken cancellation);
        /// <summary>中止・失敗したときの一時ファイルの削除（中止の後でも消せるよう、必要ならつなぎ直す）。消せたら true</summary>
        Task<bool> TryCleanupAsync(string path);
        bool IsPermissionDenied(Exception e);
    }

    public string RemotePath { get; }
    public string TemporaryPath { get; }
    private readonly string? _permissions;

    /// <summary>一時ファイルを送り終えた（やり直しでは送り直さない）</summary>
    private bool _uploaded;
    /// <summary>名前を変えられなかったので、元のファイルを消した</summary>
    private bool _originalDeleted;
    /// <summary>名前の変更が失敗した（元のファイルは残っている）</summary>
    private bool _renameFailed;
    private bool _completed;

    public ReplacingUpload(string remotePath, string? permissions)
    {
        RemotePath = remotePath;
        TemporaryPath = TemporaryPathFor(remotePath);
        _permissions = permissions;
    }

    public static string TemporaryPathFor(string remotePath) =>
        Core.RemotePath.Join(Core.RemotePath.Parent(remotePath), "." + Core.RemotePath.Name(remotePath) + ".ferrypart");

    public async Task RunAsync(ISteps steps, CancellationToken cancellation)
    {
        if (!_uploaded)
        {
            try
            {
                await steps.UploadAsync(TemporaryPath, cancellation);
            }
            catch (Exception e) when (!_originalDeleted && !cancellation.IsCancellationRequested && steps.IsPermissionDenied(e))
            {
                // フォルダに書けない（ファイルだけ書ける）サーバーでは一時ファイルを作れないので、今までどおり直接上書きする。
                // FluentFTP の上書きは「消してから送る」なので、権限も戻す
                await steps.TryCleanupAsync(TemporaryPath);
                await steps.UploadAsync(RemotePath, cancellation);
                await FinishAsync(steps);
                return;
            }
            catch
            {
                // 元のファイルを消した後は、一時ファイルが新しい内容の唯一の置き場所なので消さない
                if (!_originalDeleted) await steps.TryCleanupAsync(TemporaryPath);
                throw;
            }
            _uploaded = true;
        }

        // ここからは短い操作なので、中止されても最後まで済ませる（途中で止めると元のファイルが無い状態になりうる）
        if (!_originalDeleted)
        {
            try
            {
                await steps.RenameAsync(TemporaryPath, RemotePath, CancellationToken.None);
            }
            catch (Exception renameError)
            {
                // 置き換え先があると名前を変えられないサーバー（OpenSSH の通常の rename、IIS の FTP など）。
                // ただし、一時ファイルが無いなら名前の変更は済んでいる（応答の前に接続が切れた・やり直しなど）ので、元のファイルを消さない
                bool temporaryExists;
                try
                {
                    temporaryExists = await steps.FileExistsAsync(TemporaryPath, CancellationToken.None);
                }
                catch (Exception)
                {
                    throw renameError;
                }
                if (!temporaryExists)
                {
                    await FinishAsync(steps);
                    return;
                }
                try
                {
                    await steps.DeleteAsync(RemotePath, CancellationToken.None);
                }
                catch (Exception)
                {
                    // 元のファイルはそのまま。一時ファイルを消してから、名前を変えられなかった理由を返す
                    _renameFailed = true;
                    if (await steps.TryCleanupAsync(TemporaryPath)) _uploaded = false;
                    throw renameError;
                }
                _originalDeleted = true;
            }
        }
        if (_originalDeleted)
        {
            // 失敗しても一時ファイルは残す（Explain でその名前を伝える）
            await steps.RenameAsync(TemporaryPath, RemotePath, CancellationToken.None);
        }
        await FinishAsync(steps);
    }

    /// <summary>置き換えると新しいファイルは既定の権限になるので、元の権限に戻す。更新日時は置き換えた後の名前に合わせる</summary>
    private async Task FinishAsync(ISteps steps)
    {
        _completed = true;
        if (_permissions is not null && Permissions.ToOctal(_permissions) is { } mode)
            await steps.ChangePermissionsAsync(RemotePath, mode, CancellationToken.None);
        await steps.SetModifiedTimeAsync(RemotePath, CancellationToken.None);
    }

    /// <summary>失敗したときのエラーに、サーバー上のファイルがどうなっているかを足す</summary>
    public FerryException Explain(FerryException e)
    {
        if (_completed || e.Kind == FerryErrorKind.Cancelled) return e;
        var name = Core.RemotePath.Name(RemotePath);
        if (_originalDeleted)
            return new FerryException(e.Kind,
                $"「{name}」を置き換えられませんでした。元のファイルは削除済みで、新しい内容はサーバーの「{Core.RemotePath.Name(TemporaryPath)}」に残っています"
                + $"（サーバー上でこのファイルの名前を「{name}」に変えてください）。{e.Message}", e);
        if (_renameFailed)
            return new FerryException(e.Kind, $"「{name}」を置き換えられませんでした（サーバーのファイルは元のままです）。{e.Message}", e);
        return e;
    }
}
