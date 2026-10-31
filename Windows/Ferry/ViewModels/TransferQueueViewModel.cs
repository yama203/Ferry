using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ferry.Core;
using Ferry.Core.Remote;

namespace Ferry.ViewModels;

public enum TransferState { Waiting, Running, Finished, Cancelled, Failed }

/// <summary>転送一覧の 1 行</summary>
public sealed partial class TransferItem : ObservableObject
{
    public PlannedTransfer Plan { get; }
    public TransferDirection Direction => Plan.Direction;
    public string Name => Plan.Name;
    public string Detail => Direction == TransferDirection.Upload ? $"→ {Plan.RemotePath}" : $"← {Plan.RemotePath}";
    public string Glyph => Direction == TransferDirection.Upload ? "" : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsRunning), nameof(StatusText), nameof(CanCancel), nameof(CanRetry),
                              nameof(IsFailed), nameof(IsProgressVisible))]
    public partial TransferState State { get; private set; } = TransferState.Waiting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent), nameof(StatusText))]
    public partial long Done { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent), nameof(StatusText))]
    public partial long Total { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? ErrorMessage { get; private set; }

    public CancellationTokenSource? Cancellation { get; set; }
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TransferItem(PlannedTransfer plan)
    {
        Plan = plan;
        Total = plan.Size;
    }

    public bool IsActive => State is TransferState.Waiting or TransferState.Running;
    public bool IsRunning => State == TransferState.Running;
    public bool IsFailed => State == TransferState.Failed;
    public bool IsProgressVisible => State == TransferState.Running;
    public bool CanCancel => IsActive;
    public bool CanRetry => State is TransferState.Failed or TransferState.Cancelled;
    public double Percent => Total > 0 ? Math.Min(100.0 * Done / Total, 100) : 0;

    public string StatusText => State switch
    {
        TransferState.Waiting => "待機中",
        TransferState.Running => $"{FormatBytes(Done)} / {FormatBytes(Total)}",
        TransferState.Finished => "完了",
        TransferState.Cancelled => "中止",
        _ => "失敗: " + ErrorMessage,
    };

    /// <summary>終わる（完了・中止・失敗）まで待つ</summary>
    public Task Completion => _completion.Task;

    public void SetState(TransferState state, string? error = null)
    {
        ErrorMessage = error;
        State = state;
        if (!IsActive) _completion.TrySetResult();
    }

    private static string FormatBytes(long bytes) => Models.FileEntry.FormatSize(bytes);
}

/// <summary>上書き確認を待っているまとまり（1 回のドラッグやボタン操作の単位）</summary>
public sealed record PendingConflict(TransferDirection Direction, IReadOnlyList<PlannedTransfer> Items, bool Notifies)
{
    public int ConflictCount => Items.Count(i => i.DestinationExists);
    public string FirstConflictName => Items.FirstOrDefault(i => i.DestinationExists)?.Name ?? "";
}

/// <summary>
/// 転送の待ち行列。転送は専用の接続で 1 件ずつ処理し、フォルダの中身の一覧は一覧表示用の接続で取る
/// （転送中でもフォルダを移動できるように）。
/// </summary>
public sealed partial class TransferQueueViewModel : ObservableObject
{
    public ObservableCollection<TransferItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    public partial int ActiveCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    public partial bool IsPreparing { get; private set; }

    [ObservableProperty]
    public partial bool HasItems { get; private set; }

    /// <summary>同名のファイルがあるとき、どうするかを聞く（null なら転送をやめる）</summary>
    public Func<PendingConflict, Task<ConflictPolicy?>>? AskConflict { get; set; }
    /// <summary>待ち行列が空になったとき（両ペインの再読み込み用）</summary>
    public event EventHandler? Idle;

    private IRemoteClient _client;
    private readonly Func<IRemoteClient> _lister;
    private readonly string _serverName;
    private bool _running;

    public TransferQueueViewModel(IRemoteClient client, Func<IRemoteClient> lister, string serverName)
    {
        _client = client;
        _lister = lister;
        _serverName = serverName;
        Items.CollectionChanged += (_, _) => UpdateCounts();
    }

    public string SummaryText => IsPreparing ? "転送するファイルを調べています…"
        : ActiveCount > 0 ? $"残り {ActiveCount} 件" : "";

    private static ExcludeRules Rules => AppServices.Store.ExcludeRules;

