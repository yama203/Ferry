namespace Ferry.Tests;

/// <summary>テストごとの一時フォルダ（終わったら消す）</summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FerryTests-" + Guid.NewGuid().ToString("N"));

    public TempDirectory()
    {
        Directory.CreateDirectory(Path);
    }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public static string Fixture(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

/// <summary>資格情報マネージャーの代わり</summary>
public sealed class MemoryPasswordStore : Ferry.Core.IPasswordStore
{
    public Dictionary<Guid, string> Passwords { get; } = [];
    public string? GetPassword(Guid serverId) => Passwords.GetValueOrDefault(serverId);
    public void SetPassword(Guid serverId, string? password)
    {
        if (password is null) Passwords.Remove(serverId);
        else Passwords[serverId] = password;
    }
}
