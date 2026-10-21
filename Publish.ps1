$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'FRD.csproj'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.InformationalVersion
if ($version -notmatch '^v\d+\.pre\d+$') { throw 'Invalid release version.' }
$localReleaseRoot = 'D:\pub\FRD'
$localArchive = Join-Path $localReleaseRoot "$version.zip"
$localHash = "$localArchive.sha256"
if ((Test-Path -LiteralPath $localArchive) -or (Test-Path -LiteralPath $localHash)) {
    throw "Local release already exists: $localArchive"
}
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "artifacts/$version"))
$output = Join-Path $releaseRoot 'win-x64'
$staging = Join-Path $releaseRoot ('staging-' + [Guid]::NewGuid().ToString('N'))
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'third_party/ffmpeg/runtime/avcodec-62.dll'))) {
    throw 'Run Get-FFmpeg.ps1 first.'
}
# NativeAOT single native exe; FFmpeg is an optional extension package, so the base package carries none of it.
& dotnet publish $project -c Release -r win-x64 -p:IncludeFfmpegRuntime=false -p:DebugType=None -o $staging
if ($LASTEXITCODE -ne 0) { throw "Publish failed; staging retained at $staging" }
# frd-wintools.exe: the secure-desktop (UAC / lock screen) helper service, kept out of FRD.exe.
& dotnet publish (Join-Path $PSScriptRoot 'tools/FrdWinTools/FrdWinTools.csproj') -c Release -r win-x64 -p:DebugType=None -o $staging
if ($LASTEXITCODE -ne 0) { throw "frd-wintools publish failed; staging retained at $staging" }
$startupError = Join-Path $staging 'startup-error.txt'
if (Test-Path -LiteralPath $startupError) { Remove-Item -LiteralPath $startupError }
$documents = @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'licenses/dotnet-ThirdPartyNotices.txt', 'docs/使用.md', 'docs/目标与验收.md',
    'docs/本机输入延迟验证.md', 'docs/公网拥塞控制调查.md', 'docs/受限公网回归设计.md',
    "docs/$version-发布说明.md", "docs/$version-静态检查.md", 'docs/输入生命周期修复.md', 'docs/单端口输入延迟回归.md')
