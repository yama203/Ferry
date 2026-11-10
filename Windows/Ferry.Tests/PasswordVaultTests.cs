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

    // MARK: マスターパスワードの変更

    [Fact]
    public void マスターパスワードを変えると新しいパスワードで別のPCから開ける()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("passwords.vault");
        var secrets = new MemorySecretStore();
        using var vault = new PasswordVault(path, secrets, "k", watch: false);
        vault.Enable("古いマスター", new Dictionary<Guid, string> { [A] = "a", [B] = "b" });
        var oldSalt = (string)JsonNode.Parse(File.ReadAllText(path))!["salt"]!;
        var oldKey = secrets.Secrets["k"];

        Assert.True(vault.ChangeMasterPassword("古いマスター", "新しいマスター"));

        Assert.Equal(PasswordVault.VaultState.Unlocked, vault.State);
        Assert.Equal("a", vault.GetPassword(A));
        // 形式は同じで、salt と保存した鍵は新しくなる
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(["version", "salt", "iterations", "sealed"], json.Select(p => p.Key));
        Assert.Equal(1, (int)json["version"]!);
        Assert.NotEqual(oldSalt, (string)json["salt"]!);
        Assert.Equal(16, Convert.FromBase64String((string)json["salt"]!).Length);
        Assert.NotEqual(oldKey, secrets.Secrets["k"]);
        // 次に起動しても新しい鍵で開ける
        using (var restarted = new PasswordVault(path, secrets, "k", watch: false))
            Assert.Equal(PasswordVault.VaultState.Unlocked, restarted.State);

        using var other = new PasswordVault(path, new MemorySecretStore(), "k", watch: false);
        Assert.Equal(PasswordVault.VaultState.Locked, other.State);
        Assert.False(other.HasOutdatedKey);
        Assert.True(other.Unlock("新しいマスター"));
        Assert.Equal("a", other.GetPassword(A));
        Assert.Equal("b", other.GetPassword(B));
        Assert.Equal(2, other.Count);
    }

    [Fact]
    public void 変更後は古い鍵を持つ別のPCはロック中になり古いパスワードでは開けない()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("passwords.vault");
        using var mine = new PasswordVault(path, new MemorySecretStore(), "k", watch: false);
        mine.Enable("古いマスター", new Dictionary<Guid, string> { [A] = "a" });
        var otherSecrets = new MemorySecretStore();
        using var other = new PasswordVault(path, otherSecrets, "k", watch: false);
        Assert.True(other.Unlock("古いマスター"));
        Assert.False(other.HasOutdatedKey);

        Assert.True(mine.ChangeMasterPassword("古いマスター", "新しいマスター"));

        // 他の PC の変更が届いた（監視で読み直す）
        other.Reload();
        Assert.Equal(PasswordVault.VaultState.Locked, other.State);
        Assert.True(other.HasOutdatedKey);
        Assert.Null(other.GetPassword(A));
        // 鍵を覚えていても、次に起動したときもロック中
        using (var restarted = new PasswordVault(path, otherSecrets, "k", watch: false))
        {
            Assert.Equal(PasswordVault.VaultState.Locked, restarted.State);
            Assert.True(restarted.HasOutdatedKey);
        }
        // 書こうとしても書かない（古い鍵で上書きしない）
        var before = File.ReadAllText(path);
        other.SetPassword(B, "b");
        Assert.Equal(before, File.ReadAllText(path));

        Assert.False(other.Unlock("古いマスター"));
        Assert.True(other.Unlock("新しいマスター"));
        Assert.False(other.HasOutdatedKey);
        Assert.Equal("a", other.GetPassword(A));
    }

    [Fact]
    public void 今のマスターパスワードが違うと変更せずファイルも鍵も元のまま()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("passwords.vault");
        var secrets = new MemorySecretStore();
        using var vault = new PasswordVault(path, secrets, "k", watch: false);
        vault.Enable("マスター", new Dictionary<Guid, string> { [A] = "a" });
        var file = File.ReadAllText(path);
        var key = secrets.Secrets["k"];

        Assert.False(vault.ChangeMasterPassword("違う", "新しいマスター"));

        Assert.Equal(file, File.ReadAllText(path));
        Assert.Equal(key, secrets.Secrets["k"]);
        Assert.Equal(PasswordVault.VaultState.Unlocked, vault.State);
        Assert.Equal("a", vault.GetPassword(A));
        using var other = new PasswordVault(path, new MemorySecretStore(), "k", watch: false);
        Assert.False(other.Unlock("新しいマスター"));
        Assert.True(other.Unlock("マスター"));
    }

    [Fact]
    public void 変更の直前に別のPCが足したパスワードも残る()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("passwords.vault");
        using var mine = new PasswordVault(path, new MemorySecretStore(), "k", watch: false);
        mine.Enable("マスター", new Dictionary<Guid, string> { [A] = "a" });
        using var other = new PasswordVault(path, new MemorySecretStore(), "k", watch: false);
        Assert.True(other.Unlock("マスター"));
        other.SetPassword(B, "他の PC で追加");
        // mine はまだ読み直していない
        Assert.Null(mine.GetPassword(B));

        Assert.True(mine.ChangeMasterPassword("マスター", "新しいマスター"));

        Assert.Equal("他の PC で追加", mine.GetPassword(B));
        using var third = new PasswordVault(path, new MemorySecretStore(), "k", watch: false);
        Assert.True(third.Unlock("新しいマスター"));
        Assert.Equal("a", third.GetPassword(A));
        Assert.Equal("他の PC で追加", third.GetPassword(B));
    }

    [Fact]
    public void ロック中や同期していないときは変更できない()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("passwords.vault");
        var offSecrets = new MemorySecretStore();
        using (var off = new PasswordVault(path, offSecrets, "k", watch: false))
        {
            Assert.Throws<InvalidOperationException>(() => off.ChangeMasterPassword("", "新しいマスター"));
            Assert.False(File.Exists(path));
            Assert.Empty(offSecrets.Secrets);
        }

        using (var setup = new PasswordVault(path, new MemorySecretStore(), "k", watch: false))
            setup.Enable("マスター", new Dictionary<Guid, string> { [A] = "a" });
        var file = File.ReadAllText(path);
        var lockedSecrets = new MemorySecretStore();
        using var locked = new PasswordVault(path, lockedSecrets, "k", watch: false);
        Assert.Equal(PasswordVault.VaultState.Locked, locked.State);
        // 今のマスターパスワードが正しくても、開いていなければ変えない
        Assert.Throws<InvalidOperationException>(() => locked.ChangeMasterPassword("マスター", "新しいマスター"));
        Assert.Equal(file, File.ReadAllText(path));
        Assert.Empty(lockedSecrets.Secrets);
        Assert.Equal(PasswordVault.VaultState.Locked, locked.State);
    }

    [Fact]
    public void 書き込みに失敗したら元の状態のまま()
    {
        using var temp = new TempDirectory();
        var folder = temp.Combine("shared");
        var path = Path.Combine(folder, "passwords.vault");
        var secrets = new MemorySecretStore();
        using var vault = new PasswordVault(path, secrets, "k", watch: false);
        vault.Enable("マスター", new Dictionary<Guid, string> { [A] = "a" });
        var file = File.ReadAllText(path);
        var key = secrets.Secrets["k"];

        // 書き込めないようにする（Mac・Linux はフォルダを読み取り専用に、Windows はファイルを読み取り専用に）
        if (OperatingSystem.IsWindows()) File.SetAttributes(path, FileAttributes.ReadOnly);
        else File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var error = Assert.ThrowsAny<Exception>(() => vault.ChangeMasterPassword("マスター", "新しいマスター"));
            Assert.True(error is IOException or UnauthorizedAccessException, error.GetType().Name);
        }
        finally
        {
            if (OperatingSystem.IsWindows()) File.SetAttributes(path, FileAttributes.Normal);
            else File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal(file, File.ReadAllText(path));
        Assert.Equal(key, secrets.Secrets["k"]);
        // 一時ファイルも残さない
        Assert.Equal(["passwords.vault"], Directory.GetFiles(folder).Select(Path.GetFileName));
        // メモリの鍵も元のまま（読み直しても開けて、書き込める）
        vault.Reload();
        Assert.Equal(PasswordVault.VaultState.Unlocked, vault.State);
        vault.SetPassword(B, "b");
        using var other = new PasswordVault(path, new MemorySecretStore(), "k", watch: false);
        Assert.True(other.Unlock("マスター"));
        Assert.Equal("b", other.GetPassword(B));
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
    public void 会社の同期を始めるときは自分の保管庫にしか無いパスワードも入れる()
    {
        using var temp = new TempDirectory();
        var local = new MemoryPasswordStore();
        using var personalVault = new PasswordVault(temp.Combine("me.vault"), new MemorySecretStore(), "me", watch: false);
        using var sharedVault = new PasswordVault(temp.Combine("company.vault"), new MemorySecretStore(), "company", watch: false);
        var macOnly = Guid.NewGuid();
        var pcOnly = Guid.NewGuid();
        var nowhere = Guid.NewGuid();
        // Mac で保存して、自分の保管庫にだけあるもの
        personalVault.Enable("me", new Dictionary<Guid, string> { [macOnly] = "from mac" });
        local.SetPassword(pcOnly, "from pc");
        var service = new PasswordService(local) { Personal = personalVault, Shared = sharedVault, IsShared = _ => true };

        var initial = service.KnownPasswords([macOnly, pcOnly, nowhere]);
        sharedVault.Enable("company", initial);

        Assert.Equal("from mac", sharedVault.GetPassword(macOnly));
        Assert.Equal("from pc", sharedVault.GetPassword(pcOnly));
        Assert.Equal(2, sharedVault.Count);
        Assert.Equal(0, service.AddKnownPasswords(sharedVault, [macOnly, pcOnly, nowhere]));
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
