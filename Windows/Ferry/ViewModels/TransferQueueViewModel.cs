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
    /// <summary>今の転送が 1 バイトでも進んだか（画面用に間引く前の進み具合。別のスレッドから書かれる）</summary>
    private volatile bool _progressed;
    public bool HasProgressed
    {
        get => _progressed;
        set => _progressed = value;
    }
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
/// 転送の待ち行列。転送は専用の接続（設定の「同時に転送する数」まで）で先頭から順に同時に処理し、
/// フォルダの中身の一覧は一覧表示用の接続で取る（転送中でもフォルダを移動できるように）。
/// 接続の増やし方・減らし方は Core の TransferLanes に任せる。
/// </summary>
public sealed partial class TransferQueueViewModel : ObservableObject, ITransferQueue<TransferItem>
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

    private readonly TransferLanes<TransferItem> _lanes;
    private readonly Func<IRemoteClient> _lister;
    private readonly string _serverName;

    /// <summary>createClient は転送用の接続を作る（2 本目以降は必要になったときに呼ぶ）</summary>
    public TransferQueueViewModel(Func<IRemoteClient> createClient, Func<IRemoteClient> lister, string serverName)
    {
        _lanes = new TransferLanes<TransferItem>(createClient, this)
        {
            MaxConcurrency = () => AppServices.Settings.ConcurrentTransfers,
        };
        _lanes.Idle += (_, _) => Idle?.Invoke(this, EventArgs.Empty);
        _lister = lister;
        _serverName = serverName;
        Items.CollectionChanged += (_, _) => UpdateCounts();
    }

    public string SummaryText => IsPreparing ? "転送するファイルを調べています…"
        : ActiveCount > 0 ? $"残り {ActiveCount} 件" : "";

    private static ExcludeRules Rules => AppServices.Store.ExcludeRules;

    /// <summary>接続時に入力されたパスワード（保存しなかった場合も転送に使う）</summary>
    public void SetPassword(string? password) => _lanes.SetPassword(password);

    /// <summary>接続設定が変わったとき。転送中のものは止め、転送用の接続を全部作り直す</summary>
    public async Task ReplaceClientAsync(Func<IRemoteClient> createClient)
    {
        CancelAll();
        await _lanes.ReplaceAsync(createClient);
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

    /// <summary>
    /// エディタで保存されたファイルをそのままアップロードする（確認なし。失敗したときの知らせは呼び出し側で出す）。
    /// exists・permissions はサーバー側の今のファイル（一時ファイルから置き換えるか、置き換えた後の権限）
    /// </summary>
    public TransferItem UploadEdited(string localPath, string remotePath, bool exists, string? permissions)
    {
        // 保存の直後に消された・置き換え中などで大きさを読めなくても、ここでは落とさない（アップロードの失敗として出る）
        long size = 0;
        try { size = new FileInfo(localPath).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var plan = new PlannedTransfer
        {
            Direction = TransferDirection.Upload, LocalPath = localPath, RemotePath = remotePath, Size = size,
            DestinationExists = exists, DestinationPermissions = permissions,
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

    // MARK: 実行（UI スレッドで回し、通信は接続の中で非同期に行う。await の続きも UI スレッドに戻る）

    private void Pump() => _lanes.Pump();

    int ITransferQueue<TransferItem>.WaitingCount => Items.Count(i => i.State == TransferState.Waiting);

    TransferItem? ITransferQueue<TransferItem>.TakeNext()
    {
        if (Items.FirstOrDefault(i => i.State == TransferState.Waiting) is not { } next) return null;
        next.Cancellation = new CancellationTokenSource();
        next.HasProgressed = false;
        next.SetState(TransferState.Running);
        return next;
    }

    async Task ITransferQueue<TransferItem>.RunAsync(TransferItem item, IRemoteClient client)
    {
        var cancellation = item.Cancellation!;
        // 進み具合は頻繁に届くので、画面の更新は 0.1 秒ごとに間引く
        var lastReport = 0L;
        TransferProgress progress = (done, total) =>
        {
            // 接続を減らすかの判断に使うので、間引く前に覚える
            if (done > 0) item.HasProgressed = true;
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref lastReport) < 100) return;
            Interlocked.Exchange(ref lastReport, now);
            AppServices.OnUi(() =>
            {
                // 待ちに戻した後に遅れて届いた分は捨てる
                if (item.State != TransferState.Running || item.Cancellation != cancellation) return;
                item.Done = done;
                if (total > 0) item.Total = total;
            });
        };
        var plan = item.Plan;
        if (plan.Direction == TransferDirection.Upload)
        {
            // サーバーにすでにあるファイルだけ、一時ファイルに送ってから置き換える（新しいファイルは壊れるものが無いので直接）
            var options = new UploadOptions
            {
                ViaTemporaryFile = AppServices.Settings.UploadViaTemporaryFile && plan.DestinationExists,
                Permissions = plan.DestinationPermissions,
            };
            await client.UploadAsync(plan.LocalPath, plan.RemotePath, progress, cancellation.Token, options);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(plan.LocalPath)!);
            await client.DownloadAsync(plan.RemotePath, plan.LocalPath, plan.Size, progress, cancellation.Token);
            // 更新日時をサーバーと揃える（同期で「変更あり」と判定されないように）
            if (plan.SourceModified is { } modified) File.SetLastWriteTimeUtc(plan.LocalPath, modified);
        }
    }

    void ITransferQueue<TransferItem>.Complete(TransferItem item, Exception? error)
    {
        var cancelled = item.Cancellation?.IsCancellationRequested == true;
        item.Cancellation?.Dispose();
        item.Cancellation = null;
        if (error is null)
        {
            item.Done = item.Total;
            item.SetState(TransferState.Finished);
        }
        else if (cancelled || error is FerryException { Kind: FerryErrorKind.Cancelled })
        {
            item.SetState(TransferState.Cancelled);
        }
        else
        {
            item.SetState(TransferState.Failed, BrowserViewModel.Describe(error));
        }
    }

    bool ITransferQueue<TransferItem>.HasProgressed(TransferItem item) => item.HasProgressed;

    bool ITransferQueue<TransferItem>.TryRequeue(TransferItem item)
    {
        if (item.Cancellation?.IsCancellationRequested == true) return false;
        item.Cancellation?.Dispose();
        item.Cancellation = null;
        item.Done = 0;
        item.SetState(TransferState.Waiting);
        return true;
    }

    public async Task DisposeClientAsync()
    {
        CancelAll();
        await _lanes.DisposeAsync();
    }
}
