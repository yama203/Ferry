#!/bin/zsh
# Release ビルドを作って /Applications/Ferry.app に入れ替える
set -e
cd "$(dirname "$0")"
xcodegen generate -q
xcodebuild -project Ferry.xcodeproj -scheme Ferry -configuration Release -derivedDataPath build build -quiet
# Homebrew の無い Mac でも動くよう libcurl などを同梱する
Tools/bundle-libs.sh build/Build/Products/Release/Ferry.app "Apple Development"
osascript -e 'quit app "Ferry"' 2>/dev/null || true
sleep 1
rm -rf /Applications/Ferry.app
cp -R build/Build/Products/Release/Ferry.app /Applications/
echo "✅ /Applications/Ferry.app を更新しました"

# 他の Mac 用に iCloud Drive/Ferry/App へ最新版を置く（ditto の zip は署名を保ったまま圧縮できる）
ICLOUD="$HOME/Library/Mobile Documents/com~apple~CloudDocs"
if [[ -d "$ICLOUD" ]]; then
    DEST="$ICLOUD/Ferry/App"
    mkdir -p "$DEST"
    ditto -c -k --sequesterRsrc --keepParent /Applications/Ferry.app "$DEST/Ferry.zip.tmp"
    mv -f "$DEST/Ferry.zip.tmp" "$DEST/Ferry.zip"
    VERSION=$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" /Applications/Ferry.app/Contents/Info.plist)
    cat > "$DEST/はじめにお読みください.txt" <<TEXT
Ferry $VERSION（$(date '+%Y-%m-%d %H:%M') にビルド）

他の Mac で使うには:
1. Ferry.zip をダブルクリックして展開する
2. 出てきた Ferry.app を「アプリケーション」フォルダへ移す（古いものは置き換える）
3. 初めて開くときは、Ferry.app を右クリックして「開く」を選ぶ
TEXT
    echo "☁️  iCloud Drive/Ferry/App に最新版を置きました"
fi
