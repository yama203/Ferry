using System.Text;
using System.Xml.Linq;

namespace Ferry.Core.Import;

/// <summary>
/// FileZilla のサイトマネージャー（%APPDATA%\FileZilla\sitemanager.xml）を読む。
/// フォルダは入れ子を「親 / 子」の名前にまとめる。パスワードは base64 のものだけ読む
/// （マスターパスワードで暗号化されたものは読めない）。
/// </summary>
public static class FileZillaImporter
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileZilla", "sitemanager.xml");

    public static ImportResult Read(string path) => Parse(File.ReadAllText(path));

    public static ImportResult Parse(string xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException e)
        {
            throw new InvalidDataException("FileZilla のサイトマネージャーのファイルではありません。", e);
        }
        var servers = document.Root?.Element("Servers")
                      ?? throw new InvalidDataException("FileZilla のサイトマネージャーのファイルではありません。");
        var result = new ImportResult();
        ReadContainer(servers, null, result);
        return result;
    }

    private static void ReadContainer(XElement container, string? folderName, ImportResult result)
    {
        foreach (var element in container.Elements())
        {
            if (element.Name == "Server")
            {
                ReadServer(element, folderName, result);
            }
            else if (element.Name == "Folder")
            {
                // フォルダ名は子要素の前にある文字列
                var name = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
                ReadContainer(element, folderName is null ? name : $"{folderName} / {name}", result);
            }
        }
    }

    private static void ReadServer(XElement element, string? folderName, ImportResult result)
    {
        string? Value(string name) => element.Element(name)?.Value is { Length: > 0 } value ? value : null;

        // 0: FTP（可能なら TLS）、1: SFTP、3: FTPS（Implicit）、4: FTPS（Explicit）、6: 暗号化しない FTP
        ServerProtocol? protocol = Value("Protocol") switch
        {
            null or "0" or "6" => ServerProtocol.Ftp,
            "1" => ServerProtocol.Sftp,
            "3" => ServerProtocol.FtpsImplicit,
            "4" => ServerProtocol.Ftps,
            _ => null,
        };
        var host = Value("Host");
        if (protocol is not { } chosen || host is null)
        {
            result.UnsupportedCount++;
            return;
        }
        var port = int.TryParse(Value("Port"), out var p) && p != chosen.DefaultPort() ? p : (int?)null;
        var server = new ServerConfig
        {
            Name = Value("Name") ?? host,
            ServerProtocol = chosen,
            Host = host,
            Port = port,
            Username = Value("User") ?? "",
            InitialPath = DecodeRemoteDir(Value("RemoteDir")),
            LocalPath = Value("LocalDir"),
            PassiveMode = Value("PasvMode") != "MODE_ACTIVE",
            PrivateKeyPath = chosen == ServerProtocol.Sftp ? Value("Keyfile") : null,
        };

        string? password = null;
        if (element.Element("Pass") is { } pass && pass.Value.Length > 0)
        {
            var encoding = pass.Attribute("encoding")?.Value;
            if (encoding == "base64")
            {
                try { password = Encoding.UTF8.GetString(Convert.FromBase64String(pass.Value)); }
                catch (FormatException) { }
            }
            else if (encoding == "crypt")
            {
                result.EncryptedPasswordCount++;
            }
            else
            {
                password = pass.Value;
            }
        }
        // 匿名ログイン（Logontype 0）はユーザー名 anonymous
        if (Value("Logontype") == "0" && server.Username.Length == 0) server = server with { Username = "anonymous" };
        result.Add(folderName, new ImportedServer(server, password));
    }

    /// <summary>
    /// RemoteDir は「種類 接頭辞の長さ [接頭辞] (長さ 名前)...」の形。例: "1 0 3 www 4 html" → "/www/html"。
    /// 名前に空白を含むことがあるので、長さを使って切り出す。
    /// </summary>
    public static string? DecodeRemoteDir(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var position = 0;

        string? NextToken()
        {
            while (position < value.Length && value[position] == ' ') position++;
            var start = position;
            while (position < value.Length && value[position] != ' ') position++;
            return position > start ? value[start..position] : null;
        }

        string? Take(int length)
        {
            if (position < value.Length && value[position] == ' ') position++;
            if (position + length > value.Length) return null;
            var text = value.Substring(position, length);
            position += length;
            return text;
        }

        if (NextToken() is null) return null;                       // 種類（1: Unix など）
        if (!int.TryParse(NextToken(), out var prefixLength)) return null;
        if (prefixLength > 0) Take(prefixLength);                    // 接頭辞（VMS など。Unix では無い）
        var segments = new List<string>();
        while (NextToken() is { } lengthText && int.TryParse(lengthText, out var length))
        {
            if (Take(length) is not { } segment) return null;
            segments.Add(segment);
        }
        return "/" + string.Join("/", segments);
    }
}
