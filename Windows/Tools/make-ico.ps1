# Mac 版のアイコン（App/Ferry/Assets.xcassets/AppIcon.appiconset の PNG）を、Windows の .ico にまとめる。
# .ico の中身は PNG のまま入れる（Windows Vista 以降はこの形を読める）。
#   powershell -File Windows\Tools\make-ico.ps1
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$icons = Join-Path $root 'App\Ferry\Assets.xcassets\AppIcon.appiconset'
$output = Join-Path $root 'Windows\Ferry\Assets\Ferry.ico'

# 大きさ → 元の PNG
$sources = [ordered]@{
    16  = 'icon_16x16.png'
    32  = 'icon_32x32.png'
    64  = 'icon_32x32@2x.png'
    128 = 'icon_128x128.png'
    256 = 'icon_256x256.png'
}

# （数字のキーで引くと「n 番目」と解釈されるので、組を順に回す）
$images = foreach ($entry in $sources.GetEnumerator()) {
    [pscustomobject]@{ Size = $entry.Key; Bytes = [IO.File]::ReadAllBytes((Join-Path $icons $entry.Value)) }
}

New-Item -ItemType Directory -Force (Split-Path $output) | Out-Null
$stream = [IO.File]::Create($output)
$writer = New-Object IO.BinaryWriter($stream)
try {
    # ICONDIR
    $writer.Write([UInt16]0)               # 予約
    $writer.Write([UInt16]1)               # 1 = アイコン
    $writer.Write([UInt16]$images.Count)
    # ICONDIRENTRY（16 バイト × 枚数）の後ろに画像を並べる
    $offset = 6 + 16 * $images.Count
    foreach ($image in $images) {
        $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }   # 256 は 0 と書く
        $writer.Write([Byte]$dimension)    # 幅
        $writer.Write([Byte]$dimension)    # 高さ
        $writer.Write([Byte]0)             # パレットの色数
        $writer.Write([Byte]0)             # 予約
        $writer.Write([UInt16]1)           # カラープレーン
        $writer.Write([UInt16]32)          # ビット数
        $writer.Write([UInt32]$image.Bytes.Length)
        $writer.Write([UInt32]$offset)
        $offset += $image.Bytes.Length
    }
    foreach ($image in $images) { $writer.Write($image.Bytes) }
}
finally {
    $writer.Dispose()
}
Write-Host "✅ $output を作りました（$($images.Count) 枚）"
