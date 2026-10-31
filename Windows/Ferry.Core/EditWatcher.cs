namespace Ferry.Core;

/// <summary>エディタで開いているサーバーのファイル</summary>
public sealed class EditedFile
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string RemotePath { get; init; }
    public required string LocalPath { get; init; }
    public DateTime? LastModified { get; internal set; }
    public DateTime? LastUploaded { get; internal set; }
    /// <summary>変更を検知したが、書き込み途中かもしれないので次の確認まで待っている</summary>
    internal DateTime? PendingSince { get; set; }

    public string Name => Path.GetFileName(LocalPath);
}

/// <summary>
/// サーバーのファイルをエディタで開いている間、保存を検知してアップロードする。
/// エディタは一時ファイルに書いて置き換えることが多いので、ファイル監視ではなく更新日時を 1 秒ごとに見る。
/// 書き込み途中を避けるため、更新日時が 1 回の確認の間変わらなければ書き込み完了とみなす。
/// </summary>
public sealed class EditWatcher : IDisposable
{
    private readonly List<EditedFile> _files = [];
    private readonly Action<EditedFile> _onSave;
    private readonly object _lock = new();
    private Timer? _timer;

    /// <summary>onSave はタイマーのスレッドから呼ばれる</summary>
    public EditWatcher(Action<EditedFile> onSave)
    {
        _onSave = onSave;
    }

    /// <summary>監視中のファイルが増えた・減ったとき</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<EditedFile> Files
    {
        get { lock (_lock) return _files.ToList(); }
    }

    public EditedFile Watch(string localPath, string remotePath, bool startTimer = true)
    {
        var file = new EditedFile { LocalPath = localPath, RemotePath = remotePath, LastModified = Modified(localPath) };
        lock (_lock)
        {
            _files.RemoveAll(f => f.RemotePath == remotePath);
            _files.Add(file);
            if (startTimer) _timer ??= new Timer(_ => Poll(), null, 1000, 1000);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return file;
    }

    public void Stop(Guid id)
    {
        lock (_lock)
        {
            _files.RemoveAll(f => f.Id == id);
            if (_files.Count == 0) StopTimer();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void StopAll()
    {
        lock (_lock)
        {
            _files.Clear();
            StopTimer();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>1 回分の確認（テストからも呼ぶ）</summary>
    internal void Poll()
    {
        var saved = new List<EditedFile>();
        lock (_lock)
        {
            foreach (var file in _files)
            {
                if (Modified(file.LocalPath) is not { } modified) continue;
                if (file.PendingSince is { } pending)
                {
                    // 前回から更新日時が変わっていなければ書き込み完了とみなす
                    if (modified == pending)
                    {
                        file.PendingSince = null;
                        file.LastModified = modified;
                        file.LastUploaded = DateTime.UtcNow;
                        saved.Add(file);
                    }
                    else
                    {
                        file.PendingSince = modified;
                    }
                }
                else if (modified != file.LastModified)
                {
                    file.PendingSince = modified;
                }
            }
        }
        foreach (var file in saved) _onSave(file);
    }

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private static DateTime? Modified(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;

    public void Dispose()
    {
        lock (_lock) StopTimer();
    }
}
