using Ferry.Core;
using Ferry.Core.Remote;

namespace Ferry.Tests;

/// <summary>
/// 公開テストサーバー test.rebex.net（demo / password、読み取り専用）での動作確認。
/// ネットワークが必要なので、外したいときは dotnet test --filter "Category!=Network"
/// </summary>
[Trait("Category", "Network")]
public class RebexServerTests
{
    private static ServerConfig Rebex(ServerProtocol protocol) => new()
    {
        Name = "Rebex", Host = "test.rebex.net", Username = "demo", ServerProtocol = protocol,
    };

    private static IRemoteClient Client(ServerProtocol protocol, TempDirectory temp, string password = "password")
    {
        var server = Rebex(protocol);
        return protocol.IsFtpFamily()
            ? new FtpRemoteClient(server, password)
            : new SftpRemoteClient(server, password, new KnownHosts(temp.Combine("known_hosts.json")));
    }

    [Theory]
    [InlineData(ServerProtocol.Ftp)]
    [InlineData(ServerProtocol.Ftps)]
    [InlineData(ServerProtocol.FtpsImplicit)]
    [InlineData(ServerProtocol.Sftp)]
    public async Task 一覧とダウンロード(ServerProtocol protocol)
    {
        using var temp = new TempDirectory();
        await using var client = Client(protocol, temp);

        var home = await client.HomeDirectoryAsync();
        Assert.StartsWith("/", home);

        var items = await client.ListAsync("/");
        var readme = Assert.Single(items, i => i.Name == "readme.txt");
        Assert.False(readme.IsDirectory);
        Assert.Equal("/readme.txt", readme.Path);
        Assert.True(readme.Size > 0);
        Assert.NotNull(readme.Modified);
        Assert.Contains(items, i => i.Name == "pub" && i.IsDirectory);

        var local = temp.Combine("readme.txt");
        long reported = 0;
        await client.DownloadAsync(readme.Path, local, readme.Size, (done, _) => reported = done);
        Assert.Equal(readme.Size, new FileInfo(local).Length);
        Assert.False(File.Exists(local + ".ferrypart"));

        // 深いフォルダの一覧（同じ接続を使い回す）
        var example = await client.ListAsync("/pub/example");
        Assert.NotEmpty(example);
        Assert.All(example, i => Assert.StartsWith("/pub/example/", i.Path));
    }

    [Theory]
    [InlineData(ServerProtocol.Ftp)]
    [InlineData(ServerProtocol.Sftp)]
    public async Task パスワードが違うとログイン失敗(ServerProtocol protocol)
    {
        using var temp = new TempDirectory();
        await using var client = Client(protocol, temp, password: "wrong");

        var error = await Assert.ThrowsAsync<FerryException>(() => client.HomeDirectoryAsync());
        Assert.Equal(FerryErrorKind.LoginFailed, error.Kind);

        // 正しいパスワードを入れ直せばつながる
        client.SetPassword("password");
        Assert.StartsWith("/", await client.HomeDirectoryAsync());
    }

    [Fact]
    public async Task 読み取り専用のサーバーへのアップロードは権限エラー()
    {
        using var temp = new TempDirectory();
        await using var client = Client(ServerProtocol.Sftp, temp);
        var local = temp.Combine("x.txt");
        File.WriteAllText(local, "x");

        var error = await Assert.ThrowsAsync<FerryException>(() => client.UploadAsync(local, "/x.txt"));
        Assert.Equal(FerryErrorKind.PermissionDenied, error.Kind);
    }

    [Fact]
    public async Task 存在しないホスト()
    {
        using var temp = new TempDirectory();
        var server = Rebex(ServerProtocol.Ftp) with { Host = "no-such-host.invalid" };
        await using var client = new FtpRemoteClient(server, "password");

        var error = await Assert.ThrowsAsync<FerryException>(() => client.HomeDirectoryAsync());
        Assert.True(error.Kind == FerryErrorKind.HostNotFound, $"{error.Kind}: {error.InnerException}");
    }

    [Fact]
    public async Task 証明書のホスト名が合わないときは確認を求め検証せずにも接続できる()
    {
        // IP アドレスで接続すると、証明書の名前（test.rebex.net）と合わない
        var address = (await System.Net.Dns.GetHostAddressesAsync("test.rebex.net"))
            .First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        var server = Rebex(ServerProtocol.Ftps) with { Host = address.ToString() };

        await using (var strict = new FtpRemoteClient(server, "password"))
        {
            var error = await Assert.ThrowsAsync<FerryException>(() => strict.HomeDirectoryAsync());
            Assert.True(error.Kind == FerryErrorKind.UntrustedCertificate, $"{error.Kind}: {error.Message} {error.InnerException}");
            Assert.Contains("ホスト名", error.Message);
        }

        await using var relaxed = new FtpRemoteClient(server with { VerifyCertificate = false }, "password");
        Assert.Contains(await relaxed.ListAsync("/"), i => i.Name == "readme.txt");
    }

    [Fact]
    public async Task SFTPのホスト鍵が変わっていたら接続を止める()
    {
        using var temp = new TempDirectory();
        var knownHosts = new KnownHosts(temp.Combine("known_hosts.json"));
        await using (var first = new SftpRemoteClient(Rebex(ServerProtocol.Sftp), "password", knownHosts))
        {
            await first.HomeDirectoryAsync();
        }
        // 記録した鍵を別のものに書き換える
        var path = temp.Combine("known_hosts.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        var keys = json["test.rebex.net:22"]!.AsObject();
        var keyType = Assert.Single(keys).Key;
        keys[keyType] = "別の鍵";
        File.WriteAllText(path, json.ToJsonString());

        await using var second = new SftpRemoteClient(Rebex(ServerProtocol.Sftp), "password", knownHosts);
        var error = await Assert.ThrowsAsync<FerryException>(() => second.HomeDirectoryAsync());
        Assert.Equal(FerryErrorKind.HostKeyChanged, error.Kind);
    }
}
