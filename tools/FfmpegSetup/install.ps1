# Installs the FFmpeg libraries for FRD into the current user's profile:  %LOCALAPPDATA%\FRD\ffmpeg\<version>\
# FRD looks there on its own (newest version first), so the installer does not have to find FRD.exe, works for the single-file frd-<version>.exe
# too, and one copy serves every FRD version. Nothing outside the user profile is touched.
#
# Runs inside a OneSetup installer window: lines starting with "." drive the window (.title .info .warn .error .progress .ask), answers arrive on stdin.
# Exit codes: 0 installed, 1 failed, 2 cancelled.
#
# Environment (scripted installs and tests):  FFMPEG_TARGET  install into this folder instead of the default
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$interactive = ($env:ONESETUP_SHELL -eq 'ui') -and [string]::IsNullOrEmpty($env:FFMPEG_TARGET)

function Say([string]$line) {
    [Console]::Out.WriteLine($line); [Console]::Out.Flush()
    if ($env:FRD_SETUP_TRACE) { Add-Content -LiteralPath $env:FRD_SETUP_TRACE -Value $line -Encoding UTF8 }   # for tests: the installer window hides stdout
}
function Info([string]$text) { Say ".info $text" }
function Progress([int]$value) { Say ".progress $value" }
function Fail([string]$text, [int]$code = 1) { Say ".error $text"; exit $code }
function Ask([string]$question, [string]$yes = 'Yes', [string]$no = 'No') {
    Say ".ask $question | $yes | $no"
    return ([Console]::In.ReadLine() -eq 'yes')
}

Say '.title FFmpeg extension for FRD / FRD 的 FFmpeg 扩展'
Progress 3

$version = (Get-Content -LiteralPath (Join-Path $here 'version.txt') -TotalCount 1).Trim()
if ([string]::IsNullOrWhiteSpace($version)) { Fail 'The installer is missing its version file.' }
if ($env:FFMPEG_TARGET) { $destination = $env:FFMPEG_TARGET }
else {
    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { Fail 'LOCALAPPDATA is not set; cannot find your profile. / 找不到用户目录。' }
    $destination = Join-Path $env:LOCALAPPDATA "FRD\ffmpeg\$version"
}
Info "Target: $destination"
Progress 10

if ($interactive) {
    $existing = Join-Path $destination 'avcodec-62.dll'
    if (Test-Path -LiteralPath $existing) {
        $current = (Get-Item -LiteralPath $existing).VersionInfo.FileVersion
        if (-not (Ask "FFmpeg $version is already installed (avcodec $current). Replace it?" 'Replace' 'Cancel')) { Fail 'Cancelled.' 2 }
    }
    elseif (-not (Ask "Install FFmpeg $version for FRD in $destination ? Nothing outside your user profile is touched. / 安装到当前用户目录，不会改动其他位置。" 'Install' 'Cancel')) { Fail 'Cancelled.' 2 }
}

# ---- copy -----------------------------------------------------------------------------------------------------------
$source = Join-Path $here 'ffmpeg'
Info 'Copying FFmpeg libraries / 正在复制 FFmpeg 库'
try {
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    $files = Get-ChildItem -LiteralPath $source -File
    $index = 0
    foreach ($file in $files) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination $file.Name) -Force
        $index++
        Progress (10 + [int](75 * $index / $files.Count))
    }
}
catch {
    Fail "Could not write to $destination ($($_.Exception.Message)). Close FRD and try again. / 无法写入，请先关闭 FRD 再试。"
}

# ---- verify ---------------------------------------------------------------------------------------------------------
Info 'Verifying the copy / 正在校验'
$expected = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $here 'files.sha256')) {
    if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+)$') { $expected[$Matches[2].Trim()] = $Matches[1].ToLowerInvariant() }
}
foreach ($name in $expected.Keys) {
    $path = Join-Path $destination $name
    if (-not (Test-Path -LiteralPath $path)) { Fail "Missing after copy: $name" }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected[$name]) { Fail "Checksum mismatch after copy: $name" }
}
Progress 97
$built = (Get-Item -LiteralPath (Join-Path $destination 'avcodec-62.dll')).VersionInfo.FileVersion
Info "Installed FFmpeg $version ($($expected.Count) files, avcodec $built) in $destination"
Info 'FRD finds it automatically; restart FRD to use the FFmpeg presets. / FRD 会自动找到它，重启 FRD 即可使用 FFmpeg 预设。'
Progress 100
exit 0