    /// <summary>接続時に入力されたパスワード（保存しなかった場合も転送に使う）</summary>
    public void SetPassword(string? password) => _client.SetPassword(password);

    /// <summary>接続設定が変わったとき。転送中のものは止める</summary>
    public async Task ReplaceClientAsync(IRemoteClient client)
    {
        CancelAll();
        var old = _client;
        _client = client;
        await old.DisposeAsync();
    }

    // MARK: 追加

    public async Task UploadAsync(IReadOnlyList<string> localPaths, string remoteDirectory)
    {
        if (localPaths.Count == 0 || remoteDirectory.Length == 0) return;
        IsPreparing = true;
        try
        {
            var batch = await Task.Run(() => TransferPlanner.CollectUploads(localPaths, remoteDirectory, Rules));
            batch = await TransferPlanner.MarkRemoteConflictsAsync(batch, _lister());
            await SubmitAsync(batch, TransferDirection.Upload);
        }
        finally
        {
            IsPreparing = false;
        }
    }

    public async Task DownloadAsync(IReadOnlyList<RemoteItem> remoteItems, string localDirectory)
    {
        if (remoteItems.Count == 0 || localDirectory.Length == 0) return;
        IsPreparing = true;
        try
        {
            List<PlannedTransfer> batch;
            try
            {
                batch = await TransferPlanner.CollectDownloadsAsync(remoteItems, localDirectory, _lister(), Rules);
            }
            catch (Exception e)
            {
                AddFailed(remoteItems[0], localDirectory, e);
                return;
            }
            await SubmitAsync(TransferPlanner.MarkLocalConflicts(batch), TransferDirection.Download);
        }
        finally
        {
            IsPreparing = false;
        }
    }

    /// <summary>エクスプローラーへのドラッグ用。一時フォルダへダウンロードし、終わるまで待つ（確認・通知なし）</summary>
    public async Task<IReadOnlyList<TransferItem>> DownloadAndWaitAsync(IReadOnlyList<RemoteItem> remoteItems, string localDirectory)
    {
        var batch = await TransferPlanner.CollectDownloadsAsync(remoteItems, localDirectory, _lister(), Rules);
        var started = Start(batch, TransferDirection.Download, notifies: false);
        await Task.WhenAll(started.Select(i => i.Completion));
        if (started.FirstOrDefault(i => i.State != TransferState.Finished) is { } failed)
            throw FerryException.Other(failed.ErrorMessage ?? "中止しました。");
        return started;
    }

    /// <summary>同期など、転送するものが確定しているまとまりを追加する（確認なし。通知は同期側で出す）</summary>
    public IReadOnlyList<TransferItem> EnqueuePlanned(IReadOnlyList<PlannedTransfer> batch) =>
        batch.Count == 0 ? [] : Start(batch, batch[0].Direction, notifies: false);

    /// <summary>エディタで保存されたファイルをそのままアップロードする（確認・通知なし）</summary>
    public TransferItem UploadEdited(string localPath, string remotePath)
    {
        var plan = new PlannedTransfer
        {
            Direction = TransferDirection.Upload, LocalPath = localPath, RemotePath = remotePath,
            Size = new FileInfo(localPath).Length,
        };
        return Start([plan], TransferDirection.Upload, notifies: false)[0];
    }

    private void AddFailed(RemoteItem item, string localDirectory, Exception e)
    {
        var failed = new TransferItem(new PlannedTransfer
        {
            Direction = TransferDirection.Download, RemotePath = item.Path,
            LocalPath = Path.Combine(localDirectory, TransferPlanner.LocalName(item.Name)),
        });
        failed.SetState(TransferState.Failed, BrowserViewModel.Describe(e));
        Items.Add(failed);
    }

    private async Task SubmitAsync(List<PlannedTransfer> batch, TransferDirection direction)
    {
        if (batch.Count == 0) return;
        if (batch.Any(t => t.DestinationExists))
        {
            var policy = AskConflict is null ? ConflictPolicy.NewerOnly : await AskConflict(new PendingConflict(direction, batch, true));
            if (policy is not { } chosen) return;
            batch = TransferPlanner.Apply(batch, chosen);
        }
        Start(batch, direction, notifies: true);
    }

