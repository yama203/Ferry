namespace Ferry.Core;

/// <summary>
/// サイドバーの接続先の検索。名前・ホスト・ユーザー名・フォルダ名を対象に、ファイル一覧の絞り込み（NameFilter）と
/// 同じ比べ方をする（空白（全角の空白も）で区切った語をすべて含む。大文字・小文字と全角・半角は区別しない）。
/// </summary>
public static class ServerSearch
{
    /// <summary>検索しているか（空や空白だけなら検索しない）</summary>
    public static bool IsActive(string? query) => NameFilter.IsActive(query);

    /// <summary>入力を語に分ける（何件も比べるときは 1 回だけ分けて Matches に渡す）</summary>
    public static string[] Terms(string? query) => NameFilter.Terms(query);

    public static bool Matches(ServerConfig server, string? folderName, string? query) =>
        Matches(server, folderName, Terms(query));

    /// <summary>語が無ければすべて合う</summary>
    public static bool Matches(ServerConfig server, string? folderName, IReadOnlyList<string> terms) =>
        NameFilter.Matches(SearchText(server, folderName), terms);

    /// <summary>
    /// 検索の対象の文字列。項目どうしは改行でつなぐ（語は空白を含まないので、項目をまたいで合うことはない）
    /// </summary>
    private static string SearchText(ServerConfig server, string? folderName) =>
        string.Join("\n", server.Name, server.Host, server.Username, folderName ?? "");
}
