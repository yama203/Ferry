#!/bin/zsh
# 新しいバージョンを GitHub のリリースとして公開する。
#   App/release.sh 0.3        … バージョンを指定
#   App/release.sh            … 末尾の数字を 1 つ上げる（0.2 → 0.3）
# ビルド・/Applications と iCloud Drive の更新（install.sh）も行う。
set -euo pipefail
cd "$(dirname "$0")"

if [[ -n "$(git status --porcelain)" ]]; then
    echo "❌ コミットしていない変更があります。先にコミットしてください" >&2
    exit 1
fi

current=$(sed -n 's/^ *MARKETING_VERSION: "\(.*\)"/\1/p' project.yml)
if [[ $# -ge 1 ]]; then
    version="$1"
else
    version="${current%.*}.$(( ${current##*.} + 1 ))"
fi
build=$(( $(sed -n 's/^ *CURRENT_PROJECT_VERSION: "\(.*\)"/\1/p' project.yml) + 1 ))
tag="v$version"
if git rev-parse "$tag" >/dev/null 2>&1; then
    echo "❌ $tag はすでにあります" >&2
    exit 1
fi
echo "🚀 Ferry $current → $version（ビルド $build）"

sed -i '' "s/MARKETING_VERSION: \".*\"/MARKETING_VERSION: \"$version\"/; s/CURRENT_PROJECT_VERSION: \".*\"/CURRENT_PROJECT_VERSION: \"$build\"/" project.yml
./install.sh

zip="build/Ferry-$version.zip"
ditto -c -k --sequesterRsrc --keepParent /Applications/Ferry.app "$zip"

# リリースノート: 前のタグからのコミットの 1 行目（バージョン更新のコミットは除く）
previous=$(git describe --tags --abbrev=0 2>/dev/null || true)
notes=$(git log --pretty='- %s' ${previous:+$previous..HEAD} | grep -v '^- バージョン ' || true)

git add project.yml
git commit -qm "バージョン $version"
git tag "$tag"
git push -q origin HEAD --tags

gh release create "$tag" "$zip" --title "Ferry $version" --notes "## 変更点
${notes:-- 初回リリース}

## インストール
Ferry-$version.zip をダウンロードして展開し、Ferry.app を「アプリケーション」フォルダへ移してください。
初めて開くときは、Ferry.app を右クリックして「開く」を選びます。"

echo "✅ $(gh release view "$tag" --json url -q .url)"

# 会社の人の Mac 用に、共有フォルダの App/Mac へ置く（Ferry はここを見て自分で更新する）
# version.json は zip を置いてから書く（Ferry は version.json を見て zip を取りに来るので）
shared=$(defaults read com.yama.Ferry sharedDirectory 2>/dev/null || true)
if [[ -n "$shared" && -d "$shared" ]]; then
    dest="$shared/App/Mac"
    mkdir -p "$dest"
    cp "$zip" "$dest/Ferry.zip.tmp"
    mv -f "$dest/Ferry.zip.tmp" "$dest/Ferry.zip"
    printf '{"build":%d,"version":"%s"}\n' "$build" "$version" > "$dest/version.json.tmp"
    mv -f "$dest/version.json.tmp" "$dest/version.json"
    cp "../docs/manual/Ferry 取り扱い説明書.pdf" "$dest/" 2>/dev/null || true
    cat > "$dest/はじめにお読みください.txt" <<TEXT
Ferry for Mac $version（$(date '+%Y-%m-%d %H:%M') に作成）

インストール:
1. Ferry.zip をダブルクリックして展開する
2. 出てきた Ferry.app を「アプリケーション」フォルダへ移す
3. 初めて開くときは、Ferry.app を右クリックして「開く」を選ぶ
4. 以後は、Ferry がこのフォルダから自動で更新されます（設定で会社の共有フォルダを選んでおく）

使い方や同期の設定は「Ferry 取り扱い説明書.pdf」を見てください（画面は Windows 版ですが、できることは同じです）。
TEXT
    echo "☁️  $dest に置きました"
fi
