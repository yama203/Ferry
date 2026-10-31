namespace Ferry.Core.Import;

/// <summary>他のアプリから読み込んだ接続先</summary>
public sealed class ImportResult
{
    public sealed record Folder(string? Name, List<ImportedServer> Servers);

    public List<Folder> Folders { get; } = [];
    /// <summary>対応していないプロトコル（WebDAV・S3 など）で読み込めなかった数</summary>
    public int UnsupportedCount { get; set; }
    /// <summary>パスワードが暗号化されていて読めなかった数（FileZilla のマスターパスワードなど）</summary>
    public int EncryptedPasswordCount { get; set; }

    public int ServerCount => Folders.Sum(f => f.Servers.Count);
    public IEnumerable<ImportedServer> AllServers => Folders.SelectMany(f => f.Servers);

    /// <summary>同じフォルダ名のまとまりに入れる（null はフォルダなし）</summary>
    public void Add(string? folderName, ImportedServer server)
    {
        var folder = Folders.FirstOrDefault(f => f.Name == folderName);
        if (folder is null)
        {
            folder = new Folder(folderName, []);
            Folders.Add(folder);
        }
        folder.Servers.Add(server);
    }
}

/// <summary>読み込んだ接続先 1 件（パスワードを読めた場合は一緒に持つ）</summary>
public sealed record ImportedServer(ServerConfig Server, string? Password = null);
