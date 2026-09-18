param([string]$SevenZip = 'C:/Program Files/7-Zip/7z.exe')
$ErrorActionPreference = 'Stop'
$dependencyRoot = Join-Path $PSScriptRoot 'third_party/ffmpeg'
$provenance = Get-Content -LiteralPath (Join-Path $dependencyRoot 'provenance.json') -Raw | ConvertFrom-Json
if (-not (Test-Path -LiteralPath $SevenZip -PathType Leaf)) {
    $SevenZip = (Get-Command 7z -ErrorAction Stop).Source
}
$archivePath = Join-Path $dependencyRoot ('ffmpeg-' + $provenance.version + '.7z')
if (-not (Test-Path -LiteralPath $archivePath)) {
    Invoke-WebRequest -Uri $provenance.url -OutFile $archivePath
}
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $provenance.sha256) {
    throw 'FFmpeg SHA-256 does not match the pinned distribution. Archive was not extracted.'
}
& $SevenZip x $archivePath ('-o' + $dependencyRoot) -y
if ($LASTEXITCODE -ne 0) { throw 'FFmpeg extraction failed.' }
$distribution = Join-Path $dependencyRoot ('ffmpeg-' + $provenance.version)
$runtime = Join-Path $dependencyRoot 'runtime'
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
Get-ChildItem -LiteralPath (Join-Path $distribution 'bin') -File | Copy-Item -Destination $runtime
Copy-Item -LiteralPath (Join-Path $distribution 'LICENSE') -Destination (Join-Path $runtime 'LICENSE.txt')
Write-Output ('FFmpeg runtime ready: ' + $runtime)
