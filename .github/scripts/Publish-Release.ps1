param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$NotesPath,
    [Parameter(Mandatory = $true)][string[]]$Assets
)
$ErrorActionPreference = 'Stop'
if (-not $env:GITHUB_TOKEN) { throw 'GITHUB_TOKEN is unavailable.' }
if (-not $env:GITHUB_REPOSITORY) { throw 'GITHUB_REPOSITORY is unavailable.' }
$headers = @{
    Authorization = 'Bearer ' + $env:GITHUB_TOKEN
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
}
$payload = @{
    tag_name = $Tag
    name = 'FRD ' + $Tag
    body = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $NotesPath))
    draft = $false
    prerelease = $true
} | ConvertTo-Json
$body = [Text.Encoding]::UTF8.GetBytes($payload)
$release = Invoke-RestMethod -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' `
    -Body $body -Uri "https://api.github.com/repos/$env:GITHUB_REPOSITORY/releases"
foreach ($asset in $Assets) {
    $path = (Resolve-Path -LiteralPath $asset).Path
    $name = [Uri]::EscapeDataString([IO.Path]::GetFileName($path))
    $contentType = if ($path.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) { 'application/zip' } else { 'text/plain' }
    Invoke-RestMethod -Method Post -Headers $headers -ContentType $contentType -InFile $path `
        -Uri "https://uploads.github.com/repos/$env:GITHUB_REPOSITORY/releases/$($release.id)/assets?name=$name" | Out-Null
}
Write-Output $release.html_url
