using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ferry.Core;

/// <summary>
/// 同期するパスワードの保管庫。マスターパスワードから作った鍵で暗号化してファイルに保存する（Mac 版の PasswordVault と同じ形式）。
/// <list type="bullet">
/// <item>自分の保管庫: iCloud Drive\Ferry\passwords.vault（自分の Mac・Windows の間で同期）</item>
/// <item>会社の保管庫: &lt;共有フォルダ&gt;\passwords.vault（会社のマスターパスワードを知っている人の間で同期）</item>
/// </list>
/// 鍵は各 PC の資格情報マネージャーに保存するので、マスターパスワードの入力は PC ごとに 1 回だけ。
/// <code>
/// {"version":1, "salt":"&lt;base64&gt;", "iterations":600000, "sealed":"&lt;base64&gt;"}
/// sealed = AES-256-GCM（nonce 12 バイト + 暗号文 + タグ 16 バイト）で暗号化した {"サーバーの ID（大文字）": "パスワード"} の JSON
/// 鍵 = PBKDF2-HMAC-SHA256（マスターパスワードの UTF-8、salt 16 バイト、iterations 回、32 バイト）
/// </code>
/// </summary>
public sealed class PasswordVault : IDisposable
{
    public enum VaultState
    {
        /// <summary>同期していない（ファイルが無い）</summary>
        Off,
        /// <summary>他の PC で同期が設定されているが、この PC ではまだマスターパスワードを入力していない</summary>
        Locked,
        Unlocked,
    }

    public const int DefaultIterations = 600_000;

    public string FilePath { get; }
    public VaultState State { get; private set; } = VaultState.Off;
    public int Count => _passwords.Count;

    /// <summary>中身や状態が変わったとき（他の PC の変更が届いたときも）。UI スレッドとは限らない。</summary>
    public event EventHandler? Changed;

    private readonly ISecretStore _secrets;
    private readonly string _keyName;
    private byte[]? _key;
    private Dictionary<string, string> _passwords = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    private sealed class VaultFile
    {
        public int Version { get; set; } = 1;
        public byte[] Salt { get; set; } = [];
        public int Iterations { get; set; }
        public byte[] Sealed { get; set; } = [];
    }

    /// <param name="filePath">passwords.vault の場所</param>
    /// <param name="secrets">マスターパスワードから作った鍵の保存先（この PC だけ）</param>
    /// <param name="keyName">鍵の名前（自分用と会社用で分ける）</param>
    public PasswordVault(string filePath, ISecretStore secrets, string keyName, bool watch = true)
    {
        FilePath = filePath;
        _secrets = secrets;
        _keyName = keyName;
        if (secrets.GetSecret(keyName) is { } stored)
        {
            try { _key = Convert.FromBase64String(stored); } catch (FormatException) { }
        }
        Reload();
        if (watch) StartWatching();
    }

    // MARK: パスワード

    public string? GetPassword(Guid id)
    {
        lock (_lock)
        {
            return State == VaultState.Unlocked && _passwords.TryGetValue(FerryJson.IdString(id), out var password) ? password : null;
        }
    }

    public bool Contains(Guid id)
    {
        lock (_lock) return _passwords.ContainsKey(FerryJson.IdString(id));
    }

