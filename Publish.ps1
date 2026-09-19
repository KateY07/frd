$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'FRD.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.InformationalVersion
if ($version -notmatch '^v\d+\.pre\d+$') { throw 'Invalid release version.' }
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "artifacts/$version"))
$output = Join-Path $releaseRoot 'win-x64'
$staging = Join-Path $releaseRoot ('staging-' + [Guid]::NewGuid().ToString('N'))
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'third_party/ffmpeg/runtime/avcodec-62.dll'))) {
    throw 'Run Get-FFmpeg.ps1 first.'
}
& dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishAot=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $staging
if ($LASTEXITCODE -ne 0) { throw "Publish failed; staging retained at $staging" }
$documents = @('README.md', 'THIRD-PARTY-NOTICES.md', 'docs/使用.md', 'docs/目标与验收.md',
    'docs/本机输入延迟验证.md', 'docs/公网拥塞控制调查.md', 'docs/受限公网回归设计.md',
    "docs/$version-发布说明.md", "docs/$version-静态检查.md")
foreach ($document in $documents) {
    $destination = Join-Path $staging $document
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $document) -Destination $destination
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs/使用.md') -Destination $staging
$productVersion = (Get-Item -LiteralPath (Join-Path $staging 'FRD.exe')).VersionInfo.ProductVersion
if (-not $productVersion.StartsWith($version + '+') -and $productVersion -ne $version) { throw 'Published binary version mismatch.' }
foreach ($required in @('codec-config.json', 'ffmpeg/avcodec-62.dll', 'ffmpeg/avutil-60.dll', 'ffmpeg/LICENSE.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $staging $required))) { throw "Missing release file: $required" }
}
if (Get-ChildItem -LiteralPath $staging -Filter '*Tests*' -Recurse) { throw 'Test executable leaked into release.' }
$workspace = [IO.Path]::GetFullPath($PSScriptRoot) + [IO.Path]::DirectorySeparatorChar
foreach ($path in @($staging, $output)) {
    if (-not $path.StartsWith($workspace, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish path escapes workspace.' }
}
$symbols = [IO.Path]::GetFullPath((Join-Path $releaseRoot ('symbols-' + [Guid]::NewGuid().ToString('N'))))
if (-not $symbols.StartsWith($workspace, [StringComparison]::OrdinalIgnoreCase)) { throw 'Symbols path escapes workspace.' }
foreach ($symbol in Get-ChildItem -LiteralPath $staging -Filter '*.pdb' -File -Recurse) {
    $symbolDestination = Join-Path $symbols ([IO.Path]::GetRelativePath($staging, $symbol.FullName))
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
Move-Item -LiteralPath $freshZip -Destination $archivePath -Force
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $releaseRoot 'SHA256SUMS.txt'), "$hash  $([IO.Path]::GetFileName($archivePath))`n")
Write-Output "Published $productVersion`: $archivePath"
