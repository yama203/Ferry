using Ferry.Core;

namespace Ferry.Tests;

public class NameFilterTests
{
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("　 \t")]
    public void 空や空白だけなら絞り込まない(string? query)
    {
        Assert.False(NameFilter.IsActive(query));
        Assert.Empty(NameFilter.Terms(query));
        Assert.True(NameFilter.Matches("wp-config.php", query));
        Assert.True(NameFilter.Matches(".htaccess", query));
    }

    [Theory]
    [InlineData("logo-2024.png", "logo", true)]
    [InlineData("logo-2024.png", "2024", true)]
    [InlineData("logo-2024.png", "banner", false)]
    [InlineData("uploads", "load", true)]
    public void 名前に含むものが合う(string name, string query, bool expected) =>
        Assert.Equal(expected, NameFilter.Matches(name, query));

    [Theory]
    [InlineData("IMG_0012.JPG", "img")]
    [InlineData("img_0012.jpg", "IMG")]
    [InlineData("Logo.PNG", "logo.png")]
    public void 大文字小文字を区別しない(string name, string query) =>
        Assert.True(NameFilter.Matches(name, query));

    [Theory]
    [InlineData("ＬＯＧＯ.png", "logo")]
    [InlineData("logo2024.png", "２０２４")]
    [InlineData("ｶﾀﾛｸﾞ.pdf", "カタログ")]
    [InlineData("カタログ.pdf", "ｶﾀﾛｸﾞ")]
    public void 全角半角を区別しない(string name, string query) =>
        Assert.True(NameFilter.Matches(name, query));

    [Fact]
    public void 空白区切りの語をすべて含むものだけ合う()
    {
        Assert.True(NameFilter.Matches("hero-image-2024-large.jpg", "hero 2024"));
        Assert.True(NameFilter.Matches("hero-image-2024-large.jpg", "2024 hero"));
        Assert.False(NameFilter.Matches("hero-image-2023-large.jpg", "hero 2024"));
        // 全角の空白・続いた空白でも区切る
        Assert.True(NameFilter.Matches("hero-image-2024-large.jpg", "hero　2024"));
        Assert.True(NameFilter.Matches("hero-image-2024-large.jpg", "  hero 　 large  "));
        Assert.Equal(["hero", "2024", "large"], NameFilter.Terms(" hero　2024  large "));
    }

    [Fact]
    public void 日本語のファイル名()
    {
        Assert.True(NameFilter.Matches("会社案内_2024年版.pdf", "会社案内"));
        Assert.True(NameFilter.Matches("会社案内_2024年版.pdf", "案内 年版"));
        Assert.False(NameFilter.Matches("会社案内_2024年版.pdf", "採用"));
        Assert.True(NameFilter.Matches("お知らせ画像.jpg", "画像"));
        // Mac から上げたファイルは濁点が分かれている（NFD）ことがある
        Assert.True(NameFilter.Matches("ガイド.pdf".Normalize(System.Text.NormalizationForm.FormD), "ガイド"));
        Assert.True(NameFilter.Matches("ガイド.pdf", "ガイド".Normalize(System.Text.NormalizationForm.FormD)));
        // 濁点の有無は区別する
        Assert.False(NameFilter.Matches("カイト.pdf", "ガイド"));
    }

    [Fact]
    public void 正しくない文字を含む名前でも落ちない()
    {
        Assert.True(NameFilter.Matches("bad\ud800name.txt", "name"));
        Assert.False(NameFilter.Matches("bad\ud800name.txt", "other"));
    }
}
