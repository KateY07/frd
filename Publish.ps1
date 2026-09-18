$ErrorActionPreference = 'Stop'
$output = Join-Path $PSScriptRoot 'artifacts/v1.pre1/win-x64'
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'third_party/ffmpeg/runtime/avcodec-62.dll'))) {
    throw 'Run Get-FFmpeg.ps1 first.'
}
& dotnet publish (Join-Path $PSScriptRoot 'FRD.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs/使用.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs/v1.pre1-验证.md') -Destination $output
Write-Output ('Run FRD.exe in: ' + $output)
