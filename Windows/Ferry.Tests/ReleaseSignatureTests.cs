using Ferry.Core;

namespace Ferry.Tests;

public class ReleaseSignatureTests
{
    private const string Feed = """{"Assets":[{"PackageId":"FerryApp","Version":"0.1.9","Type":"Full","FileName":"FerryApp-0.1.9-full.nupkg","SHA256":"E0DE"}]}""";

    [Fact]
    public void 署名した一覧は読めて書き換えると読めない()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Path);
        var (privateKey, publicKey) = ReleaseSignature.GenerateKey();
        Assert.Equal(publicKey, ReleaseSignature.PublicKeyOf(privateKey));

        var feedPath = temp.Combine("releases.win.json");
        File.WriteAllText(feedPath, Feed);
        File.WriteAllText(feedPath + ReleaseSignature.Extension, ReleaseSignature.Sign(File.ReadAllBytes(feedPath), privateKey));
        Assert.Equal(Feed, ReleaseSignature.ReadVerifiedFeed(temp.Path, "win", publicKey));

        // パッケージのハッシュを差し替えた一覧
        File.WriteAllText(feedPath, Feed.Replace("E0DE", "BAD0"));
        Assert.Throws<ReleaseSignatureException>(() => ReleaseSignature.ReadVerifiedFeed(temp.Path, "win", publicKey));
    }

    [Fact]
    public void 別の鍵の署名や署名の無い一覧は読めない()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Path);
        var (_, publicKey) = ReleaseSignature.GenerateKey();
        var (otherKey, _) = ReleaseSignature.GenerateKey();
        var feedPath = temp.Combine("releases.win.json");
        File.WriteAllText(feedPath, Feed);

        Assert.Throws<ReleaseSignatureException>(() => ReleaseSignature.ReadVerifiedFeed(temp.Path, "win", publicKey));

        File.WriteAllText(feedPath + ReleaseSignature.Extension, ReleaseSignature.Sign(File.ReadAllBytes(feedPath), otherKey));
        Assert.Throws<ReleaseSignatureException>(() => ReleaseSignature.ReadVerifiedFeed(temp.Path, "win", publicKey));

        File.WriteAllText(feedPath + ReleaseSignature.Extension, "壊れた署名");
        Assert.Throws<ReleaseSignatureException>(() => ReleaseSignature.ReadVerifiedFeed(temp.Path, "win", publicKey));
    }
}
