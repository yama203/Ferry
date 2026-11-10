using Ferry.Core;

namespace Ferry.Tests;

public class ServerSearchTests
{
    private static readonly ServerConfig Server = new()
    {
        Name = "岡部工事部 本番", Host = "ftp.okabe-example.jp", Username = "okabe_admin",
    };

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("　 \t")]
    public void 空や空白だけなら検索しない(string? query)
    {
        Assert.False(ServerSearch.IsActive(query));
        Assert.Empty(ServerSearch.Terms(query));
        Assert.True(ServerSearch.Matches(Server, null, query));
    }

    [Theory]
    [InlineData("本番")]
    [InlineData("okabe-example")]
    [InlineData("okabe_admin")]
    [InlineData("お客さん")]
    public void 名前かホストかユーザー名かフォルダ名に含めば合う(string query)
    {
        Assert.True(ServerSearch.IsActive(query));
        Assert.True(ServerSearch.Matches(Server, "お客さん", query));
    }

    [Fact]
    public void どれにも含まなければ合わない()
    {
        Assert.False(ServerSearch.Matches(Server, "お客さん", "staging"));
        // フォルダが無ければフォルダ名では合わない
        Assert.False(ServerSearch.Matches(Server, null, "お客さん"));
    }

    [Theory]
    [InlineData("FTP.OKABE")]
    [InlineData("ｆｔｐ．ｏｋａｂｅ")]
    [InlineData("ＯＫＡＢＥ")]
    public void 大文字小文字と全角半角は区別しない(string query) =>
        Assert.True(ServerSearch.Matches(Server, null, query));

    [Fact]
    public void 半角カナでも合う()
    {
        var server = Server with { Name = "サンプル ガーデン" };
        Assert.True(ServerSearch.Matches(server, null, "ｶﾞｰﾃﾞﾝ"));
        Assert.True(ServerSearch.Matches(server with { Name = "ｶﾞｰﾃﾞﾝ" }, null, "ガーデン"));
    }

    [Theory]
    [InlineData("岡部 admin", true)]
    [InlineData("岡部　admin", true)]
    [InlineData("本番 お客さん", true)]
    [InlineData("岡部 staging", false)]
    public void 空白で区切った語はすべて含むものだけ合う(string query, bool expected) =>
        Assert.Equal(expected, ServerSearch.Matches(Server, "お客さん", query));

    [Fact]
    public void 項目をまたいでは合わない()
    {
        // 名前の末尾「本番」とホストの先頭「ftp」をつなげた語
        Assert.False(ServerSearch.Matches(Server, null, "本番ftp"));
    }
}
