#!/bin/zsh
# ビルドした Ferry.app に Homebrew の libcurl と依存ライブラリ、ルート証明書を同梱し、
# Homebrew の無い Mac でも動くようにする。
#   App/Tools/bundle-libs.sh <Ferry.app> <署名に使う証明書の名前>
set -euo pipefail

APP="$1"
IDENTITY="$2"
BINARY="$APP/Contents/MacOS/Ferry"
FRAMEWORKS="$APP/Contents/Frameworks"
mkdir -p "$FRAMEWORKS"

# Homebrew 内のライブラリへの参照を、実際のファイルのパスで列挙する（自分自身の id は除く）。
# @rpath/@loader_path の参照は、元のファイルと同じフォルダにあるものとして解決する。
homebrew_deps() {
    local file="$1" own="$(otool -D "$1" | tail -1)"
    for dep in $(otool -L "$file" | tail -n +2 | awk '{print $1}'); do
        [[ "$dep" == "$own" ]] && continue
        case "$dep" in
            /opt/homebrew/*) echo "$dep" ;;
            @rpath/*|@loader_path/*)
                local candidate="${file:A:h}/${dep:t}"
                [[ "$file" == /opt/homebrew/* && -f "$candidate" ]] && echo "$candidate" ;;
        esac
    done
}

# 依存をたどって Frameworks にコピーする（たどるのは Homebrew 内の元のファイル）
typeset -A copied
queue=("$BINARY")
while (( ${#queue} > 0 )); do
    current="${queue[1]}"
    queue=("${(@)queue[2,-1]}")
    for dep in $(homebrew_deps "$current"); do
        name="${dep:t}"
        [[ -n "${copied[$name]:-}" ]] && continue
        copied[$name]=1
        cp -fL "$dep" "$FRAMEWORKS/$name"
        chmod u+w "$FRAMEWORKS/$name"
        queue+=("${dep:A}")
    done
done

# 参照を @rpath に書き換える
rewrite() {
    for dep in $(otool -L "$1" | tail -n +2 | awk '{print $1}' | grep '^/opt/homebrew/' || true); do
        install_name_tool -change "$dep" "@rpath/${dep:t}" "$1" 2>/dev/null
    done
}
for lib in "$FRAMEWORKS"/*.dylib; do
    install_name_tool -id "@rpath/${lib:t}" "$lib" 2>/dev/null
    install_name_tool -add_rpath "@loader_path" "$lib" 2>/dev/null || true
    rewrite "$lib"
done
rewrite "$BINARY"
otool -l "$BINARY" | grep -q "@executable_path/../Frameworks" \
    || install_name_tool -add_rpath "@executable_path/../Frameworks" "$BINARY"

# FTPS の証明書検証に使うルート証明書（Homebrew の OpenSSL は /opt/homebrew/etc のものを見に行くため）
cp -f /opt/homebrew/etc/ca-certificates/cert.pem "$APP/Contents/Resources/cacert.pem"

# 書き換えると署名が壊れるので署名し直す（ライブラリ → アプリの順）
for lib in "$FRAMEWORKS"/*.dylib; do
    codesign --force --sign "$IDENTITY" --timestamp=none "$lib" >/dev/null 2>&1
done
codesign --force --sign "$IDENTITY" --timestamp=none "$APP" >/dev/null 2>&1

# Homebrew への参照が残っていないか、@rpath の参照先がすべて同梱されているか確認
leftover=$( { otool -L "$BINARY"; for lib in "$FRAMEWORKS"/*.dylib; do otool -L "$lib"; done } | grep '/opt/homebrew/' || true)
missing=$( { otool -L "$BINARY"; for lib in "$FRAMEWORKS"/*.dylib; do otool -L "$lib"; done } \
    | awk '{print $1}' | grep '^@rpath/' | sort -u | while read -r ref; do
        [[ -f "$FRAMEWORKS/${ref:t}" ]] || echo "$ref"
    done)
if [[ -n "$leftover$missing" ]]; then
    echo "❌ 同梱できていない参照があります:" >&2
    echo "$leftover$missing" >&2
    exit 1
fi
echo "📦 ${#copied} 個のライブラリと証明書を同梱しました"
