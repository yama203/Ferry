# Windows 版の新しいバージョンを作って配る。
#   .\release.ps1              … 末尾の数字を 1 つ上げる（0.1.0 → 0.1.1）
#   .\release.ps1 0.2.0        … バージョンを指定
#   .\release.ps1 -Arm64       … ARM 版 Windows 用（win-arm64）も作る
#   .\release.ps1 -Local       … 手元でパッケージを作るだけ（コミット・共有フォルダ・GitHub には触らない。試し用）
#
# 流れ: テスト → ビルド → Velopack でまとめる → 会社の共有フォルダ（<共有フォルダ>\App\Windows）に置く
#       → バージョンをコミットしてタグ（windows-vX.Y.Z）→ GitHub のリリースにも置く（gh でログインしていれば）
# 会社の人は共有フォルダの FerryApp-win-Setup.exe でインストールし、以後は Ferry が共有フォルダから自動更新する。
param(
    [string]$Version,
    [switch]$Arm64,
    [switch]$Local,
    # 共有フォルダ（省略時はこの PC の Ferry の設定から）
    [string]$SharedDirectory
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Fail([string]$message) {
    Write-Host "❌ $message" -ForegroundColor Red
    exit 1
}

function Run([string]$command, [string[]]$arguments) {
    & $command @arguments
    if ($LASTEXITCODE -ne 0) { Fail "$command $($arguments -join ' ') が失敗しました" }
}

if (-not $Local -and (git status --porcelain)) {
    Fail 'コミットしていない変更があります。先にコミットしてください'
}

# バージョン
$project = Join-Path $PSScriptRoot 'Ferry\Ferry.csproj'
$projectText = [IO.File]::ReadAllText($project)
$current = [regex]::Match($projectText, '<Version>(.*?)</Version>').Groups[1].Value
if (-not $Version) {
    $parts = $current.Split('.')
    $parts[-1] = [string]([int]$parts[-1] + 1)
    $Version = $parts -join '.'
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "バージョンは 0.2.0 のような 3 つの数字にしてください（$Version）" }
$tag = "windows-v$Version"
if (-not $Local -and (git tag --list $tag)) { Fail "$tag はすでにあります" }
Write-Host "🚀 Ferry for Windows $current → $Version"

# 共有フォルダ（更新の置き場所）
if (-not $SharedDirectory) {
    $settings = Join-Path $env:LOCALAPPDATA 'Ferry\settings.json'
    if (Test-Path $settings) {
        $SharedDirectory = (Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json).sharedDirectory
    }
}
$updateDirectory = if ($SharedDirectory) { Join-Path $SharedDirectory 'App\Windows' } else { $null }
if (-not $Local -and -not $updateDirectory) {
    Fail '共有フォルダが分かりません。Ferry の設定で共有フォルダを選ぶか、-SharedDirectory で指定してください'
}

# テスト（ネットワークを使うものは除く）
Run dotnet @('test', 'Ferry.Tests', '--filter', 'Category!=Network', '--nologo', '-v', 'q')

# バージョンを書き換える（-Local では元に戻す）
[IO.File]::WriteAllText($project, $projectText.Replace("<Version>$current</Version>", "<Version>$Version</Version>"))
try {
    Run dotnet @('tool', 'restore')
    $runtimes = @('win-x64') + $(if ($Arm64) { @('win-arm64') } else { @() })
    foreach ($runtime in $runtimes) {
        $platform = if ($runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }
        # x64 は既定のチャンネル（win）、ARM は別のチャンネルにする
        $channel = if ($runtime -eq 'win-arm64') { 'win-arm64' } else { 'win' }
        $publish = Join-Path $PSScriptRoot "publish\$runtime"
        $releases = Join-Path $PSScriptRoot "Releases\$runtime"
        Remove-Item -Recurse -Force $publish -ErrorAction SilentlyContinue
        Write-Host "🔨 $runtime をビルドしています…"
        Run dotnet @('publish', 'Ferry', '-c', 'Release', '-r', $runtime, "-p:Platform=$platform", '--self-contained', '-o', $publish, '--nologo', '-v', 'q')

        # 差分の更新を作れるよう、共有フォルダにある前回の版を取ってくる
        if ($updateDirectory -and (Test-Path $updateDirectory)) {
            Run dotnet @('vpk', 'download', 'local', '--path', $updateDirectory, '-c', $channel, '-o', $releases)
        }
        Write-Host "📦 $runtime をまとめています…"
        # パッケージ ID は FerryApp（インストール先は %LOCALAPPDATA%\FerryApp）。
        # Ferry にすると、設定を置いている %LOCALAPPDATA%\Ferry と重なり、アンインストールで設定まで消える
        Run dotnet @('vpk', 'pack', '-u', 'FerryApp', '-v', $Version, '-p', $publish, '-e', 'Ferry.exe',
            '-i', (Join-Path $PSScriptRoot 'Ferry\Assets\Ferry.ico'), '--packTitle', 'Ferry', '--packAuthors', 'Ferry',
            '-r', $runtime, '-c', $channel, '-o', $releases)

        if (-not $Local) {
            New-Item -ItemType Directory -Force $updateDirectory | Out-Null
            # 共有した全員の Dropbox に同期されるので、置くのは最新の版だけにする（約 200MB）。
            # 1 つ前の版の人は差分で、もっと古い版の人は完全版で更新できる
            Run dotnet @('vpk', 'upload', 'local', '--path', $updateDirectory, '-c', $channel, '-o', $releases, '--keepMaxReleases', '1')
            # ポータブル版（zip）は使わないので置かない（vpk には作らない設定が無いので、置いた後で消す）
            Get-ChildItem $updateDirectory -Filter '*-Portable.zip' | Remove-Item -Force
        }
    }
}
finally {
    if ($Local) { [IO.File]::WriteAllText($project, $projectText) }
}

if ($Local) {
    Write-Host "✅ Releases\ にパッケージを作りました（FerryApp-win-Setup.exe でインストールを試せます）"
    exit 0
}

# 会社の人向けの説明
$readme = @"
Ferry for Windows $Version（$(Get-Date -Format 'yyyy-MM-dd HH:mm') に作成）

インストール:
1. このフォルダの FerryApp-win-Setup.exe をダブルクリックする
   （ARM 版の Windows では FerryApp-win-arm64-Setup.exe）
2. 「Windows によって PC が保護されました」と出たら、「詳細情報」→「実行」を押す
3. 以後は、Ferry がこのフォルダから自動で更新されます
"@
[IO.File]::WriteAllText((Join-Path $updateDirectory 'はじめにお読みください.txt'), $readme, (New-Object Text.UTF8Encoding($true)))
Write-Host "☁️  $updateDirectory に置きました"

# リリースノート: 前の Windows 版のタグからのコミットの 1 行目（バージョン更新のコミットは除く）
$previous = git describe --tags --abbrev=0 --match 'windows-v*' 2>$null
$range = if ($previous) { "$previous..HEAD" } else { 'HEAD' }
$notes = (git log --pretty='- %s' $range -- . | Where-Object { $_ -notmatch '^- (Windows 版の)?バージョン ' }) -join "`n"
$notesFile = Join-Path $PSScriptRoot 'Releases\notes.md'
[IO.File]::WriteAllText($notesFile, "## 変更点`n$(if ($notes) { $notes } else { '- 初回リリース' })", (New-Object Text.UTF8Encoding($false)))

git add $project
git commit -qm "Windows 版のバージョン $Version"
git tag $tag
git push -q origin HEAD --tags

# GitHub のリリースにも置く（gh でログインしていれば）
$token = if (Get-Command gh -ErrorAction SilentlyContinue) { gh auth token 2>$null } else { $env:GITHUB_TOKEN }
if ($token) {
    foreach ($runtime in $runtimes) {
        $channel = if ($runtime -eq 'win-arm64') { 'win-arm64' } else { 'win' }
        Run dotnet @('vpk', 'upload', 'github', '-o', (Join-Path $PSScriptRoot "Releases\$runtime"), '-c', $channel,
            '--repoUrl', 'https://github.com/yama203/Ferry', '--token', $token, '--tag', $tag,
            '--releaseName', "Ferry for Windows $Version", '--publish', '--merge')
    }
    Write-Host "🐙 GitHub のリリース $tag に置きました"
}
else {
    Write-Host "ℹ️  gh でログインしていないので、GitHub のリリースは作っていません（gh auth login の後に作り直せます）"
}
Write-Host "✅ Ferry for Windows $Version"
