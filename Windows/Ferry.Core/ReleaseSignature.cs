using System.Security.Cryptography;

namespace Ferry.Core;

/// <summary>
/// 自動更新の一覧（releases.&lt;チャンネル&gt;.json）の署名。共有フォルダに書き込める人なら誰でも
/// 更新を置けてしまうので、release.ps1 が開発者の PC だけにある秘密鍵で一覧に署名し（.sig）、
/// Ferry は組み込んだ公開鍵で確かめてから使う。一覧には各パッケージの SHA-256 が書いてあり、
/// Velopack がダウンロードしたパッケージをそれと照合するので、一覧に署名すればパッケージも守られる。
/// 方式は ECDSA P-256 + SHA-256。公開鍵は SubjectPublicKeyInfo、署名は IEEE P1363 形式を base64 で書く。
/// </summary>
public static class ReleaseSignature
{
    /// <summary>署名のファイル名（一覧のファイル名に付け足す）</summary>
    public const string Extension = ".sig";

    /// <summary>鍵を作る。秘密鍵は PEM（PKCS#8）、公開鍵は base64</summary>
    public static (string PrivateKeyPem, string PublicKey) GenerateKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>秘密鍵に対応する公開鍵（base64）</summary>
    public static string PublicKeyOf(string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    public static string Sign(byte[] data, string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        return Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256));
    }

    public static bool Verify(byte[] data, string signature, string publicKey)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey.Trim()), out _);
            return key.VerifyData(data, Convert.FromBase64String(signature.Trim()), HashAlgorithmName.SHA256);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// 署名を確かめた一覧を返す。署名が無い・合わないときは例外。
    /// 確かめた後でファイルが書き換わっても困らないよう、1 回だけ読んだ中身を確かめて、それを返す。
    /// </summary>
    public static string ReadVerifiedFeed(string directory, string channel, string publicKey)
    {
        var feedPath = Path.Combine(directory, $"releases.{channel}.json");
        var data = File.ReadAllBytes(feedPath);
        var signaturePath = feedPath + Extension;
        if (!File.Exists(signaturePath))
        {
            throw new ReleaseSignatureException($"更新の一覧に署名がありません（{Path.GetFileName(signaturePath)}）。");
        }
        if (!Verify(data, File.ReadAllText(signaturePath), publicKey))
        {
            throw new ReleaseSignatureException("更新の一覧の署名が合いません。開発者以外が置いた更新の可能性があるので、入れません。");
        }
        return new System.Text.UTF8Encoding(false).GetString(data).TrimStart('﻿');
    }
}

public sealed class ReleaseSignatureException(string message) : Exception(message);