    private List<TransferItem> Start(IReadOnlyList<PlannedTransfer> batch, TransferDirection direction, bool notifies)
    {
        var items = batch.Select(p => new TransferItem(p)).ToList();
        if (items.Count == 0) return items;
        foreach (var item in items)
        {
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TransferItem.State)) UpdateCounts();
            };
            Items.Add(item);
        }
        Pump();
        if (notifies && AppServices.Settings.NotifyOnComplete) _ = NotifyWhenDoneAsync(items, direction);
        return items;
    }

    /// <summary>1 回の操作で始めた転送が全部終わったら、まとめて 1 回通知する</summary>
    private async Task NotifyWhenDoneAsync(List<TransferItem> batch, TransferDirection direction)
    {
        await Task.WhenAll(batch.Select(i => i.Completion));
        var finished = batch.Where(i => i.State == TransferState.Finished).ToList();
        var failed = batch.Count(i => i.State == TransferState.Failed);
        // すべて中止した場合は知らせない
        if (finished.Count == 0 && failed == 0) return;
        var action = direction == TransferDirection.Upload ? "アップロード" : "ダウンロード";
        var title = failed > 0 ? $"{action}が終わりました（失敗 {failed} 件）" : $"{action}が完了しました";
        var body = finished.FirstOrDefault()?.Name ?? batch.First().Name;
        if (finished.Count > 1) body += $" ほか {finished.Count - 1} 件";
        Notifier.Show(title, $"{_serverName}：{body}");
    }

    // MARK: 操作

    public void Cancel(TransferItem item)
    {
        if (item.State == TransferState.Waiting) item.SetState(TransferState.Cancelled);
        else if (item.State == TransferState.Running) item.Cancellation?.Cancel();
    }

    public void CancelAll()
    {
        foreach (var item in Items.ToList()) Cancel(item);
    }

    public void Retry(TransferItem item)
    {
        if (!item.CanRetry) return;
        item.Done = 0;
        item.SetState(TransferState.Waiting);
        Pump();
    }

    public void ClearFinished()
    {
        foreach (var item in Items.Where(i => !i.IsActive).ToList()) Items.Remove(item);
    }

    private void UpdateCounts()
    {
        ActiveCount = Items.Count(i => i.IsActive);
        HasItems = Items.Count > 0;
    }

    // MARK: 実行（UI スレッドで回し、通信は接続の中で非同期に行う）

    private async void Pump()
    {
        if (_running) return;
        _running = true;
        try
        {
            while (Items.FirstOrDefault(i => i.State == TransferState.Waiting) is { } next)
            {
                await RunAsync(next);
            }
        }
        finally
        {
            _running = false;
        }
        Idle?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunAsync(TransferItem item)
    {
        item.SetState(TransferState.Running);
        using var cancellation = new CancellationTokenSource();
        item.Cancellation = cancellation;
        // 進み具合は頻繁に届くので、画面の更新は 0.1 秒ごとに間引く
        var lastReport = 0L;
        TransferProgress progress = (done, total) =>
        {
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref lastReport) < 100) return;
            Interlocked.Exchange(ref lastReport, now);
            AppServices.OnUi(() =>
            {
                item.Done = done;
                if (total > 0) item.Total = total;
            });
        };
        var plan = item.Plan;
        var client = _client;
        try
        {
            if (plan.Direction == TransferDirection.Upload)
            {
                await client.UploadAsync(plan.LocalPath, plan.RemotePath, progress, cancellation.Token);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(plan.LocalPath)!);
                await client.DownloadAsync(plan.RemotePath, plan.LocalPath, plan.Size, progress, cancellation.Token);
                // 更新日時をサーバーと揃える（同期で「変更あり」と判定されないように）
                if (plan.SourceModified is { } modified) File.SetLastWriteTimeUtc(plan.LocalPath, modified);
            }
            item.Done = item.Total;
            item.SetState(TransferState.Finished);
        }
        catch (Exception e) when (cancellation.IsCancellationRequested || e is FerryException { Kind: FerryErrorKind.Cancelled })
        {
            item.SetState(TransferState.Cancelled);
        }
        catch (Exception e)
        {
            item.SetState(TransferState.Failed, BrowserViewModel.Describe(e));
        }
        finally
        {
            item.Cancellation = null;
        }
    }

    public async Task DisposeClientAsync()
    {
        CancelAll();
        await _client.DisposeAsync();
    }
}
