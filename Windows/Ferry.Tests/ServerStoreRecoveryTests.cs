using Ferry.Core;

namespace Ferry.Tests;

/// <summary>読めなかった servers.json・overrides.json を、保存のときに上書きして消さないこと</summary>
public class ServerStoreRecoveryTests
{
    private static string[] Kept(TempDirectory temp, string name) =>
        Directory.GetFiles(temp.Path, $"{name}（読めなかったもの *）.json");

    [Fact]
    public void 壊れたserversJsonは上書きする前に別の名前で残す()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("servers.json");
        // 同期の途中で途切れたような JSON
        const string broken = "{\n  \"servers\": [\n    { \"name\": \"お客様のサーバー\", \"host\": \"ftp.example.com\"";
        File.WriteAllText(path, broken);

        using var store = new ServerStore(path);
        Assert.Empty(store.PersonalServers);
        store.Upsert(new ServerConfig { Name = "新しい接続先", Host = "new.example.com", Username = "user" });

        var kept = Assert.Single(Kept(temp, "servers"));
        Assert.Equal(broken, File.ReadAllText(kept));
        Assert.Matches(@"servers（読めなかったもの \d{4}-\d{2}-\d{2} \d{6}）\.json$", kept);
        // 新しい servers.json は読める形で書かれている
        using (var reopened = new ServerStore(path))
        {
            Assert.Equal("新しい接続先", Assert.Single(reopened.PersonalServers).Name);
        }

        // 残すのは 1 回だけ
        store.Upsert(new ServerConfig { Name = "もう 1 つ", Host = "other.example.com", Username = "user" });
        Assert.Single(Kept(temp, "servers"));
    }

    [Fact]
    public void 知らない値で読めないserversJsonも残す()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("servers.json");
        // 新しい版が足したプロトコル（この版の enum に無い）
        var unknown = File.ReadAllText(TempDirectory.Fixture("mac-servers.json")).Replace("\"sftp\"", "\"webdav\"");
        File.WriteAllText(path, unknown);

        using var store = new ServerStore(path);
        store.AddFolder("新しいフォルダ");

        Assert.Equal(unknown, File.ReadAllText(Assert.Single(Kept(temp, "servers"))));
    }

    [Fact]
    public void ファイルが無いだけなら何も残さない()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("servers.json");

        using var store = new ServerStore(path, localOverridesPath: temp.Combine("overrides.json"));
        store.Upsert(new ServerConfig { Name = "最初の接続先", Host = "ftp.example.com", Username = "user" });

        Assert.Equal(["overrides.json", "servers.json"], Directory.GetFiles(temp.Path).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void 読めるファイルは今までどおり上書きする()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("servers.json");
        File.Copy(TempDirectory.Fixture("mac-servers.json"), path);

        using var store = new ServerStore(path);
        store.AddFolder("新しいフォルダ");

        Assert.Empty(Kept(temp, "servers"));
    }

    [Fact]
    public void 壊れたoverridesJsonも残す()
    {
        using var temp = new TempDirectory();
        var serversPath = temp.Combine("servers.json");
        var overridesPath = temp.Combine("overrides.json");
        File.Copy(TempDirectory.Fixture("mac-servers.json"), serversPath);
        File.WriteAllText(overridesPath, "{ \"personal\": { ");

        using var store = new ServerStore(serversPath, localOverridesPath: overridesPath);
        store.AddFolder("新しいフォルダ");

        Assert.Equal("{ \"personal\": { ", File.ReadAllText(Assert.Single(Kept(temp, "overrides"))));
        Assert.Empty(Kept(temp, "servers"));
    }

    [Fact]
    public void 同じ秒に残すときは番号を付ける()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("servers.json");
        var now = new DateTime(2026, 11, 10, 15, 30, 0);
        var first = ServerStore.UnreadableCopyPath(path, now);
        Assert.Equal(temp.Combine("servers（読めなかったもの 2026-11-10 153000）.json"), first);

        File.WriteAllText(first, "");
        Assert.Equal(temp.Combine("servers（読めなかったもの 2026-11-10 153000） 2.json"), ServerStore.UnreadableCopyPath(path, now));
    }
}
