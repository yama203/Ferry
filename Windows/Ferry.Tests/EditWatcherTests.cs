using Ferry.Core;

namespace Ferry.Tests;

public class EditWatcherTests
{
    [Fact]
    public void 保存を検知し更新日時が落ち着いてからアップロードする()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("style.css");
        File.WriteAllText(path, "a");
        var t0 = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, t0);
        var saved = new List<string>();
        using var watcher = new EditWatcher(f => saved.Add(f.RemotePath));
        watcher.Watch(path, "/www/style.css", startTimer: false);

        watcher.Poll();
        Assert.Empty(saved);

        // 保存された（1 回目の確認では書き込み途中かもしれないので待つ）
        File.SetLastWriteTimeUtc(path, t0.AddSeconds(5));
        watcher.Poll();
        Assert.Empty(saved);

        // まだ書き込み中（更新日時が変わった）
        File.SetLastWriteTimeUtc(path, t0.AddSeconds(6));
        watcher.Poll();
        Assert.Empty(saved);

        // 落ち着いた
        watcher.Poll();
        Assert.Equal(["/www/style.css"], saved);

        // 変わらなければ何度確認してもアップロードしない
        watcher.Poll();
        watcher.Poll();
        Assert.Single(saved);
    }

    [Fact]
    public void 同じファイルを開き直すと監視は1つにまとまり止めると消える()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("a.php");
        File.WriteAllText(path, "a");
        using var watcher = new EditWatcher(_ => { });

        watcher.Watch(path, "/a.php", startTimer: false);
        var file = watcher.Watch(path, "/a.php", startTimer: false);
        Assert.Single(watcher.Files);

        watcher.Stop(file.Id);
        Assert.Empty(watcher.Files);
    }
}
