using Ferry.Core.Remote;

namespace Ferry.Core;

/// <summary>転送の待ち行列のうち、TransferLanes が使うところ（アプリでは転送一覧の ViewModel）</summary>
public interface ITransferQueue<TJob> where TJob : class
{
    /// <summary>待っている項目の数</summary>
    int WaitingCount { get; }
    /// <summary>待っている先頭の項目を取り出して「転送中」にする。無ければ null</summary>
    TJob? TakeNext();
    /// <summary>1 件を転送する。失敗は例外で返す</summary>
    Task RunAsync(TJob job, IRemoteClient client);
    /// <summary>転送が終わった（error が null なら成功）。状態（完了・中止・失敗）は呼び出し側が決める</summary>
    void Complete(TJob job, Exception? error);
    /// <summary>
    /// その転送が 1 バイトでも進んだか（画面用に間引く前の進み具合で答える）。進んだ後の失敗は、
    /// つなぎ直しで断られたのではなく転送の途中の不調なので、接続を減らさずにその項目の失敗にする
    /// </summary>
    bool HasProgressed(TJob job);
    /// <summary>接続できなかった項目を待ちに戻す。中止された項目なら戻さずに false</summary>
    bool TryRequeue(TJob job);
}

/// <summary>
/// 転送の待ち行列を、転送用の接続を何本か使って同時に進める（画面に依存しない部分）。
/// 待っている項目を先頭から取り出し、同時に最大 MaxConcurrency 件まで転送する。
/// 1 本目の接続は最初から持ち、2 本目以降は同時に転送するものがあるときに作る（1 件だけなら 1 本しかつながない）。
/// 共用サーバーには同時接続数の制限があるので、2 本目以降がつながらない・ログインできないときは、
/// 項目を「失敗」にせず待ちに戻し、その接続をやめて残りの接続で続ける（このあとは増やさない）。
/// 転送中の接続系の失敗で待ちに戻すのは、まだ 1 バイトも進んでいない（つなぎ直しで断られた）ときだけ。
/// 進んだ後の失敗（回線が一時的に切れたなど）と 1 本目の失敗は、今までどおり項目の失敗で、接続は減らさない。
/// Pump とコールバックはアプリでは UI スレッドから来る（await の続きも UI スレッドに戻る）が、テストのため lock で守る。
/// </summary>
public sealed class TransferLanes<TJob> : IAsyncDisposable where TJob : class
{
    /// <summary>設定で選べる最大の数</summary>
    public const int MaxLanes = 4;

    private sealed class Lane(IRemoteClient client, bool isPrimary)
    {
        public IRemoteClient Client { get; } = client;
        public bool IsPrimary { get; } = isPrimary;
        /// <summary>2 本目以降は、項目を取る前につながるかを確かめる</summary>
        public bool NeedsConnect { get; set; } = !isPrimary;
        /// <summary>転送の繰り返しを回している</summary>
        public bool Active { get; set; }
        public bool HasJob { get; set; }
        /// <summary>やめた（つながらなかった・接続設定が変わった）</summary>
        public bool Retired { get; set; }
    }

    private readonly ITransferQueue<TJob> _queue;
    private readonly object _lock = new();
    private readonly List<Lane> _lanes = [];
    private Func<IRemoteClient> _createClient;
    /// <summary>2 本目以降がつながらなかったときに下げる、使える接続の数</summary>
    private int _limit = MaxLanes;
    /// <summary>後から設定されたパスワード（後で作る接続にも使う）</summary>
    private (bool IsSet, string? Value) _password;
    private int _activeLoops;
    private bool _disposed;
    private CancellationTokenSource _connecting = new();

    public TransferLanes(Func<IRemoteClient> createClient, ITransferQueue<TJob> queue)
    {
        _createClient = createClient;
        _queue = queue;
        _lanes.Add(new Lane(createClient(), isPrimary: true));
    }

    /// <summary>同時に転送する数（1〜4 に丸める）。毎回読むので、設定を変えたら次に始まる転送から効く</summary>
    public Func<int> MaxConcurrency { get; set; } = () => 1;

    /// <summary>全部の接続が空いたとき（1 回の Pump で始めた転送が全部終わったら 1 回）</summary>
    public event EventHandler? Idle;

    /// <summary>今ある転送用の接続の数（テスト用）</summary>
    public int ConnectionCount
    {
        get { lock (_lock) return _lanes.Count; }
    }

    /// <summary>接続できなかったときに、項目の失敗にせず接続を減らす種類のエラーか</summary>
    public static bool IsConnectionProblem(Exception e) => e is FerryException
    {
        Kind: FerryErrorKind.LoginFailed or FerryErrorKind.ConnectionRefused or FerryErrorKind.HostNotFound
            or FerryErrorKind.Timeout or FerryErrorKind.TooManyConnections,
    };

    public void SetPassword(string? password)
    {
        List<Lane> lanes;
        lock (_lock)
        {
            _password = (true, password);
            lanes = _lanes.ToList();
        }
        foreach (var lane in lanes) lane.Client.SetPassword(password);
    }

