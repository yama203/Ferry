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
