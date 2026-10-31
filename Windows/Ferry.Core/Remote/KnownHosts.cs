namespace Ferry.Core.Remote;

/// <summary>
/// SFTP のホスト鍵の記録。初めてのホストは鍵を記録し、2 回目以降に鍵が変わっていたら接続を止める。
/// キーは "ホスト:ポート"、値は鍵の種類ごとの SHA256 フィンガープリント。
/// </summary>
public sealed class KnownHosts
{
    private readonly string _path;
    private static readonly object FileLock = new();

    public KnownHosts(string path)
    {
        _path = path;
    }

    public enum Result { Trusted, Added, Changed }

    /// <summary>鍵を照合し、初めてのホストなら記録する</summary>
    public Result Check(string host, int port, string keyType, string fingerprint)
    {
        var hostKey = $"{host.ToLowerInvariant()}:{port}";
        lock (FileLock)
        {
            var hosts = Load();
            if (hosts.TryGetValue(hostKey, out var keys) && keys.TryGetValue(keyType, out var known))
            {
                return known == fingerprint ? Result.Trusted : Result.Changed;
            }
            // 同じホストで別の種類の鍵だけを記録していた場合も、すり替えの可能性があるので止める
            if (keys is { Count: > 0 }) return Result.Changed;
            hosts[hostKey] = new Dictionary<string, string> { [keyType] = fingerprint };
            FerryJson.WriteAtomically(_path, FerryJson.Serialize(hosts));
            return Result.Added;
        }
    }

    /// <summary>記録を消す（サーバーを移転したときなど）</summary>
    public void Forget(string host, int port)
    {
        lock (FileLock)
        {
            var hosts = Load();
            if (hosts.Remove($"{host.ToLowerInvariant()}:{port}"))
                FerryJson.WriteAtomically(_path, FerryJson.Serialize(hosts));
        }
    }

    private Dictionary<string, Dictionary<string, string>> Load()
    {
        try
        {
            return FerryJson.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(_path)) ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return [];
        }
    }
}
