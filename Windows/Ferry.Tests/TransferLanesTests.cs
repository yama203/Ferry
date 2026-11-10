using Ferry.Core;
using Ferry.Core.Remote;

namespace Ferry.Tests;

/// <summary>転送用の偽の接続（転送はしない。つながるか・何本同時に動いているかだけ見る）</summary>
public sealed class LaneFakeClient(int number, Func<int, Exception?> connectError) : IRemoteClient
{
    public int Number { get; } = number;
    public ServerConfig Server { get; } = new() { Name = "fake", Host = "fake" };
    public string? Password { get; private set; }
    public bool Disposed { get; private set; }

    public void SetPassword(string? password) => Password = password;

    public Task<string> HomeDirectoryAsync(CancellationToken cancellation = default) =>
        connectError(Number) is { } error ? Task.FromException<string>(error) : Task.FromResult("/");

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    public Task<IReadOnlyList<RemoteItem>> ListAsync(string directory, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task DownloadAsync(string remotePath, string localPath, long? expectedSize = null, TransferProgress? progress = null,
                              CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task UploadAsync(string localPath, string remotePath, TransferProgress? progress = null,
                            CancellationToken cancellation = default, UploadOptions? options = null) => throw new NotSupportedException();
    public Task MakeDirectoryAsync(string path, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task DeleteFileAsync(string path, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task RemoveDirectoryAsync(string path, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task RenameAsync(string from, string to, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task ChangePermissionsAsync(string path, string mode, CancellationToken cancellation = default) => throw new NotSupportedException();
}

/// <summary>テスト用の待ち行列（アプリの TransferQueueViewModel にあたる）</summary>
public sealed class TestQueue : ITransferQueue<TestQueue.Job>
{
    public enum State { Waiting, Running, Finished, Cancelled, Failed }

    public sealed class Job(string name)
    {
        public string Name { get; } = name;
        public State State { get; set; } = State.Waiting;
        public CancellationTokenSource? Cancellation { get; set; }
        public List<int> Clients { get; } = [];
        public Exception? Error { get; set; }
        /// <summary>転送が 1 バイトでも進んだ（Transfer の中で立てる）</summary>
        public bool Progressed { get; set; }
    }

    private readonly object _lock = new();
    public List<Job> Jobs { get; } = [];
    /// <summary>1 件の転送（既定では少し待って成功）</summary>
    public Func<Job, LaneFakeClient, CancellationToken, Task> Transfer { get; set; } =
        (_, _, token) => Task.Delay(20, token);
    public int Running { get; private set; }
    public int MaxRunning { get; private set; }
    public TaskCompletionSource AllDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TestQueue(int count)
    {
        for (var i = 0; i < count; i++) Jobs.Add(new Job($"file{i}"));
    }

    public int WaitingCount
    {
        get { lock (_lock) return Jobs.Count(j => j.State == State.Waiting); }
    }

    public Job? TakeNext()
    {
        lock (_lock)
        {
            if (Jobs.FirstOrDefault(j => j.State == State.Waiting) is not { } job) return null;
            job.State = State.Running;
            job.Progressed = false;
            job.Cancellation = new CancellationTokenSource();
            Running++;
            MaxRunning = Math.Max(MaxRunning, Running);
            return job;
        }
    }

    public async Task RunAsync(Job job, IRemoteClient client)
    {
        var fake = (LaneFakeClient)client;
        lock (_lock) job.Clients.Add(fake.Number);
        try
        {
            await Transfer(job, fake, job.Cancellation!.Token);
        }
        finally
        {
            lock (_lock) Running--;
        }
    }

    public void Complete(Job job, Exception? error)
    {
        lock (_lock)
        {
            job.Error = error;
            job.State = error is null ? State.Finished
                : job.Cancellation!.IsCancellationRequested ? State.Cancelled
                : State.Failed;
            CheckDone();
        }
    }

    public bool HasProgressed(Job job) => job.Progressed;

    public bool TryRequeue(Job job)
    {
        lock (_lock)
        {
            if (job.Cancellation!.IsCancellationRequested) return false;
            job.State = State.Waiting;
            return true;
        }
    }

    public void Cancel(Job job)
    {
        lock (_lock)
        {
            if (job.State == State.Waiting)
            {
                job.State = State.Cancelled;
                CheckDone();
            }
            else if (job.State == State.Running)
            {
                job.Cancellation!.Cancel();
            }
        }
    }

    private void CheckDone()
    {
        if (Jobs.All(j => j.State is State.Finished or State.Cancelled or State.Failed)) AllDone.TrySetResult();
    }
}

public class TransferLanesTests
{
    private static (TransferLanes<TestQueue.Job> Lanes, List<LaneFakeClient> Clients) Make(
        TestQueue queue, int max, Func<int, Exception?>? connectError = null)
    {
        var clients = new List<LaneFakeClient>();
        var lanes = new TransferLanes<TestQueue.Job>(() =>
        {
            lock (clients)
            {
                var client = new LaneFakeClient(clients.Count, connectError ?? (_ => null));
                clients.Add(client);
                return client;
            }
        }, queue) { MaxConcurrency = () => max };
        return (lanes, clients);
    }

    private static async Task WaitAsync(Task task) =>
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))));

    [Fact]
    public async Task 同時に動く数は設定の数を超えず全部終わる()
    {
        var queue = new TestQueue(20);
        var (lanes, clients) = Make(queue, 3);
        var idle = 0;
        lanes.Idle += (_, _) => Interlocked.Increment(ref idle);

        lanes.Pump();
        await WaitAsync(queue.AllDone.Task);

        Assert.All(queue.Jobs, j => Assert.Equal(TestQueue.State.Finished, j.State));
        Assert.Equal(3, queue.MaxRunning);
        Assert.Equal(3, clients.Count);
        // 全部の接続が空いたときに 1 回だけ
        await Task.Delay(50);
        Assert.Equal(1, idle);
    }

    [Fact]
    public async Task 一件だけなら接続は一本しか作らない()
    {
        var queue = new TestQueue(1);
        var (lanes, clients) = Make(queue, 3);

        lanes.Pump();
        await WaitAsync(queue.AllDone.Task);

        Assert.Single(clients);
        Assert.Equal(TestQueue.State.Finished, queue.Jobs[0].State);
    }

    [Fact]
    public async Task 二本なら二本だけ作る()
    {
        var queue = new TestQueue(2);
        var (lanes, clients) = Make(queue, 4);

        lanes.Pump();
        await WaitAsync(queue.AllDone.Task);

        Assert.Equal(2, clients.Count);
    }

    [Fact]
    public async Task 二本目以降がつながらなくても失敗にせず残りの接続で全部終わる()
    {
        var queue = new TestQueue(12);
        // 3 本目（番号 2）は接続数の上限で断られる
        var (lanes, clients) = Make(queue, 4, n => n >= 2 ? FerryException.TooManyConnections() : null);

        lanes.Pump();
        await WaitAsync(queue.AllDone.Task);

        Assert.All(queue.Jobs, j => Assert.Equal(TestQueue.State.Finished, j.State));
        Assert.All(queue.Jobs, j => Assert.All(j.Clients, c => Assert.True(c < 2)));
        Assert.Equal(2, lanes.ConnectionCount);
        Assert.True(clients[2].Disposed);

        // このあとは増やさない（断られた接続を何度も試さない）
        var created = clients.Count;
        queue.Jobs.Add(new TestQueue.Job("more1"));
        queue.Jobs.Add(new TestQueue.Job("more2"));
        queue.Jobs.Add(new TestQueue.Job("more3"));
        lanes.Pump();
        await Task.Delay(300);
        Assert.All(queue.Jobs, j => Assert.Equal(TestQueue.State.Finished, j.State));
        Assert.Equal(created, clients.Count);
    }

    [Fact]
    public async Task 二本目がまだ何も送らないうちにつなぎ直せないときは待ちに戻して別の接続で送る()
    {
        var queue = new TestQueue(6);
        var (lanes, clients) = Make(queue, 2);
        var refused = 0;
        queue.Transfer = async (job, client, token) =>
        {
            await Task.Delay(20, token);
            // 2 本目の最初の転送で、サーバーに断られた（接続が切れてつなぎ直したら上限だった、など）
            if (client.Number == 1 && Interlocked.Increment(ref refused) == 1) throw FerryException.ConnectionRefused("fake", 21);
        };

        lanes.Pump();
        await WaitAsync(queue.AllDone.Task);

        Assert.All(queue.Jobs, j => Assert.Equal(TestQueue.State.Finished, j.State));
        Assert.Contains(queue.Jobs, j => j.Clients.SequenceEqual([1, 0]));
        Assert.Equal(1, lanes.ConnectionCount);
    }

    [Fact]
    public async Task 二本目以降が少し送った後に接続が切れたら項目の失敗で接続は減らさない()
    {
        var queue = new TestQueue(8);
        var (lanes, clients) = Make(queue, 3);
        var broken = 0;
        queue.Transfer = async (job, client, token) =>
        {
            await Task.Delay(20, token);
            if (client.Number == 2 && Interlocked.Increment(ref broken) == 1)
            {
                // 途中まで送ったところで、回線が一時的に切れた
                job.Progressed = true;
                throw FerryException.Timeout();
            }
        };

        lanes.Pump();
        await WaitAsync(queue.AllDone.Task);

        var failed = Assert.Single(queue.Jobs, j => j.State == TestQueue.State.Failed);
        Assert.Equal([2], failed.Clients);
        Assert.Equal(7, queue.Jobs.Count(j => j.State == TestQueue.State.Finished));
        Assert.Equal(3, lanes.ConnectionCount);
        Assert.False(clients[2].Disposed);

        // 上限も下げていないので、次の項目も同じ本数で進む
        var before = clients.Count;
        for (var i = 0; i < 6; i++) queue.Jobs.Add(new TestQueue.Job($"next{i}"));
        var running = 0;
        var maxRunning = 0;
        queue.Transfer = async (_, _, token) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            await Task.Delay(30, token);
            Interlocked.Decrement(ref running);
        };
        lanes.Pump();
        await Task.Delay(400);
        Assert.All(queue.Jobs.Where(j => j.Name.StartsWith("next")), j => Assert.Equal(TestQueue.State.Finished, j.State));
        Assert.Equal(3, maxRunning);
        Assert.Equal(before, clients.Count);
    }

    [Fact]
    public async Task 一本目の接続の失敗は今までどおり項目の失敗()
    {
        var queue = new TestQueue(1);
        var (lanes, _) = Make(queue, 3);
        queue.Transfer = (_, _, _) => throw FerryException.LoginFailed();

        lanes.Pump();
        await WaitAsync(queue.AllDone.Task);

        Assert.Equal(TestQueue.State.Failed, queue.Jobs[0].State);
        Assert.Equal(FerryErrorKind.LoginFailed, ((FerryException)queue.Jobs[0].Error!).Kind);
    }

    [Fact]
    public async Task 転送の途中の失敗はその項目の失敗で接続は減らさない()
    {
        var queue = new TestQueue(6);
        var (lanes, _) = Make(queue, 3);
        queue.Transfer = async (job, _, token) =>
        {
            await Task.Delay(10, token);
            if (job.Name is "file1" or "file2" or "file4") throw FerryException.PermissionDenied();
        };

        lanes.Pump();
        await WaitAsync(queue.AllDone.Task);

        Assert.Equal(["file1", "file2", "file4"], queue.Jobs.Where(j => j.State == TestQueue.State.Failed).Select(j => j.Name));
        Assert.Equal(3, lanes.ConnectionCount);
    }

    [Fact]
    public async Task 全部中止すると転送中も待ちも止まる()
    {
        var queue = new TestQueue(10);
        var (lanes, _) = Make(queue, 3);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Transfer = async (_, _, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var idle = 0;
        lanes.Idle += (_, _) => Interlocked.Increment(ref idle);

        lanes.Pump();
        await WaitAsync(started.Task);
        await Task.Delay(50);
        foreach (var job in queue.Jobs.ToList()) queue.Cancel(job);
        await WaitAsync(queue.AllDone.Task);

        Assert.All(queue.Jobs, j => Assert.Equal(TestQueue.State.Cancelled, j.State));
        Assert.Equal(3, queue.Jobs.Count(j => j.Clients.Count > 0));
        await Task.Delay(50);
        Assert.Equal(1, idle);
    }

    [Fact]
    public async Task 中止した項目は接続の失敗でも待ちに戻さない()
    {
        var queue = new TestQueue(2);
        var (lanes, _) = Make(queue, 2);
        queue.Transfer = async (job, client, token) =>
        {
            await Task.Delay(30, CancellationToken.None);
            if (client.Number == 1)
            {
                queue.Cancel(job);
                throw FerryException.Timeout();
            }
        };

        lanes.Pump();
        await WaitAsync(queue.AllDone.Task);

        Assert.Single(queue.Jobs, j => j.State == TestQueue.State.Cancelled);
        Assert.Equal(2, lanes.ConnectionCount);
    }

    [Fact]
    public async Task 後から設定したパスワードは後で作る接続にも使う()
    {
        var queue = new TestQueue(3);
        var (lanes, clients) = Make(queue, 3);
        lanes.SetPassword("secret");

        lanes.Pump();
        await WaitAsync(queue.AllDone.Task);

        Assert.Equal(3, clients.Count);
        Assert.All(clients, c => Assert.Equal("secret", c.Password));
    }

    [Fact]
    public async Task 接続設定を変えると古い接続は全部閉じて新しい接続で続ける()
    {
        var queue = new TestQueue(3);
        var (lanes, clients) = Make(queue, 3);
        lanes.Pump();
        await WaitAsync(queue.AllDone.Task);
        var old = clients.ToList();

        var replaced = new List<LaneFakeClient>();
        await lanes.ReplaceAsync(() =>
        {
            var client = new LaneFakeClient(100 + replaced.Count, _ => null);
            lock (replaced) replaced.Add(client);
            return client;
        });
        Assert.All(old, c => Assert.True(c.Disposed));

        queue.Jobs.Add(new TestQueue.Job("after"));
        lanes.Pump();
        await Task.Delay(200);
        Assert.Equal(TestQueue.State.Finished, queue.Jobs[^1].State);
        Assert.Equal([100], queue.Jobs[^1].Clients);
    }

    [Fact]
    public async Task 同時に転送する数を減らすと次の項目から減る()
    {
        var queue = new TestQueue(12);
        var max = 3;
        var clients = new List<LaneFakeClient>();
        var lanes = new TransferLanes<TestQueue.Job>(() =>
        {
            lock (clients)
            {
                var c = new LaneFakeClient(clients.Count, _ => null);
                clients.Add(c);
                return c;
            }
        }, queue) { MaxConcurrency = () => Volatile.Read(ref max) };
        var concurrentAfter = 0;
        var running = 0;
        queue.Transfer = async (job, _, token) =>
        {
            var now = Interlocked.Increment(ref running);
            if (job.Name is not ("file0" or "file1" or "file2" or "file3" or "file4" or "file5"))
                InterlockedMax(ref concurrentAfter, now);
            await Task.Delay(30, token);
            Interlocked.Decrement(ref running);
        };

        lanes.Pump();
        await Task.Delay(45);
        Volatile.Write(ref max, 1);
        await WaitAsync(queue.AllDone.Task);

        Assert.All(queue.Jobs, j => Assert.Equal(TestQueue.State.Finished, j.State));
        Assert.True(concurrentAfter <= 2, $"減らした後も {concurrentAfter} 件同時に動いた");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
}