foreach ($document in $documents) {
    $destination = Join-Path $staging $document
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $document) -Destination $destination
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs/使用.md') -Destination $staging
$productVersion = (Get-Item -LiteralPath (Join-Path $staging 'FRD.exe')).VersionInfo.ProductVersion
if (-not $productVersion.StartsWith($version + '+') -and $productVersion -ne $version) { throw 'Published binary version mismatch.' }
foreach ($required in @('codec-config.json', 'FRD.exe', 'frd-wintools.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $staging $required))) { throw "Missing release file: $required" }
}
if (Test-Path -LiteralPath (Join-Path $staging 'ffmpeg')) { throw 'The base package must not contain FFmpeg.' }
if (Get-ChildItem -LiteralPath $staging -Filter '*.dll' -File) { throw 'The NativeAOT base package must be a single exe with no managed DLLs.' }
if (Get-ChildItem -LiteralPath $staging -Filter '*.xml' -File) { throw 'Documentation XML files must not ship in the package.' }
if (Get-ChildItem -LiteralPath $staging -Filter '*Tests*' -Recurse) { throw 'Test executable leaked into release.' }
if (Get-ChildItem -LiteralPath $staging -Filter 'startup-error.txt' -Recurse) { throw 'Runtime error log leaked into release.' }
$workspace = [IO.Path]::GetFullPath($PSScriptRoot) + [IO.Path]::DirectorySeparatorChar
foreach ($path in @($staging, $output)) {
    if (-not $path.StartsWith($workspace, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish path escapes workspace.' }
}
$symbols = [IO.Path]::GetFullPath((Join-Path $releaseRoot ('symbols-' + [Guid]::NewGuid().ToString('N'))))
if (-not $symbols.StartsWith($workspace, [StringComparison]::OrdinalIgnoreCase)) { throw 'Symbols path escapes workspace.' }
foreach ($symbol in Get-ChildItem -LiteralPath $staging -Filter '*.pdb' -File -Recurse) {
    $stagingPrefix = $staging.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $symbol.FullName.StartsWith($stagingPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Symbol path escapes staging.' }
    $symbolDestination = Join-Path $symbols ($symbol.FullName.Substring($stagingPrefix.Length))
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $symbolDestination) | Out-Null
    Move-Item -LiteralPath $symbol.FullName -Destination $symbolDestination
}
if (Test-Path -LiteralPath $output) {
    $archive = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('archive/replaced-builds/' + $version + '-' + [Guid]::NewGuid().ToString('N'))))
    if (-not $archive.StartsWith($workspace, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive path escapes workspace.' }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $archive) | Out-Null
    Move-Item -LiteralPath $output -Destination $archive
}
Move-Item -LiteralPath $staging -Destination $output
$archivePath = Join-Path $releaseRoot "frd-$version-win-x64.zip"
if (Test-Path -LiteralPath $archivePath) { throw "Workspace release archive already exists: $archivePath" }
$zipTool = (Get-Command 7z -ErrorAction SilentlyContinue).Source
if (-not $zipTool -and (Test-Path -LiteralPath 'C:/Program Files/7-Zip/7z.exe')) { $zipTool = 'C:/Program Files/7-Zip/7z.exe' }
if (-not $zipTool) { throw '7-Zip is required to package the release.' }
# A fresh archive cannot inherit stale members from any previous package.
$freshZip = Join-Path $releaseRoot ('package-' + [Guid]::NewGuid().ToString('N') + '.zip')
& $zipTool a -tzip -mx=1 -mcu=on $freshZip (Join-Path $output '*')
if ($LASTEXITCODE -ne 0) { throw 'Release archive creation failed.' }
$verification = [IO.Compression.ZipFile]::OpenRead($freshZip)
try {
    foreach ($document in $documents) {
        if ($null -eq $verification.GetEntry($document)) { throw "Archive document filename mismatch: $document" }
    }
}
finally { $verification.Dispose() }
Move-Item -LiteralPath $freshZip -Destination $archivePath
# Optional FFmpeg extension: unzip next to FRD.exe to add the FFmpeg (software H.264/H.265/AV1) presets.
$extension = Join-Path $releaseRoot ('ffmpeg-' + [Guid]::NewGuid().ToString('N'))
$extensionFiles = @('avcodec-62.dll', 'avutil-60.dll', 'swscale-9.dll', 'swresample-6.dll', 'LICENSE.txt')
New-Item -ItemType Directory -Force -Path (Join-Path $extension 'ffmpeg') | Out-Null
foreach ($file in $extensionFiles) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "third_party/ffmpeg/runtime/$file") -Destination (Join-Path $extension 'ffmpeg') -ErrorAction Stop
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.md') -Destination $extension
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'third_party/ffmpeg/SOURCE-OFFER.txt') -Destination (Join-Path $extension 'ffmpeg') -ErrorAction Stop
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $extension -ErrorAction Stop
$extensionArchive = Join-Path $releaseRoot "frd-$version-ffmpeg-win-x64.zip"
if (Test-Path -LiteralPath $extensionArchive) { Remove-Item -LiteralPath $extensionArchive }
& $zipTool a -tzip -mx=5 -mcu=on $extensionArchive (Join-Path $extension '*')
if ($LASTEXITCODE -ne 0) { throw 'FFmpeg extension archive creation failed.' }
Remove-Item -LiteralPath $extension -Recurse -Force
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
$extensionHash = (Get-FileHash -LiteralPath $extensionArchive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $releaseRoot 'SHA256SUMS.txt'), "$hash  $([IO.Path]::GetFileName($archivePath))`n$extensionHash  $([IO.Path]::GetFileName($extensionArchive))`n")
New-Item -ItemType Directory -Force -Path $localReleaseRoot | Out-Null
Copy-Item -LiteralPath $archivePath -Destination $localArchive -ErrorAction Stop
[IO.File]::WriteAllText($localHash, "$hash *$version.zip`n")
$localExtension = Join-Path $localReleaseRoot "$version-ffmpeg.zip"
if (Test-Path -LiteralPath $localExtension) { throw "Local FFmpeg extension already exists: $localExtension" }
Copy-Item -LiteralPath $extensionArchive -Destination $localExtension -ErrorAction Stop
[IO.File]::WriteAllText("$localExtension.sha256", "$extensionHash *$version-ffmpeg.zip`n")
Write-Output "Published $productVersion`: $localArchive"
