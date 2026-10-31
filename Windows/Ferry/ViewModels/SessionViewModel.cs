using CommunityToolkit.Mvvm.ComponentModel;
using Ferry.Core;
using Ferry.Core.Remote;

namespace Ferry.ViewModels;

public enum SessionStatus
{
    Idle,
    Connecting,
    Connected,
    Failed,
    /// <summary>パスワードが未保存、またはログインに失敗した</summary>
    NeedsPassword,
    /// <summary>FTPS の証明書を検証できなかった（自己署名など）</summary>
    UntrustedCertificate,
}

/// <summary>1 つの接続先を開いている間の状態（左右のペイン）</summary>
public sealed partial class SessionViewModel : ObservableObject, IAsyncDisposable
{
    public ServerConfig Server { get; private set; }
    public BrowserViewModel Local { get; }
    public BrowserViewModel Remote { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsConnecting), nameof(ShowPasswordPrompt),
                              nameof(ShowCertificatePrompt), nameof(ShowFailure), nameof(ShowRemotePane), nameof(ShowStatusPanel))]
    public partial SessionStatus Status { get; private set; } = SessionStatus.Idle;

    /// <summary>エラーやパスワード画面に出す説明</summary>
    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    public bool IsConnected => Status == SessionStatus.Connected;
    public bool IsConnecting => Status is SessionStatus.Connecting or SessionStatus.Idle;
    public bool ShowPasswordPrompt => Status == SessionStatus.NeedsPassword;
    public bool ShowCertificatePrompt => Status == SessionStatus.UntrustedCertificate;
    public bool ShowFailure => Status == SessionStatus.Failed;
    public bool ShowRemotePane => Status == SessionStatus.Connected;
    /// <summary>接続していないときはサーバー側に状態（接続中・パスワード・証明書・エラー）を出す</summary>
    public bool ShowStatusPanel => !ShowRemotePane;

    public string Title => Server.Name;
    public string Subtitle => $"{Server.ServerProtocol.DisplayName()} · {Server.Username}@{Server.Host}";
    public string PasswordLabel => Server.PrivateKeyPath is { Length: > 0 } ? "秘密鍵のパスフレーズ" : "パスワード";

    /// <summary>接続後に開くブックマーク（サイドバーから選んだときなど）</summary>
    public PaneBookmark? PendingBookmark { get; set; }

    public TransferQueueViewModel Transfers { get; }

    private IRemoteClient _browseClient;
    private bool _hasPassword;

    public SessionViewModel(ServerConfig server)
    {
        Server = server;
        var settings = AppServices.Settings;
        Local = new BrowserViewModel(PaneSide.Local, settings.ShowHiddenLocal);
        // WordPress などでは .htaccess を触るのでサーバー側は隠しファイルも表示する
        Remote = new BrowserViewModel(PaneSide.Remote, settings.ShowHiddenRemote);
        var password = AppServices.Passwords.GetPassword(server.Id);
        // 一覧用と転送用で接続を分け、転送中でもフォルダを移動できるようにする
        _browseClient = RemoteClientFactory.Create(server, password);
        Remote.Client = _browseClient;
        Transfers = new TransferQueueViewModel(RemoteClientFactory.Create(server, password), () => _browseClient, server.Name);
        Transfers.Idle += async (_, _) =>
        {
            await Local.RefreshAsync();
            await Remote.RefreshAsync();
        };
        // 秘密鍵はパスフレーズ無しの場合もあるので、パスワード無しでも接続を試す
        _hasPassword = password is not null || server.PrivateKeyPath is { Length: > 0 } || server.Username == "anonymous";
    }

    // MARK: 転送

    /// <summary>ローカルの項目を、サーバー側で開いているフォルダへ</summary>
    public Task UploadAsync(IEnumerable<string> localPaths) =>
        IsConnected ? Transfers.UploadAsync(localPaths.ToList(), Remote.Path) : Task.CompletedTask;

    /// <summary>サーバーの項目を、ローカル側で開いているフォルダへ</summary>
    public Task DownloadAsync(IEnumerable<Models.FileEntry> entries) =>
        IsConnected && Local.Path.Length > 0
            ? Transfers.DownloadAsync(entries.Select(ToRemoteItem).ToList(), Local.Path)
            : Task.CompletedTask;

    /// <summary>ブックマークした組み合わせを左右のペインで開く。未接続なら接続後に開く</summary>
    public async Task OpenBookmarkAsync(PaneBookmark bookmark)
    {
        if (!IsConnected)
        {
            PendingBookmark = bookmark;
            if (Status is SessionStatus.Idle or SessionStatus.Failed) await ConnectAsync();
            return;
        }
        await Local.LoadAsync(bookmark.LocalPath);
        await Remote.LoadAsync(bookmark.RemotePath);
    }

    // MARK: プレビュー（一覧表示や転送を待たせないよう接続を分ける）

    private const long PreviewSizeLimit = 50L * 1024 * 1024;
    private const long PrefetchSizeLimit = 5L * 1024 * 1024;
    private IRemoteClient? _previewClient;
    private string? _password;
    private readonly Dictionary<string, Task<string?>> _previewDownloads = [];

    private IRemoteClient PreviewClient =>
        _previewClient ??= RemoteClientFactory.Create(Server, _password ?? AppServices.Passwords.GetPassword(Server.Id));

    private static string CacheRoot(string kind) =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Ferry", kind);

    /// <summary>サーバーのパスとサイズごとの置き場所（同じサイズのものは使い回す）</summary>
    private string PreviewCachePath(Models.FileEntry entry)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{Server.Id}|{entry.Path}|{entry.Size}")))[..16];
        return System.IO.Path.Combine(CacheRoot("Preview"), key, TransferPlanner.LocalName(entry.Name));
    }

    public string? CachedPreviewPath(Models.FileEntry entry) =>
        PreviewCachePath(entry) is var path && File.Exists(path) ? path : null;

    /// <summary>プレビュー用に一時フォルダへダウンロードする（転送一覧には出さない）。50MB を超えるものはしない</summary>
    public async Task<string?> PreviewPathAsync(Models.FileEntry entry)
    {
        if (entry.IsDirectory) return null;
        if (entry.Size > PreviewSizeLimit)
        {
            Remote.ErrorMessage = $"{entry.Name} は大きいため（{Models.FileEntry.FormatSize(entry.Size.Value)}）プレビューしません。";
            return null;
        }
        if (CachedPreviewPath(entry) is { } cached) return cached;
        if (_previewDownloads.TryGetValue(entry.Path, out var running)) return await running;
        var task = DownloadPreviewAsync(entry);
        _previewDownloads[entry.Path] = task;
        try
        {
            return await task;
        }
        finally
        {
            _previewDownloads.Remove(entry.Path);
        }
    }

    private async Task<string?> DownloadPreviewAsync(Models.FileEntry entry)
    {
        var path = PreviewCachePath(entry);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await PreviewClient.DownloadAsync(entry.Path, path, entry.Size);
            return path;
        }
        catch (Exception e)
        {
            Remote.ErrorMessage = "プレビュー用のダウンロードに失敗しました: " + BrowserViewModel.Describe(e);
            return null;
        }
    }

    /// <summary>サーバー側で小さいファイルを 1 つ選んだら、スペースを押す前に裏でダウンロードしておく</summary>
    public void PrefetchPreview(Models.FileEntry entry)
    {
        if (!IsConnected || entry.IsDirectory || entry.Size is not { } size || size > PrefetchSizeLimit
            || CachedPreviewPath(entry) is not null || _previewDownloads.ContainsKey(entry.Path))
            return;
        _ = PreviewPathAsync(entry);
    }

    // MARK: エディタで開いて自動アップロード

    private EditWatcher? _editWatcher;

    [ObservableProperty]
    public partial IReadOnlyList<EditedFile> EditedFiles { get; private set; } = [];

    [ObservableProperty]
    public partial string? OpeningForEdit { get; private set; }

    private EditWatcher Watcher
    {
        get
        {
            if (_editWatcher is not null) return _editWatcher;
            _editWatcher = new EditWatcher(file => AppServices.OnUi(() => Transfers.UploadEdited(file.LocalPath, file.RemotePath)));
            _editWatcher.Changed += (_, _) => AppServices.OnUi(() => EditedFiles = _editWatcher.Files);
            return _editWatcher;
        }
    }

    /// <summary>一時フォルダにダウンロードしてエディタで開き、保存されるたびにアップロードする</summary>
    public async Task OpenInEditorAsync(Models.FileEntry entry)
    {
        if (entry.IsDirectory || !IsConnected) return;
        var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{Server.Id}|{entry.Path}")))[..16];
        var path = System.IO.Path.Combine(CacheRoot("Edit"), key, TransferPlanner.LocalName(entry.Name));
        OpeningForEdit = entry.Name;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await PreviewClient.DownloadAsync(entry.Path, path, entry.Size);
            Watcher.Watch(path, entry.Path);
            Editor.Open(path);
        }
        catch (Exception e)
        {
            Remote.ErrorMessage = $"「{entry.Name}」をエディタで開けませんでした: {BrowserViewModel.Describe(e)}";
        }
        finally
        {
            OpeningForEdit = null;
        }
    }

    public void ReopenEdited(EditedFile file)
    {
        try { Editor.Open(file.LocalPath); }
        catch (Exception e) { Remote.ErrorMessage = $"「{file.Name}」を開けませんでした: {e.Message}"; }
    }

    public void StopEditing(EditedFile file) => Watcher.Stop(file.Id);

    public static RemoteItem ToRemoteItem(Models.FileEntry entry) => new()
    {
        Name = entry.Name,
        Path = entry.Path,
        Kind = entry.IsDirectory ? RemoteItem.ItemKind.Directory : RemoteItem.ItemKind.File,
        Size = entry.Size,
        Modified = entry.Modified,
    };

    public async Task ConnectAsync()
    {
        if (Status == SessionStatus.Connecting) return;
        Status = SessionStatus.Connecting;

        // 接続時に開くブックマーク（サイドバーから選んだもの、なければ接続先の設定）。
        // ローカル側はパスワードを聞く前に開いておく
        var bookmark = PendingBookmark ?? (Local.Path.Length == 0 ? Server.StartBookmark : null);
        if (bookmark is not null)
        {
            await Local.LoadAsync(bookmark.LocalPath, recordHistory: false);
        }
        else if (Local.Path.Length == 0)
        {
            var localPath = Server.LocalPath is { Length: > 0 } path && Directory.Exists(Environment.ExpandEnvironmentVariables(path))
                ? Environment.ExpandEnvironmentVariables(path)
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            await Local.LoadAsync(localPath, recordHistory: false);
        }

        // 画面を開いた後にパスワードが保存された場合に備えて、もう一度探す
        if (!_hasPassword && AppServices.Passwords.GetPassword(Server.Id) is { } saved)
        {
            _browseClient.SetPassword(saved);
            Transfers.SetPassword(saved);
            _hasPassword = true;
        }
        if (!_hasPassword)
        {
            // ブックマークはパスワードを入れた後の接続で開く
            StatusMessage = null;
            Status = SessionStatus.NeedsPassword;
            return;
        }
        PendingBookmark = null;

        try
        {
            // ログインの確認を兼ねてホームを取得する
            var home = await _browseClient.HomeDirectoryAsync();
            var start = bookmark?.RemotePath
                        ?? (Remote.Path.Length > 0 ? Remote.Path : null)
                        ?? (string.IsNullOrEmpty(Server.InitialPath) ? null : Server.InitialPath)
                        ?? home;
            await Remote.LoadAsync(start, recordHistory: false);
            if (Remote.Path.Length == 0 && start != home)
            {
                // 最初に開くフォルダが無くなっていたら、ホームを開いて理由を表示する
                var reason = Remote.ErrorMessage;
                await Remote.LoadAsync(home, recordHistory: false);
                if (Remote.Path.Length > 0) Remote.ErrorMessage = $"「{start}」を開けませんでした。{reason}";
            }
            if (Remote.Path.Length == 0)
            {
                Fail(Remote.ErrorMessage);
            }
            else
            {
                StatusMessage = null;
                Status = SessionStatus.Connected;
            }
        }
        catch (FerryException e) when (e.Kind == FerryErrorKind.LoginFailed)
        {
            StatusMessage = e.Message;
            Status = SessionStatus.NeedsPassword;
        }
        catch (FerryException e) when (e.Kind == FerryErrorKind.UntrustedCertificate)
        {
            StatusMessage = e.Message;
            Status = SessionStatus.UntrustedCertificate;
        }
        catch (Exception e)
        {
            Fail(BrowserViewModel.Describe(e));
        }
    }

    private void Fail(string? message)
    {
        StatusMessage = message ?? "接続できませんでした。";
        Status = SessionStatus.Failed;
    }

    /// <summary>パスワード入力画面で入力されたパスワードで接続する</summary>
    public async Task ConnectWithPasswordAsync(string password, bool save)
    {
        _browseClient.SetPassword(password);
        Transfers.SetPassword(password);
        _password = password;
        _previewClient?.SetPassword(password);
        if (save) AppServices.Passwords.SetPassword(Server.Id, password);
        _hasPassword = true;
        Status = SessionStatus.Idle;
        await ConnectAsync();
    }

    /// <summary>「証明書を検証せずに接続」。接続設定の verifyCertificate を false にして接続し直す</summary>
    public async Task ConnectWithoutCertificateVerificationAsync()
    {
        var updated = Server with { VerifyCertificate = false };
        AppServices.Store.Upsert(updated);
        await ReplaceServerAsync(updated);
        await ConnectAsync();
    }

    /// <summary>接続設定が変わったとき。接続を作り直す（開いている場所はそのまま）</summary>
    public async Task ReplaceServerAsync(ServerConfig server)
    {
        var old = _browseClient;
        Server = server;
        var password = AppServices.Passwords.GetPassword(server.Id);
        _browseClient = RemoteClientFactory.Create(server, password);
        Remote.Client = _browseClient;
        await Transfers.ReplaceClientAsync(RemoteClientFactory.Create(server, password));
        if (_previewClient is not null)
        {
            await _previewClient.DisposeAsync();
            _previewClient = null;
        }
        _hasPassword = password is not null || server.PrivateKeyPath is { Length: > 0 } || server.Username == "anonymous";
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(PasswordLabel));
        Status = SessionStatus.Idle;
        await old.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _editWatcher?.Dispose();
        await Transfers.DisposeClientAsync();
        await _browseClient.DisposeAsync();
        if (_previewClient is not null) await _previewClient.DisposeAsync();
    }
}
