using Ferry.Core;

namespace Ferry.Tests;

/// <summary>起動時に、前回までにプレビュー・編集・ドラッグ用に落としたファイルを消す</summary>
public class TempFilesTests
{
    private static string Write(string path, DateTime time)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "DB_PASSWORD");
        File.SetCreationTimeUtc(path, time);
        File.SetLastWriteTimeUtc(path, time);
        return path;
    }

    [Fact]
    public void 起動より前に落としたものを消す()
    {
        using var temp = new TempDirectory();
        var startedAt = DateTime.UtcNow;
        var old = startedAt.AddDays(-3);
        var preview = Write(temp.Combine("Preview", "0123456789ABCDEF", "wp-config.php"), old);
        var edit = Write(temp.Combine("Edit", "FEDCBA9876543210", "style.css"), old);
        var drag = Write(temp.Combine("drag", "abc", "theme", "functions.php"), old);
        var other = Write(temp.Combine("other", "keep.txt"), old);

        TempFiles.CleanUp(startedAt, temp.Path);

        Assert.False(File.Exists(preview));
        Assert.False(File.Exists(edit));
        Assert.False(File.Exists(drag));
        // 空になったフォルダも消すが、置き場所そのものは残す
        Assert.Empty(Directory.GetFileSystemEntries(temp.Combine("Preview")));
        Assert.Empty(Directory.GetFileSystemEntries(temp.Combine("drag")));
        // 知らないフォルダには触らない
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void 起動した後に落としたものは残す()
    {
        using var temp = new TempDirectory();
        var startedAt = DateTime.UtcNow.AddMinutes(-1);
        // プレビュー（作った日時も更新日時も起動後）
        var preview = Write(temp.Combine("Preview", "0123456789ABCDEF", "index.php"), DateTime.UtcNow);
        // ドラッグ用（更新日時はサーバーに合わせて古いが、作ったのは起動後）
        var drag = temp.Combine("drag", "abc", "readme.txt");
        Write(drag, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(drag, startedAt.AddYears(-1));

        TempFiles.CleanUp(startedAt, temp.Path);

        Assert.True(File.Exists(preview));
        Assert.True(File.Exists(drag));
    }

    [Fact]
    public void 置き場所が無くても失敗しない()
    {
        using var temp = new TempDirectory();
        TempFiles.CleanUp(DateTime.UtcNow, temp.Combine("無い"));
    }
}
