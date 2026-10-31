using Ferry.Core;

namespace Ferry.Tests;

/// <summary>本物の資格情報マネージャーを使う（使い捨ての ID で書いて、最後に消す）</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class CredentialPasswordStoreTests
{
    [Fact]
    public void 保存して読み出して消せる()
    {
        var store = new CredentialPasswordStore();
        var id = Guid.NewGuid();
        try
        {
            Assert.Null(store.GetPassword(id));

            store.SetPassword(id, "パスワード p@ss");
            Assert.Equal("パスワード p@ss", store.GetPassword(id));

            store.SetPassword(id, "");
            Assert.Equal("", store.GetPassword(id));
        }
        finally
        {
            store.SetPassword(id, null);
        }
        Assert.Null(store.GetPassword(id));
        Assert.Equal("Ferry:" + id.ToString().ToUpperInvariant(), CredentialPasswordStore.TargetName(id));
    }
}
