using System.Text.Json.Nodes;
using Ferry.Core;

namespace Ferry.Tests;

/// <summary>資格情報マネージャーの代わり（保管庫の鍵の置き場）</summary>
public sealed class MemorySecretStore : ISecretStore
{
    public Dictionary<string, string> Secrets { get; } = [];
    public string? GetSecret(string name) => Secrets.GetValueOrDefault(name);
    public void SetSecret(string name, string? value)
    {
        if (value is null) Secrets.Remove(name);
        else Secrets[name] = value;
    }
}

public class PasswordVaultTests
{
    private static readonly Guid A = Guid.Parse("6F1C2E1A-1234-4ABC-8DEF-0123456789AB");
    private static readonly Guid B = Guid.Parse("7A2B3C4D-5E6F-4A1B-9C2D-3E4F5A6B7C8D");

    [Fact]
    public void 鍵の作り方はPBKDF2_SHA256()
    {
        // RFC 7914 の PBKDF2-HMAC-SHA256 のテスト用の値（"passwd" / "salt" / 1 回）
        var key = PasswordVault.DeriveKey("passwd", "salt"u8.ToArray(), 1);
        Assert.Equal("55AC046E56E3089FEC1691C22544B605F94185216DDE0465E68B9D57C20DACBC", Convert.ToHexString(key));
    }

    [Fact]
    public void 暗号化はAES_GCMのcombined形式()
    {
        var key = new byte[32];
        var sealedBox = PasswordVault.Seal("{}"u8.ToArray(), key);
        // nonce 12 + 暗号文 2 + タグ 16
        Assert.Equal(30, sealedBox.Length);
        Assert.Equal("{}"u8.ToArray(), PasswordVault.Open(sealedBox, key));
        Assert.Null(PasswordVault.Open(sealedBox, Enumerable.Repeat((byte)1, 32).ToArray()));
    }

    [Fact]
    public void 同期を始めて他のPCでマスターパスワードを入れると読める()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("passwords.vault");
        var macSecrets = new MemorySecretStore();
        using (var first = new PasswordVault(path, macSecrets, "vault-key", watch: false))
        {
            Assert.Equal(PasswordVault.VaultState.Off, first.State);
            first.Enable("マスター", new Dictionary<Guid, string> { [A] = "p@ss" });
            Assert.Equal(PasswordVault.VaultState.Unlocked, first.State);
            first.SetPassword(B, "パスワード");
        }

        // ファイルは Mac 版と同じ形（Data は base64、キーは camelCase）
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(["version", "salt", "iterations", "sealed"], json.Select(p => p.Key));
        Assert.Equal(PasswordVault.DefaultIterations, (int)json["iterations"]!);
        Assert.Equal(16, Convert.FromBase64String((string)json["salt"]!).Length);

        var windows = new MemorySecretStore();
        using var second = new PasswordVault(path, windows, "vault-key", watch: false);
        Assert.Equal(PasswordVault.VaultState.Locked, second.State);
        Assert.Null(second.GetPassword(A));
        Assert.False(second.Unlock("違う"));
        Assert.Equal(PasswordVault.VaultState.Locked, second.State);
        Assert.True(second.Unlock("マスター"));
        Assert.Equal("p@ss", second.GetPassword(A));
        Assert.Equal("パスワード", second.GetPassword(B));
        Assert.Equal(2, second.Count);

