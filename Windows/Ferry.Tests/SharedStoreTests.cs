using System.Text.Json.Nodes;
using Ferry.Core;

namespace Ferry.Tests;

public class SharedStoreTests
{
    private static readonly Guid SharedId = Guid.Parse("9C4D5E6F-7A8B-4C3D-9E4F-5A6B7C8D9EAF");

    private static (TempDirectory Temp, ServerStore Store, MemoryPasswordStore Passwords) MakeStore()
    {
        var temp = new TempDirectory();
        var passwords = new MemoryPasswordStore();
        var store = new ServerStore(temp.Combine("me", "servers.json"), passwords);
        store.Shared.SetDirectory(temp.Combine("Dropbox", "Ferry"), watch: false);
        return (temp, store, passwords);
    }

    [Fact]
    public void 共有フォルダを設定すると目印とserversフォルダを作る()
    {
        var (temp, store, _) = MakeStore();
        using var _1 = temp;
        using var _2 = store;

        var marker = temp.Combine("Dropbox", "Ferry", "ferry-shared.json");
        Assert.Equal(1, (int)JsonNode.Parse(File.ReadAllText(marker))!["version"]!);
        Assert.True(Directory.Exists(temp.Combine("Dropbox", "Ferry", "servers")));
    }

    [Fact]
    public void Mac版が書いた共有の接続先を読み競合コピーは読まない()
    {
        var (temp, store, _) = MakeStore();
        using var _1 = temp;
        using var _2 = store;
        var servers = temp.Combine("Dropbox", "Ferry", "servers");
        File.Copy(TempDirectory.Fixture("mac-shared-server.json"), Path.Combine(servers, "9C4D5E6F-7A8B-4C3D-9E4F-5A6B7C8D9EAF.json"));
        File.Copy(TempDirectory.Fixture("mac-shared-server.json"),
                  Path.Combine(servers, "9C4D5E6F-7A8B-4C3D-9E4F-5A6B7C8D9EAF (山川 の競合コピー 2026-10-31).json"));
        File.WriteAllText(Path.Combine(servers, "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA.json"), "{ 壊れたファイル");
        File.WriteAllText(temp.Combine("Dropbox", "Ferry", "folders.json"),
                          """[{"id" : "1B2C3D4E-5F6A-4B7C-8D9E-0F1A2B3C4D5E", "name" : "お客様"}]""");

        store.Shared.Load();

        var shared = Assert.Single(store.SharedServers);
        Assert.Equal("会社の共有サーバー", shared.Name);
        Assert.True(store.IsShared(SharedId));
        Assert.Equal("お客様", Assert.Single(store.Shared.Folders).Name);
        Assert.True(store.IsSharedFolder(shared.FolderId!.Value));
    }

    [Fact]
    public void 共有の接続先にはPCごとの設定を書かず自分の設定に分けて保存する()
    {
        var (temp, store, _) = MakeStore();
        using var _1 = temp;
        using var _2 = store;
        var bookmark = new PaneBookmark { Name = "テーマ", LocalPath = @"C:\Sites", RemotePath = "/www" };
        var server = new ServerConfig
        {
            Name = "共有", Host = "h", Username = "u", LocalPath = @"C:\Sites",
            PrivateKeyPath = @"C:\keys\id", Bookmarks = [bookmark], StartBookmarkId = bookmark.Id,
        };

        store.Upsert(server, shared: true);

        var file = temp.Combine("Dropbox", "Ferry", "servers", FerryJson.IdString(server.Id) + ".json");
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.DoesNotContain(json, p => p.Key is "localPath" or "privateKeyPath" or "bookmarks" or "startBookmarkID");
        Assert.Empty(store.PersonalServers);
        // 一覧では PC ごとの設定を重ねて見せる
        Assert.Equal(server, Assert.Single(store.Servers));

        using var reloaded = new ServerStore(store.FilePath);
        reloaded.Shared.SetDirectory(temp.Combine("Dropbox", "Ferry"), watch: false);
        Assert.Equal(server, Assert.Single(reloaded.Servers));
    }

