namespace Ferry.Core;

/// <summary>
/// パスワードの読み書きの窓口。この PC（資格情報マネージャー）と、同期する保管庫をまとめて扱う。
/// <list type="bullet">
/// <item>共有の接続先: 会社の保管庫 → 自分の保管庫 → この PC の順に探す。保存は この PC ＋ 会社の保管庫</item>
/// <item>自分の接続先: 自分の保管庫 → この PC の順に探す。保存は この PC ＋ 自分の保管庫</item>
/// </list>
/// この PC にだけあるパスワードは、使ったときに保管庫へ追加する（Mac 版と同じ）。
/// </summary>
public sealed class PasswordService : IPasswordStore
{
    private readonly IPasswordStore _local;

    /// <summary>自分の保管庫（iCloud Drive。無ければ null）</summary>
    public PasswordVault? Personal { get; set; }
    /// <summary>会社の保管庫（共有フォルダ。共有していなければ null）</summary>
    public PasswordVault? Shared { get; set; }
    /// <summary>接続先が会社の共有かどうか（ServerStore を作った後に設定する）</summary>
    public Func<Guid, bool> IsShared { get; set; } = _ => false;

    public PasswordService(IPasswordStore local)
    {
        _local = local;
    }

    private PasswordVault? VaultFor(Guid id) => IsShared(id) ? Shared : Personal;

    public string? GetPassword(Guid serverId)
    {
        if (IsShared(serverId) && Shared?.GetPassword(serverId) is { } shared) return shared;
        if (Personal?.GetPassword(serverId) is { } personal) return personal;
        var local = _local.GetPassword(serverId);
        // この PC にだけあるパスワードは、使ったときに同期へ追加する
        if (local is { Length: > 0 } && VaultFor(serverId) is { State: PasswordVault.VaultState.Unlocked } vault)
        {
            vault.Add(new Dictionary<Guid, string> { [serverId] = local });
        }
        return local;
    }

    public void SetPassword(Guid serverId, string? password)
    {
        _local.SetPassword(serverId, password);
        VaultFor(serverId)?.SetPassword(serverId, password);
    }

    /// <summary>この PC にだけあるパスワードを保管庫へまとめて追加する（同期を始めたときなど）。戻り値は追加した件数</summary>
    public int AddLocalPasswords(PasswordVault vault, IEnumerable<Guid> serverIds)
    {
        var entries = new Dictionary<Guid, string>();
        foreach (var id in serverIds)
        {
            if (!vault.Contains(id) && _local.GetPassword(id) is { Length: > 0 } password) entries[id] = password;
        }
        return vault.Add(entries);
    }

    /// <summary>この PC のパスワード（保管庫に入れる最初の中身など）</summary>
    public Dictionary<Guid, string> LocalPasswords(IEnumerable<Guid> serverIds)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var id in serverIds)
        {
            if (_local.GetPassword(id) is { Length: > 0 } password) result[id] = password;
        }
        return result;
    }

    /// <summary>この PC にパスワードを保存する（保管庫には書かない。同期をやめたときに残す用）</summary>
    public void SetLocalPassword(Guid serverId, string? password) => _local.SetPassword(serverId, password);
}
