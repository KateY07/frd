# Builds frd-ffmpeg-<version>-setup.exe: a OneSetup installer (https://github.com/KateY07/onesetup) that copies the FFmpeg libraries
# next to FRD.exe. The zip package stays the scriptable alternative; this is the double-click one.
#   .\tools\FfmpegSetup\Build-FfmpegSetup.ps1 [-OneSetup <path to onesetup.exe>] [-OutputDirectory artifacts]
param(
    [string]$OneSetup,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\artifacts'),
    [string]$FfmpegVersion = '8.1.2'
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $OneSetup) {
    $OneSetup = (Get-Command onesetup -ErrorAction SilentlyContinue).Source
    if (-not $OneSetup -and (Test-Path 'D:\1\onesetup\onesetup\publish\onesetup.exe')) { $OneSetup = 'D:\1\onesetup\onesetup\publish\onesetup.exe' }
}
if (-not $OneSetup -or -not (Test-Path -LiteralPath $OneSetup)) { throw 'onesetup.exe not found. Pass -OneSetup <path> or put it on PATH.' }

$runtime = Join-Path $root 'third_party\ffmpeg\runtime'
$libraries = @('avcodec-62.dll', 'avutil-60.dll', 'swscale-9.dll', 'swresample-6.dll', 'LICENSE.txt')
foreach ($name in $libraries) { if (-not (Test-Path -LiteralPath (Join-Path $runtime $name))) { throw "Missing $name; run Get-FFmpeg.ps1 first." } }

$stage = Join-Path ([IO.Path]::GetTempPath()) ('frd-ffmpeg-setup-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $stage 'ffmpeg'
New-Item -ItemType Directory -Force -Path $payload | Out-Null
try {
    foreach ($name in $libraries) { Copy-Item -LiteralPath (Join-Path $runtime $name) -Destination $payload }
    Copy-Item -LiteralPath (Join-Path $root 'third_party\ffmpeg\SOURCE-OFFER.txt') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.bat') -Destination $stage
    # install.ps1 carries Chinese text and is run by Windows PowerShell 5.1, which needs the UTF-8 byte order mark.
    $script = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'install.ps1') -Raw -Encoding UTF8
    [IO.File]::WriteAllText((Join-Path $stage 'install.ps1'), $script, (New-Object System.Text.UTF8Encoding($true)))
    $lines = Get-ChildItem -LiteralPath $payload -File | ForEach-Object { '{0} *{1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }
    [IO.File]::WriteAllText((Join-Path $stage 'files.sha256'), (($lines -join "`n") + "`n"))

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $output = [IO.Path]::GetFullPath((Join-Path $OutputDirectory "frd-ffmpeg-$FfmpegVersion-setup.exe"))
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output }   # onesetup never overwrites
    & $OneSetup $stage --ui -o $output -l 9
    if ($LASTEXITCODE -ne 0) { throw "onesetup failed ($LASTEXITCODE)." }
    $hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$output.sha256", "$hash *$([IO.Path]::GetFileName($output))`n")
    '{0} ({1:N1} MB)  sha256 {2}' -f $output, ((Get-Item -LiteralPath $output).Length / 1MB), $hash
}
finally { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
