using Ferry.Core;
using Ferry.Core.Remote;
using static Ferry.Core.SyncPlanner;

namespace Ferry.Tests;

public class ExcludeRulesTests
{
    [Theory]
    [InlineData(".git", true)]
    [InlineData("node_modules", true)]
    [InlineData("._foo", true)]
    [InlineData(".swp", true)]
    [InlineData("a.swp", true)]
    [InlineData("index.php~", true)]
    [InlineData("image.png.ferrypart", true)]
    [InlineData("Thumbs.db", true)]
    [InlineData(".gitignore", false)]
    [InlineData(".htaccess", false)]
    [InlineData("thumbs.db", false)] // Mac 版（fnmatch）と同じく大文字・小文字を区別する
    [InlineData("node_modules2", false)]
    public void 既定のルール(string name, bool excluded)
    {
        Assert.Equal(excluded, ExcludeRules.Defaults.Excludes(name));
    }

    [Theory]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "ac", false)]
    [InlineData("*.min.*", "app.min.js", true)]
    [InlineData("*a*b*", "xxaYYbzz", true)]
    [InlineData("*a*b", "ab_b_", false)]
    [InlineData("**", "", true)]
    public void ワイルドカード(string pattern, string name, bool matches)
    {
        Assert.Equal(matches, ExcludeRules.Matches(pattern, name));
    }

    [Fact]
    public void 空のパターンは何にも一致しない()
    {
        Assert.False(new ExcludeRules([""]).Excludes(""));
    }
}

public class SyncPlannerTests
{
    private static readonly DateTime T = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Entry File(long size, double seconds = 0) => new(false, size, T.AddSeconds(seconds));
    private static Entry Folder() => new(true, null, T);

    [Fact]
    public void サイズが違うか2秒より新しいファイルを転送する()
    {
        var source = new Dictionary<string, Entry>
        {
            ["new.txt"] = File(10),
            ["size.txt"] = File(20),
            ["newer.txt"] = File(5, 3),
            ["clock.txt"] = File(5, 2),
            ["older.txt"] = File(5, -100),
            ["dir"] = Folder(),
            ["dir/file10.txt"] = File(1),
            ["dir/file9.txt"] = File(1),
        };
        var destination = new Dictionary<string, Entry>
        {
            ["size.txt"] = File(21),
            ["newer.txt"] = File(5),
            ["clock.txt"] = File(5),
            ["older.txt"] = File(5),
        };

        var plan = SyncPlanner.Make(source, destination, deleteExtraneous: false);

        Assert.Equal(["dir/file9.txt", "dir/file10.txt", "new.txt", "newer.txt", "size.txt"],
                     plan.Changes.Select(c => c.RelativePath));
        Assert.Equal(ChangeReason.Changed, plan.Changes.Single(c => c.RelativePath == "size.txt").Reason);
        Assert.Equal(ChangeReason.New, plan.Changes.Single(c => c.RelativePath == "new.txt").Reason);
        Assert.Equal(10 + 20 + 5 + 1 + 1, plan.TotalBytes);
        Assert.Empty(plan.Deletions);
    }

    [Fact]
    public void ミラーでは転送先にしかないものを消しフォルダの中身は挙げない()
    {
        var source = new Dictionary<string, Entry> { ["keep.txt"] = File(1) };
        var destination = new Dictionary<string, Entry>
        {
            ["keep.txt"] = File(1),
            ["old"] = Folder(),
            ["old/a.txt"] = File(1),
            ["old/sub"] = Folder(),
            ["old/sub/b.txt"] = File(1),
            ["old-file.txt"] = File(1),
        };

        var plan = SyncPlanner.Make(source, destination, deleteExtraneous: true);

        Assert.Empty(plan.Changes);
        Assert.Equal([new Deletion("old", true), new Deletion("old-file.txt", false)],
                     plan.Deletions.OrderBy(d => d.RelativePath, StringComparer.Ordinal));
    }

    [Fact]
    public void 違いが無ければ空()
    {
        var entries = new Dictionary<string, Entry> { ["a"] = File(1) };
        Assert.True(SyncPlanner.Make(entries, entries, deleteExtraneous: true).IsEmpty);
    }

    [Fact]
    public void ローカルの走査は除外ルールのフォルダの中に入らない()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine("node_modules", "lib"));
        System.IO.File.WriteAllText(temp.Combine("node_modules", "lib", "x.js"), "x");
        Directory.CreateDirectory(temp.Combine("src"));
        System.IO.File.WriteAllText(temp.Combine("src", "app.js"), "abc");
        System.IO.File.WriteAllText(temp.Combine("Thumbs.db"), "");

        var entries = SyncPlanner.ScanLocal(temp.Path, ExcludeRules.Defaults);

        Assert.Equal(["src", "src/app.js"], entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(3, entries["src/app.js"].Size);
    }
}

public class PathAndPermissionTests
{
    [Theory]
    [InlineData("/a/b/c", "/a/b")]
    [InlineData("/a/b/", "/a")]
    [InlineData("/a", "/")]
    [InlineData("/", "/")]
    public void 親フォルダ(string path, string parent) => Assert.Equal(parent, RemotePath.Parent(path));

    [Fact]
    public void つなげる()
    {
        Assert.Equal("/a/b", RemotePath.Join("/a", "b"));
        Assert.Equal("/b", RemotePath.Join("/", "b"));
        Assert.Equal("c", RemotePath.Name("/a/b/c"));
    }

    [Theory]
    [InlineData("755", "rwxr-xr-x")]
    [InlineData("644", "rw-r--r--")]
    [InlineData("0600", "rw-------")]
    public void パーミッションの変換(string octal, string symbolic)
    {
        Assert.Equal(symbolic, Permissions.FromOctal(octal));
        Assert.Equal(octal[^3..], Permissions.ToOctal(symbolic));
    }

    [Fact]
    public void 読めないパーミッション()
    {
        Assert.Null(Permissions.FromOctal("789"));
        Assert.Null(Permissions.ToOctal("rwx"));
        Assert.Equal("755", Permissions.ToOctal("drwxr-xr-x"));
    }

    [Theory]
    [InlineData("530", "Login incorrect.", FerryErrorKind.LoginFailed)]
    [InlineData("550", "/x: No such file or directory", FerryErrorKind.NotFound)]
    [InlineData("550", "Permission denied.", FerryErrorKind.PermissionDenied)]
    [InlineData("553", "Could not create file.", FerryErrorKind.PermissionDenied)]
    [InlineData("425", "Can't open data connection.", FerryErrorKind.DataConnectionFailed)]
    [InlineData("500", "Unknown command.", FerryErrorKind.Other)]
    public void FTPの応答の分類(string code, string message, FerryErrorKind kind)
    {
        Assert.Equal(kind, FtpRemoteClient.FromReply(code, message).Kind);
    }

    [Fact]
    public void FluentFTPが付ける前置きは取り除く()
    {
        var error = FtpRemoteClient.FromReply("550", "Code: 550 Message: Access denied.");
        Assert.Equal(FerryErrorKind.PermissionDenied, error.Kind);
        Assert.Equal("権限がありません。（サーバーの応答: Access denied.）", error.Message);
    }
}
