using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Ferry.Core.Import;

/// <summary>
/// WinSCP の保存したセッションを読む。設定はレジストリ（HKCU\Software\Martin Prikryl\WinSCP 2\Sessions）か、
/// WinSCP.ini（ポータブル版や「INI ファイルに保存」の場合）にある。
/// セッション名の「/」はフォルダ。パスワードは独自の方式で隠されているので読まない（接続時に入力する）。
/// </summary>
public static class WinScpImporter
{
    private const string RegistryPath = @"Software\Martin Prikryl\WinSCP 2\Sessions";

    /// <summary>WinSCP.ini の置き場所の候補</summary>
    public static IEnumerable<string> IniCandidates =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinSCP.ini"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WinSCP", "WinSCP.ini"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinSCP", "WinSCP.ini"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinSCP", "WinSCP.ini"),
    ];

    /// <summary>レジストリから読む。WinSCP の設定が無ければ null</summary>
    [SupportedOSPlatform("windows")]
    public static ImportResult? ReadRegistry()
    {
        using var sessions = Registry.CurrentUser.OpenSubKey(RegistryPath);
        if (sessions is null) return null;
        var values = new List<(string Name, IReadOnlyDictionary<string, string> Values)>();
        foreach (var name in sessions.GetSubKeyNames())
        {
            using var key = sessions.OpenSubKey(name);
            if (key is null) continue;
            var dictionary = key.GetValueNames().ToDictionary(v => v, v => Convert.ToString(key.GetValue(v)) ?? "");
            values.Add((name, dictionary));
        }
        return FromSessions(values);
    }

    /// <summary>WinSCP.ini から読む</summary>
    public static ImportResult ReadIni(string path) => ParseIni(File.ReadAllText(path));

    public static ImportResult ParseIni(string text)
    {
        var sessions = new List<(string Name, IReadOnlyDictionary<string, string> Values)>();
        Dictionary<string, string>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var section = line[1..^1];
                current = null;
                if (section.StartsWith(@"Sessions\", StringComparison.Ordinal))
                {
                    current = [];
                    sessions.Add((section[@"Sessions\".Length..], current));
                }
            }
            else if (current is not null && line.IndexOf('=') is > 0 and var equals)
            {
                current[line[..equals]] = line[(equals + 1)..];
            }
        }
        return FromSessions(sessions);
    }

    private static ImportResult FromSessions(IEnumerable<(string Name, IReadOnlyDictionary<string, string> Values)> sessions)
    {
        var result = new ImportResult();
        foreach (var (encodedName, values) in sessions)
        {
            var fullName = Uri.UnescapeDataString(encodedName);
            if (fullName == "Default Settings") continue;
            string? Value(string key) => values.TryGetValue(key, out var value) && value.Length > 0 ? Uri.UnescapeDataString(value) : null;
            int Number(string key, int fallback) => int.TryParse(Value(key), out var number) ? number : fallback;

            // FSProtocol: 0 SCP、1 SFTP（SCP に切り替え可）、2 SFTP、5 FTP、6 WebDAV、7 S3（既定は SFTP）
            // Ftps: 0 なし、1 Implicit、2・3 Explicit
            ServerProtocol? protocol = Number("FSProtocol", 2) switch
            {
                0 or 1 or 2 => ServerProtocol.Sftp,
                5 => Number("Ftps", 0) switch
                {
                    0 => ServerProtocol.Ftp,
                    1 => ServerProtocol.FtpsImplicit,
                    _ => ServerProtocol.Ftps,
                },
                _ => null,
            };
            var host = Value("HostName");
            if (protocol is not { } chosen || host is null)
            {
                result.UnsupportedCount++;
                continue;
            }
            var slash = fullName.LastIndexOf('/');
            var folder = slash > 0 ? fullName[..slash].Replace("/", " / ") : null;
            var name = slash > 0 ? fullName[(slash + 1)..] : fullName;
            var port = Number("PortNumber", chosen.DefaultPort());
            result.Add(folder, new ImportedServer(new ServerConfig
            {
                Name = name,
                ServerProtocol = chosen,
                Host = host,
                Port = port == chosen.DefaultPort() ? null : port,
                Username = Value("UserName") ?? "",
                InitialPath = Value("RemoteDirectory"),
                LocalPath = Value("LocalDirectory"),
                // FtpPasvMode: 1 パッシブ（既定）、0 アクティブ
                PassiveMode = Number("FtpPasvMode", 1) != 0,
                PrivateKeyPath = chosen == ServerProtocol.Sftp ? Value("PublicKeyFile") : null,
            }));
        }
        return result;
    }
}
