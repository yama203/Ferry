using Ferry.Core.Remote;

namespace Ferry.Core;

/// <summary>
/// 「FTPS に切り替えられる接続先を調べる」画面の中身。暗号化なしの FTP の接続先（自分のもの・会社の共有の両方）を並べ、
/// 順に調べて、選んだものを FTPS（Explicit）に切り替える。画面に依存しないようにここに置く
/// </summary>
public sealed class FtpsSurvey
{
    public enum EntryState { NotChecked, Checking, Done }

    /// <summary>一覧の 1 件</summary>
    public sealed class Entry
    {
        public required ServerConfig Server { get; init; }
        /// <summary>会社の共有の接続先（切り替えると全員の設定が変わる）</summary>
        public bool IsShared { get; init; }
        public EntryState State { get; internal set; }
        public FtpsCheckResult? Result { get; internal set; }

        private bool _isSelected;

        /// <summary>切り替える対象に選んだか（切り替えられるものだけ選べる）</summary>
        public bool IsSelected
        {
            get => _isSelected && CanSelect;
            set => _isSelected = value;
        }

        public bool CanSelect => State == EntryState.Done && Result is { CanSwitch: true };

        /// <summary>切り替えた後の設定（証明書を検証できないものは、検証しない FTPS にする）</summary>
        public ServerConfig? SwitchedServer(ServerConfig current) => Result?.Outcome switch
        {
            FtpsCheckOutcome.Switchable => current with { ServerProtocol = ServerProtocol.Ftps, VerifyCertificate = true },
            FtpsCheckOutcome.CertificateUnverified => current with { ServerProtocol = ServerProtocol.Ftps, VerifyCertificate = false },
            _ => null,
        };
    }

    public IReadOnlyList<Entry> Entries { get; }

    /// <summary>1 件の状態が変わったとき（調べ始めた・結果が出た）。調べ始めたスレッドで届く</summary>
    public event EventHandler<Entry>? EntryChanged;

    public FtpsSurvey(IEnumerable<Entry> entries)
    {
        Entries = entries.ToList();
    }

    /// <summary>接続先の一覧から、暗号化なしの FTP のものを集める（会社の共有を先に、名前の順）</summary>
    public static FtpsSurvey FromStore(ServerStore store) =>
        new(store.Servers
            .Where(s => s.ServerProtocol == ServerProtocol.Ftp)
            .Select(s => new Entry { Server = s, IsShared = store.IsShared(s.Id) })
            .OrderBy(e => e.IsShared ? 0 : 1)
            .ThenBy(e => e.Server.Name, SyncPlanner.NaturalOrder));

    public bool IsRunning { get; private set; }
    public bool HasChecked => Entries.Any(e => e.State == EntryState.Done);
    public bool HasSharedSelected => Entries.Any(e => e.IsSelected && e.IsShared);
    public int SelectedCount => Entries.Count(e => e.IsSelected);

    /// <summary>
    /// まだ調べていないものを順に調べる（同時に concurrency 件まで）。切り替えられるものは選んだ状態にし、
    /// 証明書を検証できないものは選ばない（なりすましは防げないので、ユーザーが選んだときだけ切り替える）
    /// </summary>
    public async Task RunAsync(Func<ServerConfig, CancellationToken, Task<FtpsCheckResult>> check, int concurrency = 3,
                               CancellationToken cancellation = default)
    {
        if (IsRunning) return;
        IsRunning = true;
        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        try
        {
            await Task.WhenAll(Entries.Where(e => e.State == EntryState.NotChecked).ToList().Select(async entry =>
            {
                await gate.WaitAsync(cancellation);
                try
                {
                    entry.State = EntryState.Checking;
                    EntryChanged?.Invoke(this, entry);
                    FtpsCheckResult result;
                    try
                    {
                        result = await check(entry.Server, cancellation);
                    }
                    catch (OperationCanceledException)
                    {
                        // 中止したものは、もう一度「調べる」で調べられるように戻す
                        entry.State = EntryState.NotChecked;
                        EntryChanged?.Invoke(this, entry);
                        throw;
                    }
                    catch (Exception e)
                    {
                        result = new FtpsCheckResult(FtpsCheckOutcome.Failed, e.Message);
                    }
                    if (cancellation.IsCancellationRequested && result.Outcome == FtpsCheckOutcome.Failed)
                    {
                        entry.State = EntryState.NotChecked;
                        EntryChanged?.Invoke(this, entry);
                        return;
                    }
                    entry.Result = result;
                    entry.State = EntryState.Done;
                    entry.IsSelected = result.Outcome == FtpsCheckOutcome.Switchable;
                    EntryChanged?.Invoke(this, entry);
                }
                finally
                {
                    gate.Release();
                }
            }));
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>
    /// 選んだ接続先を FTPS（Explicit）にして保存する（ポートはそのまま）。戻り値は切り替えたもの。
    /// 調べた後に他の人（他の PC）が接続先を変えた・消した場合は、調べた内容と違うので切り替えない
    /// </summary>
    public IReadOnlyList<ServerConfig> Apply(ServerStore store)
    {
        var switched = new List<ServerConfig>();
        foreach (var entry in Entries.Where(e => e.IsSelected))
        {
            if (store.Server(entry.Server.Id) is not { ServerProtocol: ServerProtocol.Ftp } current
                || !current.IsSameDestination(entry.Server))
                continue;
            if (entry.SwitchedServer(current) is not { } updated) continue;
            store.Upsert(updated);
            switched.Add(updated);
        }
        return switched;
    }
}
