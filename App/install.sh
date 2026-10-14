#!/bin/zsh
# Release ビルドを作って /Applications/Ferry.app に入れ替える
set -e
cd "$(dirname "$0")"
xcodegen generate -q
xcodebuild -project Ferry.xcodeproj -scheme Ferry -configuration Release -derivedDataPath build build -quiet
osascript -e 'quit app "Ferry"' 2>/dev/null || true
sleep 1
rm -rf /Applications/Ferry.app
cp -R build/Build/Products/Release/Ferry.app /Applications/
echo "✅ /Applications/Ferry.app を更新しました"
