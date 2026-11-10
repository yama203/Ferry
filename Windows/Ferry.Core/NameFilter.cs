using System.Globalization;
using System.Text;

namespace Ferry.Core;

/// <summary>
/// ファイル一覧の名前での絞り込み。空白（全角の空白も）で区切った語を、すべて名前に含むものだけ残す。
/// 大文字・小文字と全角・半角は区別しない（Mac 版の localizedStandardContains に合わせる）。
/// </summary>
public static class NameFilter
{
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth;

    /// <summary>入力を語に分ける。空や空白だけなら空</summary>
    public static string[] Terms(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Fold).ToArray();

    /// <summary>絞り込んでいるか（空や空白だけなら絞り込まない）</summary>
    public static bool IsActive(string? query) => !string.IsNullOrWhiteSpace(query);

    public static bool Matches(string name, string? query) => Matches(name, Terms(query));

    /// <summary>語が無ければすべて合う</summary>
    public static bool Matches(string name, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return true;
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        var folded = Fold(name);
        foreach (var term in terms)
        {
            if (compare.IndexOf(folded, Fold(term), Options) < 0) return false;
        }
        return true;
    }

    /// <summary>
    /// 互換の文字をそろえる（NFKC）。半角カナの濁点（ｶﾞ）は IgnoreWidth だけでは「ガ」に合わないため。
    /// 正しくない文字（対になっていないサロゲート）を含む名前はそのまま
    /// </summary>
    private static string Fold(string text)
    {
        try
        {
            return text.IsNormalized(NormalizationForm.FormKC) ? text : text.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            return text;
        }
    }
}
