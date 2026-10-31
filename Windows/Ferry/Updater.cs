using Velopack;
using Velopack.Sources;

namespace Ferry;

/// <summary>
/// 自動更新（Velopack）。更新の置き場所は会社の共有フォルダの App\Windows（release.ps1 がそこに置く）。
/// GitHub のリポジトリは非公開なので、会社の人は Dropbox から更新を受け取る。
/// </summary>
public static class Updater
{
    /// <summary>今のバージョン（開発中のビルドではプロジェクトのバージョン）</summary>
    public static string CurrentVersion =>
        typeof(Updater).Assembly.GetName().Version is { } version ? $"{version.Major}.{version.Minor}.{version.Build}" : "";

    /// <summary>更新の置き場所（設定が無ければ 共有フォルダ\App\Windows）</summary>
    public static string? UpdateDirectory =>
        AppServices.Settings.UpdateDirectory
        ?? (AppServices.Settings.SharedDirectory is { } shared ? Path.Combine(shared, "App", "Windows") : null);

    private static UpdateManager? _manager;
    private static UpdateInfo? _pending;

    /// <summary>
    /// 更新があればダウンロードしておく。戻り値は新しいバージョン（無ければ null）。
    /// インストールしていない（開発中の）ビルドでは何もしない。
    /// </summary>
    public static async Task<string?> CheckAndDownloadAsync()
    {
        if (UpdateDirectory is not { } directory || !Directory.Exists(directory)) return null;
        try
        {
            var manager = new UpdateManager(new SimpleFileSource(new DirectoryInfo(directory)));
            if (!manager.IsInstalled) return null;
            var update = await manager.CheckForUpdatesAsync();
            if (update is null) return null;
            await manager.DownloadUpdatesAsync(update);
            _manager = manager;
            _pending = update;
            return update.TargetFullRelease.Version.ToString();
        }
        catch (Exception e)
        {
            // 更新できなくても、そのまま使えるようにする
            AppServices.Log(e, "更新の確認");
            return null;
        }
    }

    /// <summary>ダウンロード済みの更新を入れて再起動する</summary>
    public static void ApplyAndRestart()
    {
        if (_manager is null || _pending is null) return;
        _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }
}
