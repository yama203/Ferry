namespace Ferry.Core;

/// <summary>
/// フォルダを転送・同期するときに含めないファイル名のパターン（* と ? が使える。名前だけで判定）。
/// 明示的に選んだ項目そのものには適用せず、フォルダの中身にだけ適用する。
/// Mac 版の fnmatch(pattern, name, 0) と同じく、大文字・小文字を区別し、先頭の "." も * に含まれる。
/// </summary>
public sealed class ExcludeRules : IEquatable<ExcludeRules>
{
    public IReadOnlyList<string> Patterns { get; }

    public ExcludeRules(IEnumerable<string> patterns)
    {
        Patterns = patterns.ToArray();
    }

    public static readonly ExcludeRules Defaults = new([
        ".git", ".svn", ".hg",
        "node_modules",
        ".DS_Store", "._*", "Thumbs.db", "desktop.ini",
        ".idea", ".sass-cache", "__pycache__",
        "*.swp", "*~",
        "*.ferrypart",
    ]);

    public bool Excludes(string name) =>
        Patterns.Any(pattern => pattern.Length > 0 && Matches(pattern, name));

    /// <summary>* は 0 文字以上、? は 1 文字に一致する</summary>
    internal static bool Matches(string pattern, string name)
    {
        int p = 0, n = 0, starP = -1, starN = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == name[n]) && pattern[p] != '*')
            {
                p++;
                n++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starN = n;
            }
            else if (starP >= 0)
            {
                // 直前の * に 1 文字多く一致させてやり直す
                p = starP + 1;
                n = ++starN;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    public bool Equals(ExcludeRules? other) => other is not null && Patterns.SequenceEqual(other.Patterns);
    public override bool Equals(object? obj) => Equals(obj as ExcludeRules);
    public override int GetHashCode() => Patterns.Aggregate(0, (hash, pattern) => HashCode.Combine(hash, pattern));
}
