# Builds frd-service-<version>-setup.exe: a OneSetup installer (https://github.com/KateY07/onesetup) that installs, updates or removes the FRD
# secure-desktop helper service (frd-wintools.exe). Run it after Publish.ps1 has produced the staged tools.
#   .\tools\ServiceSetup\Build-ServiceSetup.ps1 -Version v5.3 -WintoolsPath <frd-wintools.exe> [-OneSetup <onesetup.exe>] [-OutputDirectory artifacts\v5.3]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$WintoolsPath,
    [string]$OneSetup,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\artifacts')
)
$ErrorActionPreference = 'Stop'
if (-not $OneSetup) {
    $OneSetup = (Get-Command onesetup -ErrorAction SilentlyContinue).Source
    if (-not $OneSetup -and (Test-Path 'D:\1\onesetup\onesetup\publish\onesetup.exe')) { $OneSetup = 'D:\1\onesetup\onesetup\publish\onesetup.exe' }
}
if (-not $OneSetup -or -not (Test-Path -LiteralPath $OneSetup)) { throw 'onesetup.exe not found. Pass -OneSetup <path> or put it on PATH.' }
if (-not (Test-Path -LiteralPath $WintoolsPath)) { throw "frd-wintools.exe not found: $WintoolsPath" }

$stage = Join-Path ([IO.Path]::GetTempPath()) ('frd-service-setup-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $stage | Out-Null
try {
    Copy-Item -LiteralPath $WintoolsPath -Destination (Join-Path $stage 'frd-wintools.exe')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.bat') -Destination $stage
    [IO.File]::WriteAllText((Join-Path $stage 'version.txt'), "$Version`n")
    # install.ps1 carries Chinese text and is run by Windows PowerShell 5.1, which needs the UTF-8 byte order mark.
    $script = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'install.ps1') -Raw -Encoding UTF8
    [IO.File]::WriteAllText((Join-Path $stage 'install.ps1'), $script, (New-Object System.Text.UTF8Encoding($true)))
    $hash = (Get-FileHash -LiteralPath (Join-Path $stage 'frd-wintools.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $stage 'files.sha256'), "$hash *frd-wintools.exe`n")

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $output = [IO.Path]::GetFullPath((Join-Path $OutputDirectory "frd-service-$Version-setup.exe"))
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output }   # onesetup never overwrites
    & $OneSetup $stage --ui -o $output -l 9
    if ($LASTEXITCODE -ne 0) { throw "onesetup failed ($LASTEXITCODE)." }
    $outputHash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$output.sha256", "$outputHash *$([IO.Path]::GetFileName($output))`n")
    '{0} ({1:N1} MB)  sha256 {2}' -f $output, ((Get-Item -LiteralPath $output).Length / 1MB), $outputHash
}
finally { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
