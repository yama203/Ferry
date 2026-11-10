using Ferry.Core;
using Ferry.Core.Remote;

namespace Ferry.Tests;

public class EditUploadFlowTests
{
    private static readonly DateTime T = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Path = "/www/wp-content/themes/a/style.css";

    private static RemoteItem Item(long size, DateTime modified, string name = "style.css", string permissions = "rw-r--r--") => new()
    {
        Name = name, Path = "/www/wp-content/themes/a/" + name, Size = size, Modified = modified, Permissions = permissions,
    };

    /// <summary>サーバー側（一覧）とアップロード・確認の記録</summary>
    private sealed class Harness
    {
        public List<RemoteItem> Listing { get; set; } = [Item(100, T)];
        public bool ListFails { get; set; }
        public List<string> Log { get; } = [];
        public Func<EditConflict, Task<bool>> Confirm { get; set; } = _ => Task.FromResult(true);
        /// <summary>アップロード（既定では成功し、サーバー側のサイズ・日時が変わる）</summary>
        public Func<Task<bool>> Upload { get; set; }
        public int Uploads { get; private set; }
        public EditUploadFlow Flow { get; }

        public Harness()
        {
            Upload = () =>
            {
                Listing = [Item(200 + Uploads, T.AddMinutes(10 + Uploads))];
                return Task.FromResult(true);
            };
            Flow = new EditUploadFlow(
                directory =>
                {
                    Log.Add($"list {directory}");
                    return ListFails
                        ? Task.FromException<IReadOnlyList<RemoteItem>>(FerryException.Timeout())
                        : Task.FromResult<IReadOnlyList<RemoteItem>>(Listing.ToList());
                },
                (_, conflict) =>
                {
                    Log.Add($"confirm {conflict}");
                    return Confirm(conflict);
                },
                async (_, exists, permissions) =>
                {
                    Log.Add($"upload exists={exists} permissions={permissions}");
                    Uploads++;
                    return await Upload();
                });
        }
    }

    private static EditedFile Opened(RemoteSnapshot? baseline) => new()
    {
        LocalPath = "/tmp/style.css", RemotePath = Path, Baseline = baseline,
    };

    [Fact]
    public void 比べるのはサイズと更新日時で一覧を取れなければ確認しない()
    {
        var baseline = new RemoteSnapshot(100, T);
        Assert.Equal(EditConflict.None, EditUploadFlow.Compare(baseline, [Item(100, T)], Path));
        Assert.Equal(EditConflict.Changed, EditUploadFlow.Compare(baseline, [Item(101, T)], Path));
        Assert.Equal(EditConflict.Changed, EditUploadFlow.Compare(baseline, [Item(100, T.AddSeconds(1))], Path));
        Assert.Equal(EditConflict.Deleted, EditUploadFlow.Compare(baseline, [Item(100, T, "other.css")], Path));
        // 同じ名前のフォルダになっていたら、ファイルは消えている
        Assert.Equal(EditConflict.Deleted, EditUploadFlow.Compare(baseline,
            [new RemoteItem { Name = "style.css", Path = Path, Kind = RemoteItem.ItemKind.Directory }], Path));
        Assert.Equal(EditConflict.None, EditUploadFlow.Compare(baseline, null, Path));
        Assert.Equal(EditConflict.None, EditUploadFlow.Compare(null, [Item(1, T)], Path));
        // 日時が分からない一覧どうしでも、そのまま等しいかで比べる
        Assert.Equal(EditConflict.None, EditUploadFlow.Compare(new RemoteSnapshot(100, null), [new RemoteItem { Name = "style.css", Path = Path, Size = 100 }], Path));
    }

    [Fact]
    public async Task 変わっていなければそのままアップロードし基準を新しい値にする()
    {
        var h = new Harness();
        var file = Opened(new RemoteSnapshot(100, T));

        await h.Flow.SavedAsync(file);
        Assert.Equal(["list /www/wp-content/themes/a", "upload exists=True permissions=rw-r--r--", "list /www/wp-content/themes/a"], h.Log);
        // 偽のサーバーは 1 回目のアップロードで 201 バイト・10 時 11 分になる
        Assert.Equal(new RemoteSnapshot(201, T.AddMinutes(11)), file.Baseline);

        // 自分のアップロードを次の保存で「変更された」と判定しない
        h.Log.Clear();
        await h.Flow.SavedAsync(file);
        Assert.DoesNotContain(h.Log, l => l.StartsWith("confirm"));
        Assert.Equal(2, h.Uploads);
    }

