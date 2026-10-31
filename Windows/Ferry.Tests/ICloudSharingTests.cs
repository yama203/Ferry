using System.Text.Json.Nodes;
using Ferry.Core;

namespace Ferry.Tests;

/// <summary>Mac 版と同じ servers.json（iCloud Drive）を使うとき、PC ごとの設定をこの PC のファイルに分ける</summary>
public class ICloudSharingTests
{
    private static readonly Guid KajuId = Guid.Parse("6F1C2E1A-1234-4ABC-8DEF-0123456789AB");

    private static (TempDirectory Temp, string ServersPath, string OverridesPath) Setup()
    {
        var temp = new TempDirectory();
        var servers = temp.Combine("iCloudDrive", "Ferry", "servers.json");
        Directory.CreateDirectory(Path.GetDirectoryName(servers)!);
        File.Copy(TempDirectory.Fixture("mac-servers.json"), servers);
        return (temp, servers, temp.Combine("Local", "Ferry", "overrides.json"));
    }

    [Fact]
    public void Macのパスは使わずこのPCの設定を重ねる()
    {
        var (temp, serversPath, overridesPath) = Setup();
        using var _ = temp;
        using var store = new ServerStore(serversPath, localOverridesPath: overridesPath);

        var kaju = store.Server(KajuId)!;
        // Mac のローカルのフォルダとブックマークは Windows では出さない
        Assert.Null(kaju.LocalPath);
        Assert.Null(kaju.Bookmarks);
        Assert.Equal("山神果樹薬草園", kaju.Name);
        Assert.Equal("/www.example.com/wp/wp-content/themes/xxx", kaju.InitialPath);
        // Mac の sharedOverrides もこの PC の設定としては使わない
        Assert.Empty(store.SharedOverrides);
    }

    [Fact]
    public void 書き戻してもMacの値は残りWindowsの値は別のファイルに入る()
    {
        var (temp, serversPath, overridesPath) = Setup();
        using var _ = temp;
        using (var store = new ServerStore(serversPath, localOverridesPath: overridesPath))
        {
            var bookmark = new PaneBookmark { Name = "Windows", LocalPath = @"D:\Sites", RemotePath = "/www" };
            store.Upsert(store.Server(KajuId)! with { Name = "名前を変えた", LocalPath = @"D:\Sites", Bookmarks = [bookmark] });
            store.Upsert(new ServerConfig { Name = "Windows で追加", Host = "w.example.com", Username = "u", LocalPath = @"C:\w" });
        }

        var json = JsonNode.Parse(File.ReadAllText(serversPath))!.AsObject();
        var kaju = json["servers"]!.AsArray().Single(s => (string)s!["id"]! == "6F1C2E1A-1234-4ABC-8DEF-0123456789AB")!;
        // 名前の変更は Mac にも届く
        Assert.Equal("名前を変えた", (string)kaju["name"]!);
        // Mac のパスとブックマークはそのまま
        Assert.Equal("/Users/yama/Sites", (string)kaju["localPath"]!);
        Assert.Equal("テーマ", (string)kaju["bookmarks"]![0]!["name"]!);
        // Windows で追加したものに Windows のパスは書かない
        var added = json["servers"]!.AsArray().Single(s => (string)s!["name"]! == "Windows で追加")!;
        Assert.Null(added["localPath"]);
        // Mac の sharedOverrides も残す
        Assert.Equal("/Users/yama/Work", (string)json["sharedOverrides"]!["9C4D5E6F-7A8B-4C3D-9E4F-5A6B7C8D9EAF"]!["localPath"]!);

        using var reloaded = new ServerStore(serversPath, localOverridesPath: overridesPath);
        Assert.Equal(@"D:\Sites", reloaded.Server(KajuId)!.LocalPath);
        Assert.Equal("Windows", reloaded.Server(KajuId)!.Bookmarks!.Single().Name);
        Assert.Equal(@"C:\w", reloaded.PersonalServers.Single(s => s.Name == "Windows で追加").LocalPath);
    }

    [Fact]
    public void 共有の接続先のこのPCの設定も別のファイルに入る()
    {
        var (temp, serversPath, overridesPath) = Setup();
        using var _ = temp;
        using var store = new ServerStore(serversPath, localOverridesPath: overridesPath);
        store.Shared.SetDirectory(temp.Combine("Dropbox"), watch: false);
        var shared = new ServerConfig { Name = "共有", Host = "s", Username = "u", LocalPath = @"D:\share" };

        store.Upsert(shared, shared: true);

        Assert.Equal(@"D:\share", store.Server(shared.Id)!.LocalPath);
        var file = JsonNode.Parse(File.ReadAllText(serversPath))!.AsObject();
        Assert.Null(file["sharedOverrides"]![FerryJson.IdString(shared.Id)]);
        var local = JsonNode.Parse(File.ReadAllText(overridesPath))!.AsObject();
        Assert.Equal(@"D:\share", (string)local["shared"]![FerryJson.IdString(shared.Id)]!["localPath"]!);
    }

    [Fact]
    public void 前に使っていたファイルの接続先を取り込む()
    {
        var (temp, serversPath, overridesPath) = Setup();
        using var _ = temp;
        var old = temp.Combine("Roaming", "servers.json");
        using (var before = new ServerStore(old))
        {
            before.Upsert(new ServerConfig { Name = "前から", Host = "old.example.com", Username = "u", LocalPath = @"C:\old" });
            before.Upsert(new ServerConfig { Name = "重複", Host = "yamagamikaju.kappe.ne.jp", Port = 10021, Username = "kaju", ServerProtocol = ServerProtocol.Ftps });
        }
        using var store = new ServerStore(serversPath, localOverridesPath: overridesPath);

        Assert.Equal(1, store.MergePersonalFrom(old));
        Assert.Equal(@"C:\old", store.PersonalServers.Single(s => s.Name == "前から").LocalPath);
        Assert.Equal(4, store.PersonalServers.Count);
    }

    [Fact]
    public async Task 他のPCの変更を読み直す()
    {
        var (temp, serversPath, overridesPath) = Setup();
        using var _ = temp;
        using var store = new ServerStore(serversPath, localOverridesPath: overridesPath, watch: true);
        var changed = new TaskCompletionSource();
        store.Changed += (_, _) => changed.TrySetResult();

        await Task.Delay(2100);
        var json = File.ReadAllText(serversPath).Replace("山神果樹薬草園", "Mac で変えた");
        File.WriteAllText(serversPath, json);

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Mac で変えた", store.Server(KajuId)!.Name);
    }
}
