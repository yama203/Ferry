using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ferry.Core;

/// <summary>通信の方式。JSON では "ftp" | "ftps" | "ftpsImplicit" | "sftp"（Mac 版と同じ）</summary>
public enum ServerProtocol
{
    Ftp,
    /// <summary>AUTH TLS で途中から暗号化する方式（一般的な FTPS）</summary>
    Ftps,
    /// <summary>接続直後から TLS の方式（通常ポート 990）</summary>
    FtpsImplicit,
    Sftp,
}

public static class ServerProtocolExtensions
{
    public static string DisplayName(this ServerProtocol protocol) => protocol switch
    {
        ServerProtocol.Ftp => "FTP",
        ServerProtocol.Ftps => "FTPS（Explicit）",
        ServerProtocol.FtpsImplicit => "FTPS（Implicit）",
        _ => "SFTP",
    };

    public static int DefaultPort(this ServerProtocol protocol) => protocol switch
    {
        ServerProtocol.Ftp or ServerProtocol.Ftps => 21,
        ServerProtocol.FtpsImplicit => 990,
        _ => 22,
    };

    public static bool IsFtpFamily(this ServerProtocol protocol) => protocol != ServerProtocol.Sftp;

    public static bool UsesTls(this ServerProtocol protocol) =>
        protocol is ServerProtocol.Ftps or ServerProtocol.FtpsImplicit;
}

/// <summary>
/// 接続先。パスワードはここに含めず、資格情報マネージャーに保存する。
/// Mac 版の ServerConfig と同じ形式（キーは camelCase、値が無いキーは省略）。
/// </summary>
public sealed record ServerConfig
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public ServerProtocol ServerProtocol { get; init; } = ServerProtocol.Ftp;
    public string Host { get; init; } = "";
    public int? Port { get; init; }
    public string Username { get; init; } = "";
    /// <summary>接続直後に開くリモートのフォルダ。null ならログイン時のフォルダ。</summary>
    public string? InitialPath { get; init; }
    /// <summary>FTP/FTPS のパッシブモード（既定でオン）。Mac 版では必須のキーなので必ず書く。</summary>
    public bool PassiveMode { get; init; } = true;
    /// <summary>FTPS のサーバー証明書を検証するか。Mac 版では必須のキーなので必ず書く。</summary>
    public bool VerifyCertificate { get; init; } = true;
    /// <summary>SFTP の秘密鍵ファイル。指定時はパスワードを鍵のパスフレーズとして使う。（PC ごとの設定）</summary>
    public string? PrivateKeyPath { get; init; }
    /// <summary>接続直後に開くローカルのフォルダ（PC ごとの設定）</summary>
    public string? LocalPath { get; init; }
    /// <summary>サイドバーのフォルダ。null ならフォルダに入れない。</summary>
    [JsonPropertyName("folderID")]
    public Guid? FolderId { get; init; }
    /// <summary>左右のペインで開く場所の組み合わせ（PC ごとの設定）</summary>
    public IReadOnlyList<PaneBookmark>? Bookmarks { get; init; }
    /// <summary>接続時に開くブックマーク（PC ごとの設定）</summary>
    [JsonPropertyName("startBookmarkID")]
    public Guid? StartBookmarkId { get; init; }

    /// <summary>この版が知らないキー（Mac 版で後から増えたものなど）。書き戻すときに消さない。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    [JsonIgnore]
    public int EffectivePort => Port ?? ServerProtocol.DefaultPort();

    [JsonIgnore]
    public PaneBookmark? StartBookmark => Bookmarks?.FirstOrDefault(b => b.Id == StartBookmarkId);

    /// <summary>読み込みのときに同じ接続先かを判断する（ホスト・ユーザー・ポート・プロトコル）</summary>
    public bool IsSameDestination(ServerConfig other) =>
        Host == other.Host && Username == other.Username
        && EffectivePort == other.EffectivePort && ServerProtocol == other.ServerProtocol;

    public bool Equals(ServerConfig? other) =>
        other is not null && Id == other.Id && Name == other.Name && ServerProtocol == other.ServerProtocol
        && Host == other.Host && Port == other.Port && Username == other.Username
        && InitialPath == other.InitialPath && PassiveMode == other.PassiveMode
        && VerifyCertificate == other.VerifyCertificate && PrivateKeyPath == other.PrivateKeyPath
        && LocalPath == other.LocalPath && FolderId == other.FolderId
        && StartBookmarkId == other.StartBookmarkId
        && (Bookmarks ?? []).SequenceEqual(other.Bookmarks ?? []);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Host, Username);
}

/// <summary>「この PC のフォルダ」と「サーバーのフォルダ」の組み合わせ</summary>
public sealed record PaneBookmark
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string LocalPath { get; init; } = "";
    public string RemotePath { get; init; } = "";
}

/// <summary>サイドバーのフォルダ</summary>
public sealed record ServerFolder
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
}

/// <summary>共有の接続先のうち、PC ごとに違う設定（共有しない）。Mac 版の SharedServerOverride。</summary>
public sealed record SharedServerOverride
{
    public string? LocalPath { get; init; }
    public IReadOnlyList<PaneBookmark>? Bookmarks { get; init; }
    [JsonPropertyName("startBookmarkID")]
    public Guid? StartBookmarkId { get; init; }
    public string? PrivateKeyPath { get; init; }

    public static SharedServerOverride From(ServerConfig server) => new()
    {
        LocalPath = server.LocalPath,
        Bookmarks = server.Bookmarks,
        StartBookmarkId = server.StartBookmarkId,
        PrivateKeyPath = server.PrivateKeyPath,
    };

    public ServerConfig ApplyTo(ServerConfig server) => server with
    {
        LocalPath = LocalPath,
        Bookmarks = Bookmarks,
        StartBookmarkId = StartBookmarkId,
        PrivateKeyPath = PrivateKeyPath,
    };

    [JsonIgnore]
    public bool IsEmpty =>
        LocalPath is null && (Bookmarks is null || Bookmarks.Count == 0)
        && StartBookmarkId is null && PrivateKeyPath is null;

    public bool Equals(SharedServerOverride? other) =>
        other is not null && LocalPath == other.LocalPath && StartBookmarkId == other.StartBookmarkId
        && PrivateKeyPath == other.PrivateKeyPath && (Bookmarks ?? []).SequenceEqual(other.Bookmarks ?? []);

    public override int GetHashCode() => HashCode.Combine(LocalPath, StartBookmarkId, PrivateKeyPath);
}

/// <summary>サーバー上のファイル・フォルダ</summary>
public sealed record RemoteItem
{
    public enum ItemKind { File, Directory, Symlink }

    public required string Name { get; init; }
    public required string Path { get; init; }
    public ItemKind Kind { get; init; }
    public long? Size { get; init; }
    /// <summary>更新日時（UTC）</summary>
    public DateTime? Modified { get; init; }
    /// <summary>"rwxr-xr-x" 形式</summary>
    public string? Permissions { get; init; }
    public string? SymlinkTarget { get; init; }

    public bool IsDirectory => Kind == ItemKind.Directory;
}

/// <summary>サーバー側のパス（区切りは常に "/"）</summary>
public static class RemotePath
{
    public static string Join(string directory, string name) =>
        directory.EndsWith('/') ? directory + name : directory + "/" + name;

    public static string Parent(string path)
    {
        var trimmed = path.Length > 1 && path.EndsWith('/') ? path[..^1] : path;
        var slash = trimmed.LastIndexOf('/');
        if (slash < 0) return "/";
        var parent = trimmed[..slash];
        return parent.Length == 0 ? "/" : parent;
    }

    public static string Name(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "/";
}
