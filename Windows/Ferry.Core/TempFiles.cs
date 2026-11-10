namespace Ferry.Core;

/// <summary>
/// サーバーから一時フォルダ（%TEMP%\Ferry）に落としたファイルの置き場所。
/// wp-config.php のようにパスワードを含むものもあるので、起動のたびに前回までの分を消す。
/// </summary>
public static class TempFiles
{
    public static string Root => Path.Combine(Path.GetTempPath(), "Ferry");

    /// <summary>プレビュー用</summary>
    public static string Preview => Path.Combine(Root, "Preview");

    /// <summary>エディタで開いて自動アップロードする用</summary>
    public static string Edit => Path.Combine(Root, "Edit");

    /// <summary>エクスプローラーへのドラッグ用</summary>
    public static string Drag => Path.Combine(Root, "drag");

    /// <summary>
    /// プレビュー・編集・ドラッグ用に落としたもののうち、startedAt より前に作ったものを消す。
    /// 消している間に落としたもの（起動直後に開いたプレビューなど）は残す。開かれているなどで消せないものは飛ばす
    /// </summary>
    /// <param name="root">テスト用（既定は %TEMP%\Ferry）</param>
    public static void CleanUp(DateTime startedAt, string? root = null)
    {
        root ??= Root;
        foreach (var name in new[] { "Preview", "Edit", "drag" })
        {
            var directory = new DirectoryInfo(Path.Combine(root, name));
            if (directory.Exists) CleanUp(directory, startedAt.ToUniversalTime());
        }
    }

    /// <summary>中身を消し、空になったフォルダも消す。戻り値は空になったか</summary>
    private static bool CleanUp(DirectoryInfo directory, DateTime startedAtUtc)
    {
        IEnumerable<FileSystemInfo> children;
        try
        {
            children = directory.EnumerateFileSystemInfos().ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        var empty = true;
        foreach (var child in children)
        {
            try
            {
                // ジャンクションの先は辿らない（リンクそのものだけ消す）
                if (child is DirectoryInfo folder && !folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    if (CleanUp(folder, startedAtUtc)) folder.Delete();
                    else empty = false;
                }
                // ダウンロードしたものは更新日時をサーバーに合わせることがあるので、作った日時でも確かめる
                else if (child.CreationTimeUtc < startedAtUtc && child.LastWriteTimeUtc < startedAtUtc)
                {
                    if (child is FileInfo { IsReadOnly: true } file) file.IsReadOnly = false;
                    child.Delete();
                }
                else
                {
                    empty = false;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                empty = false;
            }
        }
        return empty;
    }
}
