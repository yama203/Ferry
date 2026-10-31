# Ferry

自分用の Mac 向けファイル転送アプリ（FTP / FTPS / SFTP）。Transmit のような 2 ペインの画面で使う。

## インストール

1. [Releases](../../releases) から最新の `Ferry-x.x.zip` をダウンロードして展開する
2. `Ferry.app` を「アプリケーション」フォルダへ移す（古いものは置き換える）
3. 初めて開くときは、`Ferry.app` を右クリックして「開く」を選ぶ
   （「開発元を確認できません」と出る場合は「システム設定」→「プライバシーとセキュリティ」の「このまま開く」）

Apple シリコン（M1 以降）の Mac、macOS 15 以降で動く。Homebrew は不要（通信ライブラリはアプリに同梱）。

## 主な機能

- FTP / FTPS（Explicit・Implicit、パッシブモード）/ SFTP（パスワード・秘密鍵）
- 左に「この Mac」、右にサーバーの 2 ペイン。ドラッグ&ドロップ、Finder へのドラッグで転送
- 上書きの確認、フォルダの同期（差分を確認してから実行）、node_modules や .git などの除外
- エディタで開いて保存すると自動でアップロード
- 接続先のフォルダ分け・検索、左右の場所の組み合わせのブックマーク、Transmit からの読み込み
- 接続先とパスワード（マスターパスワードで暗号化）を iCloud Drive で Mac 間に同期
- 会社で共有する接続先（Dropbox などの共有フォルダ。Windows 版と同じ形式）。パスワードも会社のマスターパスワードで暗号化して共有できる
- 自分の接続先とパスワードは、iCloud Drive で Windows 版とも同期する

## Windows 版

`Windows/` で作成中（C# / .NET 10、WinUI 3）。接続先の管理（会社の共有を含む）、接続、2 ペインの一覧表示とファイル操作、転送（ドラッグ&ドロップ・上書きの確認・転送一覧・通知）、プレビュー、エディタで開いて自動アップロード、同期、ブックマーク、FileZilla / WinSCP からの読み込み、Velopack での配布と自動更新（`Windows\release.ps1`）までできている。インストーラーと更新は会社の共有フォルダ（`<共有フォルダ>\App\Windows`）に置く。仕様と進み具合は [docs/WINDOWS_PORT.md](docs/WINDOWS_PORT.md)。

## 開発

必要なもの: Xcode、[XcodeGen](https://github.com/yonaskolb/XcodeGen)、Homebrew の curl（`brew install xcodegen curl`）

```bash
cd App
xcodegen generate        # Ferry.xcodeproj を作る
./install.sh             # Release ビルド → /Applications と iCloud Drive を更新
./release.sh             # バージョンを上げて GitHub のリリースとして公開
```

- `FerryKit/` … 通信部分（libcurl のラッパー）と動作確認用の `ferry-cli`。`swift test` で一覧解析のテスト
- `App/` … アプリ本体（SwiftUI + AppKit）
- `App/Tools/make-icon.swift` … アイコンを描く
- `App/Tools/bundle-libs.sh` … libcurl と依存ライブラリ、ルート証明書をアプリに同梱する