    /// <summary>空文字や null で削除。同期していなければ何もしない。</summary>
    public void SetPassword(Guid id, string? password)
    {
        lock (_lock)
        {
            if (State != VaultState.Unlocked) return;
            // 他の PC での変更を取りこぼさないよう、最新を読んでから書く
            ReloadLocked();
            if (string.IsNullOrEmpty(password)) _passwords.Remove(FerryJson.IdString(id));
            else _passwords[FerryJson.IdString(id)] = password;
            PersistLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>まだ入っていないパスワードをまとめて追加する（入っているものは変えない）。戻り値は追加した件数</summary>
    public int Add(IReadOnlyDictionary<Guid, string> entries)
    {
        var added = 0;
        lock (_lock)
        {
            if (State != VaultState.Unlocked || entries.Count == 0) return 0;
            ReloadLocked();
            foreach (var (id, password) in entries)
            {
                if (password.Length == 0 || !_passwords.TryAdd(FerryJson.IdString(id), password)) continue;
                added++;
            }
            if (added > 0) PersistLocked();
        }
        if (added > 0) Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    // MARK: 開始・解除・停止

    /// <summary>同期を始める（ファイルを新しく作る）。initial を最初の中身にする。</summary>
    public void Enable(string masterPassword, IReadOnlyDictionary<Guid, string> initial)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        lock (_lock)
        {
            _key = DeriveKey(masterPassword, salt, DefaultIterations);
            _passwords = initial.Where(p => p.Value.Length > 0)
                .ToDictionary(p => FerryJson.IdString(p.Key), p => p.Value, StringComparer.OrdinalIgnoreCase);
            WriteFile(salt, DefaultIterations);
            _secrets.SetSecret(_keyName, Convert.ToBase64String(_key));
            ReloadLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>他の PC で始めた同期を、この PC でも使えるようにする。マスターパスワードが違えば false</summary>
    public bool Unlock(string masterPassword)
    {
        lock (_lock)
        {
            if (ReadFile() is not { } file) throw new InvalidDataException("パスワードの保管ファイルを読めませんでした。");
            var key = DeriveKey(masterPassword, file.Salt, file.Iterations);
            if (Open(file.Sealed, key) is null) return false;
            _key = key;
            _secrets.SetSecret(_keyName, Convert.ToBase64String(key));
            ReloadLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>この PC だけ同期をやめる（鍵を忘れる。ファイルや他の PC には影響しない）</summary>
    public void Forget()
    {
        lock (_lock)
        {
            _secrets.SetSecret(_keyName, null);
            _key = null;
            ReloadLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>同期をやめる（すべての PC で）。戻り値はそれまで同期していたパスワード（この PC に残す用）</summary>
    public IReadOnlyDictionary<Guid, string> Disable()
    {
        Dictionary<Guid, string> previous;
        lock (_lock)
        {
            previous = State == VaultState.Unlocked
                ? _passwords.Where(p => Guid.TryParse(p.Key, out _)).ToDictionary(p => Guid.Parse(p.Key), p => p.Value)
                : [];
            try { File.Delete(FilePath); } catch (IOException) { }
            _secrets.SetSecret(_keyName, null);
            _key = null;
            ReloadLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return previous;
    }

    // MARK: 読み書き

    public void Reload()
    {
        VaultState before;
        int countBefore;
        lock (_lock)
        {
            before = State;
            countBefore = _passwords.Count;
            ReloadLocked();
        }
        if (before != State || countBefore != _passwords.Count) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ReloadLocked()
    {
        if (ReadFile() is not { } file)
        {
            State = VaultState.Off;
            _passwords = new(StringComparer.OrdinalIgnoreCase);
            return;
        }
        if (_key is null || Open(file.Sealed, _key) is not { } plain)
        {
            // 鍵が無い、またはマスターパスワードが変わった
            State = VaultState.Locked;
            _passwords = new(StringComparer.OrdinalIgnoreCase);
            return;
        }
        try
        {
            var dictionary = JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? [];
            _passwords = new(dictionary, StringComparer.OrdinalIgnoreCase);
            State = VaultState.Unlocked;
        }
        catch (JsonException)
        {
            State = VaultState.Locked;
            _passwords = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private VaultFile? ReadFile()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var file = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(FilePath),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            return file is { Salt.Length: > 0, Sealed.Length: > 28, Iterations: > 0 } ? file : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>既存のファイルと同じ salt で書き直す</summary>
    private void PersistLocked()
    {
        if (ReadFile() is { } file) WriteFile(file.Salt, file.Iterations);
    }

    private void WriteFile(byte[] salt, int iterations)
    {
        if (_key is null) return;
        var plain = JsonSerializer.SerializeToUtf8Bytes(_passwords);
        var json = JsonSerializer.Serialize(new VaultFile { Salt = salt, Iterations = iterations, Sealed = Seal(plain, _key) },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        FerryJson.WriteAtomically(FilePath, json);
    }

    // MARK: 暗号（Mac 版の CryptoKit と同じ形）

    public static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>AES-GCM の combined 形式（nonce 12 + 暗号文 + タグ 16）</summary>
    public static byte[] Seal(byte[] plain, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
        return [.. nonce, .. cipher, .. tag];
    }

    /// <summary>鍵が違えば null</summary>
    public static byte[]? Open(byte[] sealedBox, byte[] key)
    {
        if (sealedBox.Length < 28) return null;
        var nonce = sealedBox[..12];
        var tag = sealedBox[^16..];
        var cipher = sealedBox[12..^16];
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return plain;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
    }

    // MARK: 監視（他の PC の変更が iCloud・Dropbox から届いたら読み直す）

    private void StartWatching()
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (directory is null || !Directory.Exists(directory)) return;
        _debounce = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(directory, Path.GetFileName(FilePath))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        FileSystemEventHandler onChange = (_, _) => _debounce?.Change(300, Timeout.Infinite);
        _watcher.Changed += onChange;
        _watcher.Created += onChange;
        _watcher.Deleted += onChange;
        _watcher.Renamed += (_, _) => _debounce?.Change(300, Timeout.Infinite);
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
