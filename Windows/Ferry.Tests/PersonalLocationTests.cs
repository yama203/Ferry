using Ferry.Core;

namespace Ferry.Tests;

public class PersonalLocationTests
{
    [Fact]
    public void 設定が無ければiCloudがあればそこ無ければこのPC()
    {
        using var temp = new TempDirectory();
        var iCloud = temp.Combine("iCloudDrive", "Ferry");
        var local = temp.Combine("AppData", "Ferry");

        Assert.Equal(new PersonalLocation(PersonalLocation.LocationKind.ICloud, iCloud), PersonalLocation.Resolve(null, iCloud, local));
        Assert.Equal(new PersonalLocation(PersonalLocation.LocationKind.Local, local), PersonalLocation.Resolve(null, null, local));
    }

    [Fact]
    public void 選んだフォルダとこのPCだけ()
    {
        using var temp = new TempDirectory();
        var dropbox = temp.Combine("Dropbox", "Ferry");
        Directory.CreateDirectory(dropbox);
        var local = temp.Combine("AppData", "Ferry");

        var folder = PersonalLocation.Resolve(dropbox, null, local);
        Assert.Equal(PersonalLocation.LocationKind.Folder, folder.Kind);
        Assert.True(folder.IsSynced);
        Assert.Equal(dropbox, folder.Setting);

        var onlyHere = PersonalLocation.Resolve(PersonalLocation.LocalSetting, dropbox, local);
        Assert.Equal(PersonalLocation.LocationKind.Local, onlyHere.Kind);
        Assert.False(onlyHere.IsSynced);

        // 選んだフォルダが無くなっていたら自動に戻す
        Assert.Equal(PersonalLocation.LocationKind.Local, PersonalLocation.Resolve(temp.Combine("消えた"), null, local).Kind);
    }

    [Fact]
    public void 新しい場所に無ければコピーしあればそちらを使う()
    {
        using var temp = new TempDirectory();
        var from = new PersonalLocation(PersonalLocation.LocationKind.ICloud, temp.Combine("iCloud"));
        Directory.CreateDirectory(from.Directory);
        File.WriteAllText(from.ServersPath, "servers");
        File.WriteAllText(from.VaultPath, "vault");
        var to = new PersonalLocation(PersonalLocation.LocationKind.Folder, temp.Combine("Dropbox"));

        Assert.False(PersonalLocation.PrepareMove(from, to));
        Assert.Equal("servers", File.ReadAllText(to.ServersPath));
        Assert.Equal("vault", File.ReadAllText(to.VaultPath));

        // 2 台目の PC: すでにあるものは上書きしない
        File.WriteAllText(from.ServersPath, "other pc");
        Assert.True(PersonalLocation.PrepareMove(from, to));
        Assert.Equal("servers", File.ReadAllText(to.ServersPath));
    }
}
