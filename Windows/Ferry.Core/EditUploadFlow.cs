using Ferry.Core.Remote;

namespace Ferry.Core;

/// <summary>サーバーのファイルの一覧の値（サイズと更新日時）。同じ接続先の一覧どうしなので、そのまま等しいかで比べる</summary>
public sealed record RemoteSnapshot(long? Size, DateTime? Modified)
{
    public static RemoteSnapshot Of(RemoteItem item) => new(item.Size, item.Modified);
}

/// <summary>エディタで開いた後に、サーバー側のファイルがどうなったか</summary>
public enum EditConflict
{
    /// <summary>変わっていない（または分からない）。そのままアップロードしてよい</summary>
    None,
    /// <summary>他の人が変えた</summary>
    Changed,
    /// <summary>他の人が削除した</summary>
    Deleted,
}

/// <summary>
/// エディタでの保存 → 衝突の確認 → アップロード の順番を、ファイルごとに守る。
/// 社内で同じサイトを複数人が触るので、開いた後に他の人がサーバー側を変えていたら、黙って上書きせずに確認する。
/// 確認の途中でまた保存されても確認は重ねない（その確認の答えに含める）。アップロードの途中で保存されたら、終わってからもう一度確認する。
/// 呼び出しはアプリでは UI スレッドから（同じファイルについて同時には動かない）。
/// </summary>
public sealed class EditUploadFlow
{
    internal enum Phase { Idle, Checking, Uploading }

    private readonly Func<string, Task<IReadOnlyList<RemoteItem>>> _list;
    private readonly Func<EditedFile, EditConflict, Task<bool>> _confirm;
    private readonly Func<EditedFile, bool, string?, Task<bool>> _upload;

    /// <param name="list">フォルダの一覧（一覧用の接続で取る）。取れなければ例外</param>
    /// <param name="confirm">他の人が変えていたときに聞く。true なら上書きする</param>
    /// <param name="upload">アップロードして、終わるまで待つ（引数はファイル、サーバーにあるか、今の権限）。成功したら true</param>
    public EditUploadFlow(Func<string, Task<IReadOnlyList<RemoteItem>>> list,
                          Func<EditedFile, EditConflict, Task<bool>> confirm,
                          Func<EditedFile, bool, string?, Task<bool>> upload)
    {
        _list = list;
        _confirm = confirm;
        _upload = upload;
    }

    /// <summary>基準と今の一覧を比べる。基準が無い・一覧を取れなかったときは None（確認のために保存を止めない）</summary>
    public static EditConflict Compare(RemoteSnapshot? baseline, IReadOnlyList<RemoteItem>? listing, string remotePath)
    {
        if (baseline is null || listing is null) return EditConflict.None;
        if (Find(listing, remotePath) is not { } current) return EditConflict.Deleted;
        return RemoteSnapshot.Of(current) == baseline ? EditConflict.None : EditConflict.Changed;
    }

    /// <summary>エディタで保存されたとき</summary>
    public async Task SavedAsync(EditedFile file)
    {
        if (file.FlowPhase != Phase.Idle)
        {
            // 確認の途中の保存は、その確認の答えに含める（確認を重ねない）。アップロードの途中の保存は、終わってからもう一度
            if (file.FlowPhase == Phase.Uploading) file.SavedDuringUpload = true;
            return;
        }
        try
        {
            do
            {
                file.SavedDuringUpload = false;
                file.FlowPhase = Phase.Checking;
                var listing = await TryListAsync(file);
                var conflict = Compare(file.Baseline, listing, file.RemotePath);
                // 「アップロードしない」なら基準はそのまま（次に保存したときもまた確認する）
                if (conflict != EditConflict.None && !await _confirm(file, conflict)) return;
                var current = listing is null ? null : Find(listing, file.RemotePath);
                // 一覧を取れなかったときは、開いたファイルなので「ある」とみなす
                var exists = listing is null || current is not null;
                file.FlowPhase = Phase.Uploading;
                if (await _upload(file, exists, current?.Permissions)) await RefreshBaselineAsync(file);
            } while (file.SavedDuringUpload);
        }
        finally
        {
            file.FlowPhase = Phase.Idle;
        }
    }

    /// <summary>
    /// 失敗したアップロードを転送一覧から「やり直し」て成功したとき。自分のアップロードを次の保存で
    /// 「変更された」と誤って判定しないよう、基準を取り直す（確認・アップロードの途中なら、そちらで取り直す）
    /// </summary>
    public async Task UploadedOutsideFlowAsync(EditedFile file)
    {
        if (file.FlowPhase == Phase.Idle) await RefreshBaselineAsync(file);
    }

    /// <summary>アップロード後の一覧の値を基準にする（取れなければ「分からない」にして、次は確認しない）</summary>
    private async Task RefreshBaselineAsync(EditedFile file)
    {
        var listing = await TryListAsync(file);
        file.Baseline = listing is not null && Find(listing, file.RemotePath) is { } item ? RemoteSnapshot.Of(item) : null;
    }

    private async Task<IReadOnlyList<RemoteItem>?> TryListAsync(EditedFile file)
    {
        try
        {
            return await _list(RemotePath.Parent(file.RemotePath));
        }
        catch (Exception)
        {
            // 通信エラーなど。確認のために保存を止めない
            return null;
        }
    }

    private static RemoteItem? Find(IReadOnlyList<RemoteItem> listing, string remotePath)
    {
        var name = RemotePath.Name(remotePath);
        return listing.FirstOrDefault(i => i.Name == name && !i.IsDirectory);
    }
}
