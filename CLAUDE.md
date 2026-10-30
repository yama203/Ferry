# Ferry

自分と会社の人が使う、FTP / FTPS / SFTP のファイル転送アプリ。

- `App/` … Mac 版（Swift / SwiftUI + AppKit）。`App/install.sh` で /Applications を更新、`App/release.sh` で GitHub のリリース
- `FerryKit/` … Mac 版の通信部分（libcurl のラッパー）
- `Windows/` … Windows 版（C# / WinUI 3）。仕様は `docs/WINDOWS_PORT.md`

## 決まりごと

- **ユーザーへの返答は必ず日本語**。ツールを使う前の一言や途中経過も含めて、英語の文を書かない。
- コードのコメント、コミットメッセージ、画面の文言も日本語。
- Mac 版と Windows 版は、接続先の保存ファイル（共有フォルダの `servers/<ID>.json` など）を**同じ形式**で読み書きする。形式を変えるときは両方を直し、`docs/WINDOWS_PORT.md` も更新する。
- 動作確認には公開テストサーバー `test.rebex.net`（demo / password、読み取り専用）が使える。
