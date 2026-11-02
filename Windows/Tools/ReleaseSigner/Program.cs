using Ferry.Core;

// 自動更新の一覧への署名（release.ps1 から使う）。
//   ReleaseSigner key  <秘密鍵のファイル> <公開鍵のファイル>  … 秘密鍵が無ければ作る。公開鍵のファイルを秘密鍵に合わせる
//   ReleaseSigner sign <秘密鍵のファイル> <一覧のファイル>…    … 一覧の隣に <一覧>.sig を書く
if (args.Length < 3)
{
    Console.Error.WriteLine("使い方: ReleaseSigner key <秘密鍵> <公開鍵> ／ ReleaseSigner sign <秘密鍵> <releases.win.json>…");
    return 2;
}
var keyPath = args[1];
switch (args[0])
{
    case "key":
        var publicKeyPath = args[2];
        if (!File.Exists(keyPath))
        {
            // 公開鍵がもう組み込まれているのに秘密鍵が無いなら、作り直すと配った Ferry が更新を受け取れなくなる
            if (File.Exists(publicKeyPath) && File.ReadAllText(publicKeyPath).Trim().Length > 0)
            {
                Console.Error.WriteLine($"署名の秘密鍵が見つかりません（{keyPath}）。バックアップから戻してください。");
                Console.Error.WriteLine("作り直すと、配った Ferry は新しい鍵の更新を受け取れず、各自で入れ直すことになります。");
                Console.Error.WriteLine($"それでも作り直すときは {publicKeyPath} を消してからもう一度実行してください。");
                return 1;
            }
            var (privateKey, publicKey) = ReleaseSignature.GenerateKey();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyPath))!);
            File.WriteAllText(keyPath, privateKey);
            File.WriteAllText(publicKeyPath, publicKey + "\n");
            Console.WriteLine($"🔑 署名の鍵を作りました: {keyPath}");
            Console.WriteLine("   この秘密鍵は、パスワード管理アプリなど自分だけの場所にバックアップしてください（共有フォルダには置かない）。");
            return 0;
        }
        var expected = ReleaseSignature.PublicKeyOf(File.ReadAllText(keyPath));
        if (!File.Exists(publicKeyPath) || File.ReadAllText(publicKeyPath).Trim() != expected)
        {
            Console.Error.WriteLine($"秘密鍵（{keyPath}）が、組み込む公開鍵（{publicKeyPath}）と合いません。");
            return 1;
        }
        return 0;

    case "sign":
        var pem = File.ReadAllText(keyPath);
        foreach (var feed in args.Skip(2))
        {
            File.WriteAllText(feed + ReleaseSignature.Extension, ReleaseSignature.Sign(File.ReadAllBytes(feed), pem) + "\n");
            Console.WriteLine($"✍️  署名しました: {Path.GetFileName(feed)}");
        }
        return 0;

    default:
        Console.Error.WriteLine($"知らないコマンドです: {args[0]}");
        return 2;
}
