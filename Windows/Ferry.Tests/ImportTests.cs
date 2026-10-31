using Ferry.Core;
using Ferry.Core.Import;

namespace Ferry.Tests;

public class FileZillaImporterTests
{
    private static ImportResult Read() => FileZillaImporter.Read(TempDirectory.Fixture("filezilla-sitemanager.xml"));

    [Fact]
    public void フォルダと入れ子のフォルダを読む()
    {
        var result = Read();

        Assert.Equal([null, "お客様", "お客様 / 古いもの"], result.Folders.Select(f => f.Name));
        Assert.Equal(4, result.ServerCount);
        Assert.Equal(1, result.UnsupportedCount);
        Assert.Equal(1, result.EncryptedPasswordCount);
    }

    [Fact]
    public void 接続先の中身()
    {
        var servers = Read().AllServers.ToDictionary(s => s.Server.Name);

        var ftp = servers["フォルダなしのサイト"];
        Assert.Equal(ServerProtocol.Ftp, ftp.Server.ServerProtocol);
        Assert.Null(ftp.Server.Port);
        Assert.Equal("webmaster", ftp.Server.Username);
        Assert.Equal("pass ワード", ftp.Password);
        Assert.Equal("/www/html docs", ftp.Server.InitialPath);
        Assert.Equal(@"C:\Sites\example", ftp.Server.LocalPath);
        Assert.True(ftp.Server.PassiveMode);

        var sftp = servers["本番 SFTP"].Server;
        Assert.Equal(ServerProtocol.Sftp, sftp.ServerProtocol);
        Assert.Equal(2222, sftp.Port);
        Assert.Equal(@"C:\Users\yama\.ssh\id_ed25519", sftp.PrivateKeyPath);

        var implicitFtps = servers["Implicit"];
        Assert.Equal(ServerProtocol.FtpsImplicit, implicitFtps.Server.ServerProtocol);
        Assert.Null(implicitFtps.Server.Port);
        Assert.False(implicitFtps.Server.PassiveMode);
        Assert.Null(implicitFtps.Password);

        var anonymous = servers["Explicit 匿名"].Server;
        Assert.Equal(ServerProtocol.Ftps, anonymous.ServerProtocol);
        Assert.Equal("anonymous", anonymous.Username);
    }

    [Theory]
    [InlineData("1 0 3 www 4 html", "/www/html")]
    [InlineData("1 0", "/")]
    [InlineData("1 0 9 my folder", "/my folder")]
    [InlineData("", null)]
    public void RemoteDirの形式(string value, string? expected)
    {
        Assert.Equal(expected, FileZillaImporter.DecodeRemoteDir(value));
    }

    [Fact]
    public void 別のファイルはエラー()
    {
        Assert.Throws<InvalidDataException>(() => FileZillaImporter.Parse("<html></html>"));
        Assert.Throws<InvalidDataException>(() => FileZillaImporter.Parse("not xml"));
    }
}

public class WinScpImporterTests
{
    [Fact]
    public void iniのセッションを読む()
    {
        var result = WinScpImporter.ReadIni(TempDirectory.Fixture("winscp.ini"));

        Assert.Equal(2, result.ServerCount);
        Assert.Equal(1, result.UnsupportedCount);
        Assert.Equal(["お客様", null], result.Folders.Select(f => f.Name));

        var sftp = result.Folders[0].Servers.Single();
        Assert.Equal("本番", sftp.Server.Name);
        Assert.Equal(ServerProtocol.Sftp, sftp.Server.ServerProtocol);
        Assert.Equal(2222, sftp.Server.Port);
        Assert.Equal("/var/www/html", sftp.Server.InitialPath);
        Assert.Equal(@"C:\Sites\web", sftp.Server.LocalPath);
        Assert.Equal(@"C:\Users\yama\key.ppk", sftp.Server.PrivateKeyPath);
        // パスワードは独自の方式で隠されているので読まない
        Assert.Null(sftp.Password);

        var ftp = result.Folders[1].Servers.Single().Server;
        Assert.Equal("ftp site", ftp.Name);
        Assert.Equal(ServerProtocol.Ftps, ftp.ServerProtocol);
        Assert.False(ftp.PassiveMode);
        Assert.Null(ftp.Port);
    }
}
