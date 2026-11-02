using Ferry.Core;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Ferry;

/// <summary>
/// 自動更新（Velopack）。更新の置き場所は会社の共有フォルダの App\Windows（release.ps1 がそこに置く）。
/// GitHub のリポジトリは非公開なので、会社の人は Dropbox から更新を受け取る。
/// 共有フォルダに書き込める人なら誰でも更新を置けてしまうので、release.ps1 が署名した一覧（.sig）だけを使う（ReleaseSignature）。
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

    /// <summary>起動中に確かめる間隔</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    public enum Status
    {
        /// <summary>新しいバージョンをダウンロードした（再起動で入る）</summary>
        Downloaded,
        UpToDate,
        /// <summary>更新の置き場所が無い（共有フォルダを設定していない、など）</summary>
        NoSource,
        /// <summary>インストールしていない（開発中の）ビルド</summary>
        NotInstalled,
        /// <summary>更新の一覧の署名を確かめられなかった（開発者以外が置いた可能性がある）</summary>
        Unverified,
        Failed,
    }

    public record Result(Status Status, string? Version = null);

    private static UpdateManager? _manager;
    private static UpdateInfo? _pending;

    /// <summary>更新があればダウンロードしておく</summary>
    public static async Task<Result> CheckAndDownloadAsync()
    {
        if (UpdateDirectory is not { } directory || !Directory.Exists(directory)) return new(Status.NoSource);
        try
        {
            if (PublicKey is not { } publicKey) return new(Status.Unverified);
            var manager = new UpdateManager(new SignedFileSource(new DirectoryInfo(directory), publicKey));
            if (!manager.IsInstalled) return new(Status.NotInstalled);
            var update = await manager.CheckForUpdatesAsync();
            if (update is null) return new(Status.UpToDate);
            var version = update.TargetFullRelease.Version.ToString();
            // 前に確かめたときにダウンロード済み
            if (_pending?.TargetFullRelease.Version.ToString() == version) return new(Status.Downloaded, version);
            await manager.DownloadUpdatesAsync(update);
            _manager = manager;
            _pending = update;
            return new(Status.Downloaded, version);
        }
        catch (ReleaseSignatureException e)
        {
            AppServices.Log(e, "更新の署名の確認");
            return new(Status.Unverified);
        }
        catch (Exception e)
        {
            // 更新できなくても、そのまま使えるようにする
            AppServices.Log(e, "更新の確認");
            return new(Status.Failed);
        }
    }

    /// <summary>ダウンロード済みの更新を入れて再起動する</summary>
    public static void ApplyAndRestart()
    {
        if (_manager is null || _pending is null) return;
        _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }

    /// <summary>組み込んだ公開鍵（release.ps1 が最初のリリースで作る release-public-key.txt）</summary>
    private static string? PublicKey
    {
        get
        {
            using var stream = typeof(Updater).Assembly.GetManifestResourceStream("release-public-key.txt");
            if (stream is null) return null;
            var key = new StreamReader(stream).ReadToEnd().Trim();
            return key.Length > 0 ? key : null;
        }
    }

    /// <summary>共有フォルダの更新のうち、署名を確かめた一覧だけを使う（パッケージは一覧の SHA-256 と Velopack が照合する）</summary>
    private sealed class SignedFileSource(DirectoryInfo directory, string publicKey) : SimpleFileSource(directory)
    {
        public override Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
            Guid? stagingId = null, VelopackAsset? latestLocalRelease = null) =>
            Task.FromResult(VelopackAssetFeed.FromJson(ReleaseSignature.ReadVerifiedFeed(BaseDirectory.FullName, channel, publicKey)));
    }
}
