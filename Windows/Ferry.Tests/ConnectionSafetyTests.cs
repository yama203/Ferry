using System.Net.Security;
using Ferry.Core;
using Ferry.Core.Remote;

namespace Ferry.Tests;

/// <summary>FTPS への切り替えの案内と、SFTP のホスト鍵が変わったときの承認</summary>
public class ConnectionSafetyTests
{
    // MARK: 調べた結果の分類

    [Fact]
    public void 証明書を検証できないエラーは理由付きで検証できないに分類する()
    {
        var error = FerryException.UntrustedCertificate("x", null,
            SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors);
        var result = FtpsCheck.Classify(error);
        Assert.Equal(FtpsCheckOutcome.CertificateUnverified, result.Outcome);
        Assert.Contains("ホスト名", result.Reason);
        Assert.Contains("自己署名", result.Reason);
        Assert.True(result.IsNameMismatch);
        Assert.Contains("sv1234.xserver.jp", result.Advice);
        Assert.True(result.CanSwitch);
    }

    [Fact]
    public void 自己署名だけのときはホスト名を変える案内を出さない()
    {
        var result = FtpsCheck.Classify(FerryException.UntrustedCertificate("x", null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(result.IsNameMismatch);
        Assert.DoesNotContain("xserver", result.Advice);
    }

    [Theory]
    [InlineData(FerryErrorKind.FtpsNotSupported, FtpsCheckOutcome.NotSupported)]
    [InlineData(FerryErrorKind.LoginFailed, FtpsCheckOutcome.Failed)]
    [InlineData(FerryErrorKind.ConnectionRefused, FtpsCheckOutcome.Failed)]
    [InlineData(FerryErrorKind.HostNotFound, FtpsCheckOutcome.Failed)]
    [InlineData(FerryErrorKind.Timeout, FtpsCheckOutcome.Failed)]
    [InlineData(FerryErrorKind.DataConnectionFailed, FtpsCheckOutcome.Failed)]
    [InlineData(FerryErrorKind.TooManyConnections, FtpsCheckOutcome.Failed)]
    [InlineData(FerryErrorKind.Cancelled, FtpsCheckOutcome.Failed)]
    [InlineData(FerryErrorKind.Other, FtpsCheckOutcome.Failed)]
    public void エラーの種類から結果を決める(FerryErrorKind kind, FtpsCheckOutcome expected)
    {
        var result = FtpsCheck.Classify(new FerryException(kind, "サーバーの説明"));
        Assert.Equal(expected, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.Reason));
        Assert.False(result.CanSwitch);
    }

    // MARK: 調べる処理（接続は偽物）

    /// <summary>接続の代わり。決めた設定のときに決めたエラーを出す</summary>
    private sealed class FakeClient(ServerConfig server, Func<ServerConfig, FerryException?> fail) : IRemoteClient
    {
        public ServerConfig Server { get; } = server;
        public bool Disposed { get; private set; }
        public void SetPassword(string? password) { }
        public Task<string> HomeDirectoryAsync(CancellationToken cancellation = default) =>
            fail(Server) is { } e ? Task.FromException<string>(e) : Task.FromResult("/home");
        public Task<IReadOnlyList<RemoteItem>> ListAsync(string directory, CancellationToken cancellation = default) =>
            Task.FromResult<IReadOnlyList<RemoteItem>>([]);
        public Task DownloadAsync(string remotePath, string localPath, long? expectedSize = null, TransferProgress? progress = null,
                                  CancellationToken cancellation = default) => throw new NotSupportedException();
        public Task UploadAsync(string localPath, string remotePath, TransferProgress? progress = null,
                                CancellationToken cancellation = default, UploadOptions? options = null) => throw new NotSupportedException();
        public Task MakeDirectoryAsync(string path, CancellationToken cancellation = default) => throw new NotSupportedException();
        public Task DeleteFileAsync(string path, CancellationToken cancellation = default) => throw new NotSupportedException();
        public Task RemoveDirectoryAsync(string path, CancellationToken cancellation = default) => throw new NotSupportedException();
        public Task RenameAsync(string from, string to, CancellationToken cancellation = default) => throw new NotSupportedException();
        public Task ChangePermissionsAsync(string path, string mode, CancellationToken cancellation = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static readonly ServerConfig Ftp = new()
    {
        Name = "例", Host = "example.jp", Port = 2121, Username = "u", ServerProtocol = ServerProtocol.Ftp, PassiveMode = false,
    };

    private static async Task<(FtpsCheckResult Result, List<FakeClient> Clients)> Check(
        Func<ServerConfig, FerryException?> fail, ServerConfig? server = null, string? password = "p")
    {
        var clients = new List<FakeClient>();
        var result = await FtpsCheck.CheckAsync(server ?? Ftp, password, default, (s, _) =>
        {
            var client = new FakeClient(s, fail);
            clients.Add(client);
            return client;
        });
        return (result, clients);
    }

    [Fact]
    public async Task 証明書も検証できれば切り替えられる()
    {
        var (result, clients) = await Check(_ => null);
        Assert.Equal(FtpsCheckOutcome.Switchable, result.Outcome);
        var client = Assert.Single(clients);
        // 方式だけを変え、ホスト・ポート・ユーザー名・パッシブモードはそのまま。証明書は検証する
        Assert.Equal(Ftp with { ServerProtocol = ServerProtocol.Ftps, VerifyCertificate = true }, client.Server);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task 証明書を検証できなければ検証なしでつながるかも確かめる()
    {
        var (result, clients) = await Check(s => s.VerifyCertificate
            ? FerryException.UntrustedCertificate("x", null, SslPolicyErrors.RemoteCertificateNameMismatch)
            : null);
        Assert.Equal(FtpsCheckOutcome.CertificateUnverified, result.Outcome);
        Assert.True(result.IsNameMismatch);
        Assert.Equal(2, clients.Count);
        Assert.False(clients[1].Server.VerifyCertificate);
        Assert.All(clients, c => Assert.True(c.Disposed));
    }

    [Fact]
    public async Task 検証なしでもつながらなければ調べられなかったにする()
    {
        var (result, _) = await Check(s => s.VerifyCertificate
            ? FerryException.UntrustedCertificate("x", null, SslPolicyErrors.RemoteCertificateChainErrors)
            : FerryException.LoginFailed());
        Assert.Equal(FtpsCheckOutcome.Failed, result.Outcome);
        Assert.Contains("ログイン", result.Reason);
    }

    [Fact]
    public async Task AUTH_TLSを断られたら対応していない()
    {
        var (result, clients) = await Check(_ => FerryException.FtpsNotSupported());
        Assert.Equal(FtpsCheckOutcome.NotSupported, result.Outcome);
        Assert.Single(clients);
    }

    [Fact]
    public async Task パスワードが無ければ接続せずに調べられないとする()
    {
        var (result, clients) = await Check(_ => null, password: null);
        Assert.Equal(FtpsCheckOutcome.Failed, result.Outcome);
        Assert.Contains("パスワードが保存されていない", result.Reason);
        Assert.Empty(clients);
    }

    [Fact]
    public async Task FTP以外の接続先は調べない()
    {
        var (result, clients) = await Check(_ => null, Ftp with { ServerProtocol = ServerProtocol.Ftps });
        Assert.Equal(FtpsCheckOutcome.Failed, result.Outcome);
        Assert.Empty(clients);
    }

    // MARK: まとめて調べる画面の中身

    private static (TempDirectory Temp, ServerStore Store) MakeStore()
    {
        var temp = new TempDirectory();
        var store = new ServerStore(temp.Combine("me", "servers.json"), new MemoryPasswordStore());
        store.Shared.SetDirectory(temp.Combine("Dropbox", "Ferry"), watch: false);
        return (temp, store);
    }

    private static ServerConfig Server(string name, ServerProtocol protocol = ServerProtocol.Ftp) => new()
    {
        Name = name, Host = name + ".example.jp", Username = "u", ServerProtocol = protocol,
    };

    [Fact]
    public void 自分と会社の共有のFTPの接続先だけを並べる()
    {
        var (temp, store) = MakeStore();
        using var _1 = temp;
        using var _2 = store;
        store.Upsert(Server("b"));
        store.Upsert(Server("a"));
        store.Upsert(Server("sftp", ServerProtocol.Sftp));
        store.Upsert(Server("ftps", ServerProtocol.Ftps));
        store.Upsert(Server("shared"), shared: true);

        var survey = FtpsSurvey.FromStore(store);
        Assert.Equal(["shared", "a", "b"], survey.Entries.Select(e => e.Server.Name));
        Assert.True(survey.Entries[0].IsShared);
        Assert.All(survey.Entries, e => Assert.Equal(FtpsSurvey.EntryState.NotChecked, e.State));
        Assert.All(survey.Entries, e => Assert.False(e.IsSelected));
    }

    [Fact]
    public async Task 調べると切り替えられるものだけ選んだ状態になり同時に3件まで調べる()
    {
        var entries = new[] { "ok", "cert", "no", "fail", "ok2" }
            .Select(n => new FtpsSurvey.Entry { Server = Server(n) }).ToList();
        var survey = new FtpsSurvey(entries);
        var running = 0;
        var maxRunning = 0;
        var events = new List<(string, FtpsSurvey.EntryState)>();
        survey.EntryChanged += (_, e) => { lock (events) events.Add((e.Server.Name, e.State)); };

        await survey.RunAsync(async (server, _) =>
        {
            var now = Interlocked.Increment(ref running);
            lock (events) maxRunning = Math.Max(maxRunning, now);
            await Task.Delay(30);
            Interlocked.Decrement(ref running);
            return server.Name switch
            {
                "ok" or "ok2" => new FtpsCheckResult(FtpsCheckOutcome.Switchable),
                "cert" => new FtpsCheckResult(FtpsCheckOutcome.CertificateUnverified, "x", SslPolicyErrors.RemoteCertificateNameMismatch),
                "no" => new FtpsCheckResult(FtpsCheckOutcome.NotSupported, "x"),
                _ => throw new InvalidOperationException("壊れた"),
            };
        });

        Assert.InRange(maxRunning, 2, 3);
        Assert.All(entries, e => Assert.Equal(FtpsSurvey.EntryState.Done, e.State));
        Assert.Equal(["ok", "ok2"], entries.Where(e => e.IsSelected).Select(e => e.Server.Name));
        // 証明書を検証できないものは選べるが、最初は選ばない
        Assert.True(entries[1].CanSelect);
        Assert.False(entries[1].IsSelected);
        // 対応していない・調べられなかったものは選べない
        entries[2].IsSelected = true;
        Assert.False(entries[2].IsSelected);
        Assert.Equal(FtpsCheckOutcome.Failed, entries[3].Result!.Outcome);
        Assert.Equal("壊れた", entries[3].Result!.Reason);
        // 1 件ずつ「調べている」→「結果」の順に届く
        Assert.Equal(10, events.Count);
        Assert.All(entries, e => Assert.Equal(
            [FtpsSurvey.EntryState.Checking, FtpsSurvey.EntryState.Done],
            events.Where(x => x.Item1 == e.Server.Name).Select(x => x.Item2)));
    }

    [Fact]
    public async Task 中止したものは調べる前に戻る()
    {
        var entries = Enumerable.Range(0, 5).Select(i => new FtpsSurvey.Entry { Server = Server("s" + i) }).ToList();
        var survey = new FtpsSurvey(entries);
        using var cancel = new CancellationTokenSource();
        await survey.RunAsync(async (_, token) =>
        {
            cancel.Cancel();
            await Task.Delay(1000, token);
            return new FtpsCheckResult(FtpsCheckOutcome.Switchable);
        }, cancellation: cancel.Token);
        Assert.False(survey.IsRunning);
        Assert.All(entries, e => Assert.Equal(FtpsSurvey.EntryState.NotChecked, e.State));
    }

    [Fact]
    public async Task 選んだものをFTPSにして保存しポートはそのまま()
    {
        var (temp, store) = MakeStore();
        using var _1 = temp;
        using var _2 = store;
        store.Upsert(Server("ok") with { Port = 2121 });
        store.Upsert(Server("cert"));
        store.Upsert(Server("cert-not-selected"));
        store.Upsert(Server("shared"), shared: true);
        store.Upsert(Server("no"));

        var survey = FtpsSurvey.FromStore(store);
        await survey.RunAsync((server, _) => Task.FromResult(server.Name switch
        {
            "ok" or "shared" => new FtpsCheckResult(FtpsCheckOutcome.Switchable),
            "no" => new FtpsCheckResult(FtpsCheckOutcome.NotSupported, "x"),
            _ => new FtpsCheckResult(FtpsCheckOutcome.CertificateUnverified, "x"),
        }));
        survey.Entries.Single(e => e.Server.Name == "cert").IsSelected = true;
        Assert.True(survey.HasSharedSelected);
        Assert.Equal(3, survey.SelectedCount);

        var switched = survey.Apply(store);
        Assert.Equal(3, switched.Count);
        ServerConfig Saved(string name) => store.Servers.Single(s => s.Name == name);
        Assert.Equal(ServerProtocol.Ftps, Saved("ok").ServerProtocol);
        Assert.Equal(2121, Saved("ok").Port);
        Assert.True(Saved("ok").VerifyCertificate);
        Assert.Equal(ServerProtocol.Ftps, Saved("cert").ServerProtocol);
        Assert.False(Saved("cert").VerifyCertificate);
        Assert.Equal(ServerProtocol.Ftp, Saved("cert-not-selected").ServerProtocol);
        Assert.Equal(ServerProtocol.Ftp, Saved("no").ServerProtocol);
        // 会社の共有の接続先は共有フォルダに保存される（全員の設定が変わる）
        Assert.True(store.IsShared(Saved("shared").Id));
        Assert.Equal(ServerProtocol.Ftps, store.Shared.Servers.Single(s => s.Name == "shared").ServerProtocol);

        // 保存した内容が読み直しても同じ
        using var reloaded = new ServerStore(temp.Combine("me", "servers.json"));
        Assert.Equal(ServerProtocol.Ftps, reloaded.Servers.Single(s => s.Name == "ok").ServerProtocol);
    }

    [Fact]
    public async Task 調べた後に接続先が変わっていたら切り替えない()
    {
        var (temp, store) = MakeStore();
        using var _1 = temp;
        using var _2 = store;
        store.Upsert(Server("ok"));
        var survey = FtpsSurvey.FromStore(store);
        await survey.RunAsync((_, _) => Task.FromResult(new FtpsCheckResult(FtpsCheckOutcome.Switchable)));
        var id = survey.Entries[0].Server.Id;
        store.Upsert(store.Server(id)! with { Host = "other.example.jp" });

        Assert.Empty(survey.Apply(store));
        Assert.Equal(ServerProtocol.Ftp, store.Server(id)!.ServerProtocol);
    }

    // MARK: 接続中の画面の印

    [Fact]
    public void 対応していないと分かった接続先と今後表示しないを選んだ接続先には印を出さない()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("settings.json");
        var settings = AppSettings.Load(path);
        var a = Server("a");
        var b = Server("b");
        Assert.True(settings.ShowsFtpsHint(a));
        Assert.False(settings.ShowsFtpsHint(Server("s", ServerProtocol.Ftps)));

        Assert.False(settings.RememberFtpsCheck(a.Id, new FtpsCheckResult(FtpsCheckOutcome.Failed, "x")));
        Assert.True(settings.ShowsFtpsHint(a));
        Assert.True(settings.RememberFtpsCheck(a.Id, new FtpsCheckResult(FtpsCheckOutcome.NotSupported, "x")));
        Assert.False(settings.ShowsFtpsHint(a));
        settings.HiddenFtpsHints[b.Id] = "dismissed";
        settings.Save();

        var reloaded = AppSettings.Load(path);
        Assert.False(reloaded.ShowsFtpsHint(a));
        Assert.False(reloaded.ShowsFtpsHint(b));
        Assert.True(reloaded.ShowsFtpsHint(Server("c")));
    }

    // MARK: SFTP のホスト鍵

    [Fact]
    public void 承認した鍵に置き換えるとその鍵でだけつながる()
    {
        using var temp = new TempDirectory();
        var knownHosts = new KnownHosts(temp.Combine("known_hosts.json"));
        Assert.Equal(KnownHosts.Result.Added, knownHosts.Check("Example.jp", 22, "ssh-ed25519", "OLD"));
        Assert.Equal(KnownHosts.Result.Added, knownHosts.Check("other.jp", 22, "ssh-rsa", "KEEP"));
        Assert.Equal(KnownHosts.Result.Changed, knownHosts.Check("example.jp", 22, "ssh-ed25519", "NEW"));

        knownHosts.Replace("EXAMPLE.jp", 22, new HostKeyInfo("ssh-ed25519", "NEW"));
        Assert.Equal(KnownHosts.Result.Trusted, knownHosts.Check("example.jp", 22, "ssh-ed25519", "NEW"));
        // 前の鍵も、さらに別の鍵も、別の種類の鍵も受け入れない
        Assert.Equal(KnownHosts.Result.Changed, knownHosts.Check("example.jp", 22, "ssh-ed25519", "OLD"));
        Assert.Equal(KnownHosts.Result.Changed, knownHosts.Check("example.jp", 22, "ssh-ed25519", "THIRD"));
        Assert.Equal(KnownHosts.Result.Changed, knownHosts.Check("example.jp", 22, "ecdsa-sha2-nistp256", "NEW"));
        // 他のホスト・他のポートの記録はそのまま
        Assert.Equal(KnownHosts.Result.Trusted, knownHosts.Check("other.jp", 22, "ssh-rsa", "KEEP"));
        Assert.Equal(KnownHosts.Result.Added, knownHosts.Check("example.jp", 2222, "ssh-ed25519", "X"));
    }

    [Fact]
    public void 別の種類の鍵に変わったときも承認した種類だけを記録する()
    {
        using var temp = new TempDirectory();
        var knownHosts = new KnownHosts(temp.Combine("known_hosts.json"));
        knownHosts.Check("example.jp", 22, "ssh-rsa", "RSA");
        Assert.Equal(KnownHosts.Result.Changed, knownHosts.Check("example.jp", 22, "ssh-ed25519", "ED"));

        knownHosts.Replace("example.jp", 22, new HostKeyInfo("ssh-ed25519", "ED"));
        Assert.Equal(KnownHosts.Result.Trusted, knownHosts.Check("example.jp", 22, "ssh-ed25519", "ED"));
        Assert.Equal(KnownHosts.Result.Changed, knownHosts.Check("example.jp", 22, "ssh-rsa", "RSA"));
    }

    [Fact]
    public void フィンガープリントはssh_keygenと同じ表記で出す()
    {
        Assert.Equal("SHA256:abc+/x", new HostKeyInfo("ssh-ed25519", "abc+/x").DisplayFingerprint);
        Assert.Equal("SHA256:abc", new HostKeyInfo("ssh-ed25519", "SHA256:abc").DisplayFingerprint);
        var error = FerryException.HostKeyChanged("example.jp", new HostKeyInfo("ssh-ed25519", "abc"));
        Assert.Equal(FerryErrorKind.HostKeyChanged, error.Kind);
        Assert.Equal("abc", error.NewHostKey!.Fingerprint);
    }
}