    [Fact]
    public void 共有と自分の間で移してもIDとパスワードはそのまま()
    {
        var (temp, store, passwords) = MakeStore();
        using var _1 = temp;
        using var _2 = store;
        var server = new ServerConfig { Name = "移す", Host = "h", Username = "u", LocalPath = @"D:\work" };
        store.Upsert(server);
        passwords.SetPassword(server.Id, "secret");
        var folder = store.AddFolder("共有のフォルダ", shared: true);

        store.MoveServer(server.Id, folder.Id, toShared: true);

        Assert.True(store.IsShared(server.Id));
        Assert.Empty(store.PersonalServers);
        Assert.Equal(folder.Id, store.Server(server.Id)!.FolderId);
        Assert.Equal(@"D:\work", store.Server(server.Id)!.LocalPath);
        Assert.Equal("secret", passwords.GetPassword(server.Id));

        store.MoveServer(server.Id, null, toShared: false);

        Assert.False(store.IsShared(server.Id));
        Assert.Empty(store.Shared.Servers);
        Assert.Empty(store.SharedOverrides);
        Assert.Equal(@"D:\work", Assert.Single(store.PersonalServers).LocalPath);
    }

    [Fact]
    public void 削除するとパスワードも消え共有フォルダのファイルも消える()
    {
        var (temp, store, passwords) = MakeStore();
        using var _1 = temp;
        using var _2 = store;
        var server = new ServerConfig { Name = "消す", Host = "h", Username = "u" };
        store.Upsert(server, shared: true);
        passwords.SetPassword(server.Id, "secret");

        store.Delete(server.Id);

        Assert.Empty(store.Servers);
        Assert.Null(passwords.GetPassword(server.Id));
        Assert.Empty(Directory.GetFiles(temp.Combine("Dropbox", "Ferry", "servers")));
    }

    [Fact]
    public void フォルダを消すと中の接続先はフォルダの外に出る()
    {
        var (temp, store, _) = MakeStore();
        using var _1 = temp;
        using var _2 = store;
        var mine = store.AddFolder("自分");
        var shared = store.AddFolder("共有", shared: true);
        store.Upsert(new ServerConfig { Name = "a", Host = "h", Username = "u", FolderId = mine.Id });
        store.Upsert(new ServerConfig { Name = "b", Host = "h", Username = "u", FolderId = shared.Id }, shared: true);

        store.DeleteFolder(mine.Id);
        store.DeleteFolder(shared.Id);

        Assert.Empty(store.Folders);
        Assert.All(store.Servers, s => Assert.Null(s.FolderId));
        Assert.Single(store.ServersInFolder(null, shared: true));
        Assert.Single(store.ServersInFolder(null, shared: false));
    }

    [Fact]
    public void ブックマークの追加と接続時に開く設定()
    {
        var (temp, store, _) = MakeStore();
        using var _1 = temp;
        using var _2 = store;
        var server = new ServerConfig { Name = "a", Host = "h", Username = "u" };
        store.Upsert(server);
        var bookmark = new PaneBookmark { Name = "b", LocalPath = @"C:\", RemotePath = "/" };

        store.AddBookmark(bookmark, server.Id);
        store.SetStartBookmark(bookmark.Id, server.Id);
        Assert.Equal(bookmark, store.Server(server.Id)!.StartBookmark);

        store.RemoveBookmark(bookmark.Id, server.Id);
        Assert.Null(store.Server(server.Id)!.Bookmarks);
        Assert.Null(store.Server(server.Id)!.StartBookmarkId);
    }

    [Fact]
    public async Task 他の人の変更を監視して反映する()
    {
        using var temp = new TempDirectory();
        using var store = new ServerStore(temp.Combine("me", "servers.json"));
        var directory = temp.Combine("Dropbox");
        store.Shared.SetDirectory(directory);
        var changed = new TaskCompletionSource();
        store.Changed += (_, _) => changed.TrySetResult();

        File.Copy(TempDirectory.Fixture("mac-shared-server.json"),
                  Path.Combine(directory, "servers", "9C4D5E6F-7A8B-4C3D-9E4F-5A6B7C8D9EAF.json"));

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("会社の共有サーバー", Assert.Single(store.SharedServers).Name);
    }

    [Fact]
    public void 読み込みでは同じ接続先を飛ばし同名のフォルダに入れる()
    {
        var (temp, store, _) = MakeStore();
        using var _1 = temp;
        using var _2 = store;
        var folder = store.AddFolder("FileZilla");
        store.Upsert(new ServerConfig { Name = "既存", Host = "a.example.com", Username = "u", Port = 21 });

        var added = store.Import([
            ("FileZilla", [
                new ServerConfig { Name = "同じ", Host = "a.example.com", Username = "u" },
                new ServerConfig { Name = "新しい", Host = "b.example.com", Username = "u" },
            ]),
        ]);

        Assert.Equal(1, added);
        Assert.Equal(folder.Id, store.PersonalServers.Single(s => s.Name == "新しい").FolderId);
        Assert.Single(store.PersonalFolders);
    }
}