        // 鍵を覚えているので、次からはマスターパスワード不要
        using var third = new PasswordVault(path, windows, "vault-key", watch: false);
        Assert.Equal(PasswordVault.VaultState.Unlocked, third.State);
    }

    [Fact]
    public void 書くときは他のPCの変更を読み直してから()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("passwords.vault");
        var secrets = new MemorySecretStore();
        using var mac = new PasswordVault(path, secrets, "k", watch: false);
        mac.Enable("m", new Dictionary<Guid, string>());
        using var windows = new PasswordVault(path, secrets, "k", watch: false);

        mac.SetPassword(A, "from mac");
        windows.SetPassword(B, "from windows");

        mac.Reload();
        Assert.Equal("from mac", mac.GetPassword(A));
        Assert.Equal("from windows", mac.GetPassword(B));
    }

    [Fact]
    public void 追加は入っていないものだけ()
    {
        using var temp = new TempDirectory();
        using var vault = new PasswordVault(temp.Combine("v"), new MemorySecretStore(), "k", watch: false);
        vault.Enable("m", new Dictionary<Guid, string> { [A] = "keep" });

        Assert.Equal(1, vault.Add(new Dictionary<Guid, string> { [A] = "new", [B] = "b" }));
        Assert.Equal("keep", vault.GetPassword(A));
        Assert.Equal("b", vault.GetPassword(B));
    }

    [Fact]
    public void やめるとファイルを消し中身を返す()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("v");
        var secrets = new MemorySecretStore();
        using var vault = new PasswordVault(path, secrets, "k", watch: false);
        vault.Enable("m", new Dictionary<Guid, string> { [A] = "a" });

        var previous = vault.Disable();

        Assert.Equal("a", previous[A]);
        Assert.False(File.Exists(path));
        Assert.Empty(secrets.Secrets);
        Assert.Equal(PasswordVault.VaultState.Off, vault.State);
    }

    [Fact]
    public void Mac版が書いた保管庫のファイルを読める()
    {
        // この PC に iCloud Drive があり、Mac 版がパスワードを同期していれば確かめる
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "iCloudDrive", "Ferry", "passwords.vault");
        if (!File.Exists(path)) return;
        using var vault = new PasswordVault(path, new MemorySecretStore(), "test", watch: false);
        // 鍵が無いので中身は読めないが、ファイルの形は読める（Off ではなく Locked になる）
        Assert.Equal(PasswordVault.VaultState.Locked, vault.State);
    }
}

public class PasswordServiceTests
{
    private static readonly Guid Personal = Guid.NewGuid();
    private static readonly Guid Shared = Guid.NewGuid();

    [Fact]
    public void 共有の接続先は会社の保管庫自分の接続先は自分の保管庫に入る()
    {
        using var temp = new TempDirectory();
        var local = new MemoryPasswordStore();
        using var personalVault = new PasswordVault(temp.Combine("me.vault"), new MemorySecretStore(), "me", watch: false);
        using var sharedVault = new PasswordVault(temp.Combine("company.vault"), new MemorySecretStore(), "company", watch: false);
        personalVault.Enable("me", new Dictionary<Guid, string>());
        sharedVault.Enable("company", new Dictionary<Guid, string>());
        var service = new PasswordService(local) { Personal = personalVault, Shared = sharedVault, IsShared = id => id == Shared };

        service.SetPassword(Personal, "p");
        service.SetPassword(Shared, "s");

        Assert.Equal("p", personalVault.GetPassword(Personal));
        Assert.Null(sharedVault.GetPassword(Personal));
        Assert.Equal("s", sharedVault.GetPassword(Shared));
        Assert.Null(personalVault.GetPassword(Shared));
        // この PC にも保存する
        Assert.Equal("p", local.GetPassword(Personal));
        Assert.Equal("s", local.GetPassword(Shared));
    }

    [Fact]
    public void 保管庫を先に見てこのPCにだけあるものは使ったときに追加する()
    {
        using var temp = new TempDirectory();
        var local = new MemoryPasswordStore();
        using var sharedVault = new PasswordVault(temp.Combine("company.vault"), new MemorySecretStore(), "company", watch: false);
        sharedVault.Enable("company", new Dictionary<Guid, string> { [Shared] = "vault" });
        local.SetPassword(Shared, "old local");
        var other = Guid.NewGuid();
        local.SetPassword(other, "only here");
        var service = new PasswordService(local) { Shared = sharedVault, IsShared = _ => true };

        Assert.Equal("vault", service.GetPassword(Shared));
        Assert.Equal("only here", service.GetPassword(other));
        Assert.Equal("only here", sharedVault.GetPassword(other));
    }

    [Fact]
    public void 保管庫がロック中ならこのPCのパスワードを使う()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("v");
        using (var setup = new PasswordVault(path, new MemorySecretStore(), "k", watch: false))
            setup.Enable("m", new Dictionary<Guid, string> { [Personal] = "vault" });
        using var locked = new PasswordVault(path, new MemorySecretStore(), "k", watch: false);
        var local = new MemoryPasswordStore();
        local.SetPassword(Personal, "local");
        var service = new PasswordService(local) { Personal = locked };

        Assert.Equal("local", service.GetPassword(Personal));
    }
}