    [Fact]
    public async Task 他の人が変えていたら確認しアップロードしないなら次の保存でもまた確認する()
    {
        var h = new Harness { Listing = [Item(150, T.AddMinutes(1))], Confirm = _ => Task.FromResult(false) };
        var file = Opened(new RemoteSnapshot(100, T));

        await h.Flow.SavedAsync(file);
        Assert.Equal(["list /www/wp-content/themes/a", "confirm Changed"], h.Log);
        Assert.Equal(0, h.Uploads);
        Assert.Equal(new RemoteSnapshot(100, T), file.Baseline);

        await h.Flow.SavedAsync(file);
        Assert.Equal(2, h.Log.Count(l => l == "confirm Changed"));

        // 上書きするを選ぶとアップロードする
        h.Confirm = _ => Task.FromResult(true);
        await h.Flow.SavedAsync(file);
        Assert.Equal(1, h.Uploads);
    }

    [Fact]
    public async Task 削除されていて上書きするなら新しいファイルとして送る()
    {
        var h = new Harness { Listing = [] };
        var file = Opened(new RemoteSnapshot(100, T));

        await h.Flow.SavedAsync(file);
        Assert.Contains("confirm Deleted", h.Log);
        Assert.Contains("upload exists=False permissions=", h.Log);
    }

    [Fact]
    public async Task 一覧を取れなければ確認せずにあるものとしてアップロードする()
    {
        var h = new Harness { ListFails = true };
        var file = Opened(new RemoteSnapshot(100, T));

        await h.Flow.SavedAsync(file);
        Assert.Equal(["list /www/wp-content/themes/a", "upload exists=True permissions=", "list /www/wp-content/themes/a"], h.Log);
        // 取り直せなかったので基準は「分からない」（次の保存では確認しない）
        Assert.Null(file.Baseline);
    }

    [Fact]
    public async Task 確認の途中で何度保存しても確認は一つだけ()
    {
        var answer = new TaskCompletionSource<bool>();
        var h = new Harness { Listing = [Item(150, T.AddMinutes(1))] };
        h.Confirm = _ => answer.Task;
        var file = Opened(new RemoteSnapshot(100, T));

        var first = h.Flow.SavedAsync(file);
        await h.Flow.SavedAsync(file);
        await h.Flow.SavedAsync(file);
        Assert.Single(h.Log, l => l.StartsWith("confirm"));

        answer.SetResult(true);
        await first;
        Assert.Single(h.Log, l => l.StartsWith("confirm"));
        Assert.Equal(1, h.Uploads);
    }

    [Fact]
    public async Task アップロードの途中で保存されたら終わってからもう一度確認して送る()
    {
        var uploading = new TaskCompletionSource<bool>();
        var h = new Harness();
        var defaultUpload = h.Upload;
        h.Upload = async () =>
        {
            if (h.Uploads == 1) await uploading.Task;
            return await defaultUpload();
        };
        var file = Opened(new RemoteSnapshot(100, T));

        var first = h.Flow.SavedAsync(file);
        Assert.Equal(1, h.Uploads);
        // アップロードの途中で 2 回保存された
        await h.Flow.SavedAsync(file);
        await h.Flow.SavedAsync(file);
        uploading.SetResult(true);
        await first;

        // 自分のアップロードの後の値を基準にしてから比べるので、確認は出ない。2 回分はまとめて 1 回
        Assert.Equal(2, h.Uploads);
        Assert.DoesNotContain(h.Log, l => l.StartsWith("confirm"));
    }

    [Fact]
    public async Task アップロードに失敗したら基準は変えない()
    {
        var h = new Harness { Upload = () => Task.FromResult(false) };
        var file = Opened(new RemoteSnapshot(100, T));

        await h.Flow.SavedAsync(file);
        Assert.Equal(new RemoteSnapshot(100, T), file.Baseline);
        Assert.Equal(2, h.Log.Count);

        // 転送一覧からやり直して成功したら、基準を取り直す
        h.Listing = [Item(300, T.AddHours(1))];
        await h.Flow.UploadedOutsideFlowAsync(file);
        Assert.Equal(new RemoteSnapshot(300, T.AddHours(1)), file.Baseline);
    }
}
