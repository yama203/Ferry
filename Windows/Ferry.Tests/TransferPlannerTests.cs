using Ferry.Core;
using Ferry.Core.Remote;

namespace Ferry.Tests;

/// <summary>メモリ上のサーバー（一覧だけ）</summary>
public sealed class FakeRemoteClient : IRemoteClient
{
    public ServerConfig Server { get; } = new() { Name = "fake", Host = "fake" };
    /// <summary>パス → 項目（フォルダの中身は親のパスで探す）</summary>
    public Dictionary<string, RemoteItem> Items { get; } = [];

    public void AddFile(string path, long size, DateTime? modified = null) =>
        Items[path] = new RemoteItem { Name = RemotePath.Name(path), Path = path, Size = size, Modified = modified };

    public void AddFolder(string path) =>
        Items[path] = new RemoteItem { Name = RemotePath.Name(path), Path = path, Kind = RemoteItem.ItemKind.Directory };

    public Task<IReadOnlyList<RemoteItem>> ListAsync(string directory, CancellationToken cancellation = default)
    {
        if (directory != "/" && !Items.ContainsKey(directory)) throw FerryException.NotFound();
        return Task.FromResult<IReadOnlyList<RemoteItem>>(
            Items.Values.Where(i => RemotePath.Parent(i.Path) == directory && i.Path != "/").ToList());
    }

    public void SetPassword(string? password) { }
    public Task<string> HomeDirectoryAsync(CancellationToken cancellation = default) => Task.FromResult("/");
    public Task DownloadAsync(string remotePath, string localPath, long? expectedSize = null, TransferProgress? progress = null,
                              CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task UploadAsync(string localPath, string remotePath, TransferProgress? progress = null,
                            CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task MakeDirectoryAsync(string path, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task DeleteFileAsync(string path, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task RemoveDirectoryAsync(string path, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task RenameAsync(string from, string to, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task ChangePermissionsAsync(string path, string mode, CancellationToken cancellation = default) => throw new NotSupportedException();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class TransferPlannerTests
{
    private static readonly DateTime T = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void アップロードはフォルダを展開し除外ルールは中身にだけ使う()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine("site", "node_modules", "x"));
        File.WriteAllText(temp.Combine("site", "node_modules", "x", "a.js"), "a");
        Directory.CreateDirectory(temp.Combine("site", "css"));
        File.WriteAllText(temp.Combine("site", "css", "style.css"), "body{}");
        File.WriteAllText(temp.Combine("site", "index.php"), "<?php");
        File.WriteAllText(temp.Combine("site", "Thumbs.db"), "");
        // 直接選んだものは除外ルールに当たっても転送する
        File.WriteAllText(temp.Combine("Thumbs.db"), "x");

        var batch = TransferPlanner.CollectUploads([temp.Combine("site"), temp.Combine("Thumbs.db")], "/www", ExcludeRules.Defaults);

        Assert.Equal(["/www/site/css/style.css", "/www/site/index.php", "/www/Thumbs.db"], batch.Select(t => t.RemotePath));
        Assert.All(batch, t => Assert.Equal(TransferDirection.Upload, t.Direction));
        Assert.Equal(6, batch[0].Size);
    }

    [Fact]
    public async Task サーバーに同名のファイルがあれば印を付ける()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.Combine("a.txt"), "a");
        File.WriteAllText(temp.Combine("b.txt"), "b");
        var server = new FakeRemoteClient();
        server.AddFolder("/www");
        server.AddFile("/www/a.txt", 10, T);

        var batch = TransferPlanner.CollectUploads([temp.Combine("a.txt"), temp.Combine("b.txt")], "/www", ExcludeRules.Defaults);
        var marked = await TransferPlanner.MarkRemoteConflictsAsync(batch, server);

        Assert.True(marked[0].DestinationExists);
        Assert.Equal(T, marked[0].DestinationModified);
        Assert.False(marked[1].DestinationExists);
    }

    [Fact]
    public async Task まだ無いフォルダへのアップロードは衝突なし()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.Combine("a.txt"), "a");
        var batch = TransferPlanner.CollectUploads([temp.Combine("a.txt")], "/new/dir", ExcludeRules.Defaults);

        var marked = await TransferPlanner.MarkRemoteConflictsAsync(batch, new FakeRemoteClient());

        Assert.False(Assert.Single(marked).DestinationExists);
    }

    [Fact]
    public async Task ダウンロードはフォルダを作って中身を展開する()
    {
        using var temp = new TempDirectory();
        var server = new FakeRemoteClient();
        server.AddFolder("/wp");
        server.AddFolder("/wp/empty");
        server.AddFolder("/wp/.git");
        server.AddFile("/wp/.git/HEAD", 1);
        server.AddFile("/wp/.htaccess", 5);
        server.AddFile("/wp/a:b.php", 7, T);

        var batch = await TransferPlanner.CollectDownloadsAsync([server.Items["/wp"]], temp.Path, server, ExcludeRules.Defaults);

        Assert.Equal([temp.Combine("wp", ".htaccess"), temp.Combine("wp", "a：b.php")], batch.Select(t => t.LocalPath));
        Assert.Equal(T, batch[1].SourceModified);
        Assert.True(Directory.Exists(temp.Combine("wp", "empty")));
        Assert.False(Directory.Exists(temp.Combine("wp", ".git")));
    }

    [Fact]
    public void 上書きの方針で絞り込む()
    {
        PlannedTransfer Item(string name, bool exists, double sourceSeconds) => new()
        {
            Direction = TransferDirection.Upload, LocalPath = name, RemotePath = "/" + name,
            DestinationExists = exists, SourceModified = T.AddSeconds(sourceSeconds), DestinationModified = T,
        };
        var batch = new[] { Item("new", false, 0), Item("newer", true, 60), Item("older", true, -60), Item("same", true, 0.5) };

        Assert.Equal(["new", "newer"], TransferPlanner.Apply(batch, ConflictPolicy.NewerOnly).Select(t => t.LocalPath));
        Assert.Equal(4, TransferPlanner.Apply(batch, ConflictPolicy.Overwrite).Count);
        Assert.Equal(["new"], TransferPlanner.Apply(batch, ConflictPolicy.Skip).Select(t => t.LocalPath));
    }

    [Theory]
    [InlineData("a:b?.txt", "a：b？.txt")]
    [InlineData("name. ", "name")]
    [InlineData("...", "_")]
    [InlineData("普通の名前.html", "普通の名前.html")]
    public void Windowsで使えない文字を置き換える(string name, string expected)
    {
        Assert.Equal(expected, TransferPlanner.LocalName(name));
    }
}
