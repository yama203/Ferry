using System.Text.Json.Nodes;
using Ferry.Core;

namespace Ferry.Tests;

/// <summary>Mac 版の保存ファイルを読めること、Mac 版が読める形で書くことを確かめる</summary>
public class FileFormatTests
{
    private static readonly Guid KajuId = Guid.Parse("6F1C2E1A-1234-4ABC-8DEF-0123456789AB");

    [Fact]
    public void Mac版のserversJsonを読める()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("servers.json");
        File.Copy(TempDirectory.Fixture("mac-servers.json"), path);

        using var store = new ServerStore(path);

        Assert.Equal(3, store.PersonalServers.Count);
        var kaju = store.PersonalServers[0];
        Assert.Equal(KajuId, kaju.Id);
        Assert.Equal("山神果樹薬草園", kaju.Name);
        Assert.Equal(ServerProtocol.Ftps, kaju.ServerProtocol);
        Assert.Equal(10021, kaju.EffectivePort);
        Assert.Equal("/www.example.com/wp/wp-content/themes/xxx", kaju.InitialPath);
        Assert.False(kaju.VerifyCertificate);
        Assert.Equal(Guid.Parse("0A6C1E7B-3F2D-4C8E-9B1A-2D3E4F5A6B7C"), kaju.FolderId);
        Assert.Equal("テーマ", kaju.StartBookmark?.Name);

        var sftp = store.PersonalServers[1];
        Assert.Equal(ServerProtocol.Sftp, sftp.ServerProtocol);
        Assert.Null(sftp.Port);
        Assert.Equal(22, sftp.EffectivePort);
        Assert.Equal("~/.ssh/id_ed25519", sftp.PrivateKeyPath);

        var implicitFtps = store.PersonalServers[2];
        Assert.Equal(ServerProtocol.FtpsImplicit, implicitFtps.ServerProtocol);
        Assert.Equal(990, implicitFtps.EffectivePort);
        Assert.False(implicitFtps.PassiveMode);

        Assert.Equal("お客様サーバー", Assert.Single(store.PersonalFolders).Name);
        Assert.Equal([".git", "node_modules", "*.swp"], store.ExcludeRules.Patterns);
        Assert.Equal("/Users/yama/Work", store.SharedOverrides["9c4d5e6f-7a8b-4c3d-9e4f-5a6b7c8d9eaf"].LocalPath);
    }

    [Fact]
    public void 書き戻してもMac版の形式のまま()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("servers.json");
        File.Copy(TempDirectory.Fixture("mac-servers.json"), path);

        using (var store = new ServerStore(path))
        {
            store.Save();
        }

        var written = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var original = JsonNode.Parse(File.ReadAllText(TempDirectory.Fixture("mac-servers.json")))!.AsObject();
        // 内容は同じ（知らないキーも消さない）
        Assert.True(JsonNode.DeepEquals(original, written), written.ToJsonString());
        // キーはアルファベット順
        Assert.Equal(written.Select(p => p.Key).Order(StringComparer.Ordinal), written.Select(p => p.Key));
        // 日本語は \u にしない
        Assert.Contains("山神果樹薬草園", File.ReadAllText(path));
    }

    [Fact]
    public void 新しい接続先はMac版が必須とするキーを必ず書き値の無いキーは省く()
    {
        var server = new ServerConfig { Name = "新規", Host = "example.com", Username = "u", ServerProtocol = ServerProtocol.FtpsImplicit };

        var json = JsonNode.Parse(FerryJson.Serialize(server))!.AsObject();

        Assert.Equal(["host", "id", "name", "passiveMode", "serverProtocol", "username", "verifyCertificate"],
                     json.Select(p => p.Key));
        Assert.Equal("ftpsImplicit", (string?)json["serverProtocol"]);
        Assert.True((bool)json["passiveMode"]!);
        // ID は大文字（Swift の uuidString と同じ）
        var id = (string)json["id"]!;
        Assert.Equal(id.ToUpperInvariant(), id);
        Assert.Equal(server, FerryJson.Deserialize<ServerConfig>(FerryJson.Serialize(server)));
    }

    [Fact]
    public void ファイルが無ければ空で始まり既定の除外ルールを使う()
    {
        using var temp = new TempDirectory();
        using var store = new ServerStore(temp.Combine("Ferry", "servers.json"));

        Assert.Empty(store.Servers);
        Assert.Equal(ExcludeRules.Defaults, store.ExcludeRules);

        store.Upsert(new ServerConfig { Name = "a", Host = "h", Username = "u" });
        Assert.True(File.Exists(store.FilePath));
        using var reloaded = new ServerStore(store.FilePath);
        Assert.Equal("a", Assert.Single(reloaded.PersonalServers).Name);
    }
}