    /// <summary>待っている項目があれば、空いている接続（足りなければ新しい接続）で転送を始める</summary>
    public void Pump()
    {
        var start = new List<Lane>();
        lock (_lock)
        {
            if (_disposed) return;
            while (true)
            {
                var active = _lanes.Count(l => l.Active);
                if (active >= Max()) break;
                // 動き始めたがまだ項目を取っていない接続の分は、すでに取り手がいる
                var wanting = _lanes.Count(l => l.Active && !l.HasJob);
                if (_queue.WaitingCount <= wanting) break;
                // 空いている接続を先に使う（1 本目から）
                var lane = _lanes.FirstOrDefault(l => !l.Active);
                if (lane is null)
                {
                    lane = new Lane(_createClient(), isPrimary: false);
                    if (_password.IsSet) lane.Client.SetPassword(_password.Value);
                    _lanes.Add(lane);
                }
                lane.Active = true;
                _activeLoops++;
                start.Add(lane);
            }
        }
        foreach (var lane in start) _ = RunLaneAsync(lane);
    }

    private int Max() => Math.Min(Math.Clamp(MaxConcurrency(), 1, MaxLanes), _limit);

    private async Task RunLaneAsync(Lane lane)
    {
        if (lane.NeedsConnect)
        {
            try
            {
                // ログインの確認を兼ねる（転送の途中の失敗と、つながらない失敗を分けるため、項目を取る前につなぐ）
                await lane.Client.HomeDirectoryAsync(_connecting.Token);
                lane.NeedsConnect = false;
            }
            catch (Exception)
            {
                await RetireAsync(lane);
                return;
            }
        }
        while (TryTake(lane) is { } job)
        {
            Exception? error = null;
            try
            {
                await _queue.RunAsync(job, lane.Client);
            }
            catch (Exception e)
            {
                error = e;
            }
            lock (_lock) lane.HasJob = false;
            if (error is not null && !lane.IsPrimary && IsConnectionProblem(error) && !_queue.HasProgressed(job)
                && _queue.TryRequeue(job))
            {
                // まだ何も送れないうちに、つなぎ直したところで断られた（接続数の上限など）。項目は残りの接続で転送する
                await RetireAsync(lane);
                return;
            }
            _queue.Complete(job, error);
        }
    }

    /// <summary>次の項目を取る。取らない（無い・数を減らした・やめた）ときは、この接続の繰り返しを終える</summary>
    private TJob? TryTake(Lane lane)
    {
        bool idle;
        lock (_lock)
        {
            if (!lane.Retired && !_disposed && _lanes.Count(l => l.Active) <= Max() && _queue.TakeNext() is { } job)
            {
                lane.HasJob = true;
                return job;
            }
            idle = Deactivate(lane);
        }
        if (idle) Idle?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>lock の中で呼ぶ。全部の接続が空いたら true</summary>
    private bool Deactivate(Lane lane)
    {
        if (!lane.Active) return false;
        lane.Active = false;
        lane.HasJob = false;
        _activeLoops--;
        return _activeLoops == 0;
    }

    /// <summary>2 本目以降の接続をやめる。残りの接続で続ける（このセッションの間は増やさない）</summary>
    private async Task RetireAsync(Lane lane)
    {
        bool idle;
        lock (_lock)
        {
            if (_lanes.Remove(lane)) _limit = Math.Max(1, _lanes.Count);
            lane.Retired = true;
            idle = Deactivate(lane);
        }
        // 待ちに戻した項目を、空いている接続で始める
        Pump();
        lock (_lock) idle = idle && _activeLoops == 0;
        if (idle) Idle?.Invoke(this, EventArgs.Empty);
        await DisposeQuietlyAsync(lane.Client);
    }

    /// <summary>接続設定が変わったとき。転送中のものは呼び出し側で先に止めておく</summary>
    public async Task ReplaceAsync(Func<IRemoteClient> createClient)
    {
        List<Lane> old;
        lock (_lock)
        {
            old = _lanes.ToList();
            foreach (var lane in old) lane.Retired = true;
            _lanes.Clear();
            _createClient = createClient;
            _lanes.Add(new Lane(createClient(), isPrimary: true));
            // 新しい接続設定では、つながる数も変わりうる
            _limit = MaxLanes;
            _password = default;
            _connecting.Cancel();
            _connecting = new CancellationTokenSource();
        }
        foreach (var lane in old) await DisposeQuietlyAsync(lane.Client);
    }

    public async ValueTask DisposeAsync()
    {
        List<Lane> lanes;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _connecting.Cancel();
            lanes = _lanes.ToList();
            _lanes.Clear();
        }
        foreach (var lane in lanes) await DisposeQuietlyAsync(lane.Client);
    }

    private static async Task DisposeQuietlyAsync(IRemoteClient client)
    {
        try
        {
            await client.DisposeAsync();
        }
        catch (Exception) { }
    }
}
