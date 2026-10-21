param(
    [Parameter(Mandatory = $true)][string]$PackageRoot,
    [Parameter(Mandatory = $true)][string]$ServiceDirectory
)
$ErrorActionPreference = 'Stop'
$result = Join-Path $PackageRoot 'service-install-result.txt'
try {
    $service = Get-Service -Name FRDSecureCaptureProbe
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name FRDSecureCaptureProbe -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
    }
    Copy-Item -LiteralPath (Join-Path $PackageRoot 'service\SecureDesktopProbe.exe'),
        (Join-Path $PackageRoot 'service\SecureDesktopProbe.dll'),
        (Join-Path $PackageRoot 'service\SecureDesktopProbe.deps.json'),
        (Join-Path $PackageRoot 'service\SecureDesktopProbe.runtimeconfig.json') -Destination $ServiceDirectory -Force
    $keyDirectory = Join-Path $env:ProgramData 'FRD\SecureDesktopProbe'
    New-Item -ItemType Directory -Path $keyDirectory -Force | Out-Null
    $keyPath = Join-Path $keyDirectory 'capture.key'
    if (-not (Test-Path -LiteralPath $keyPath)) {
        $bytes = New-Object byte[] 32
        $random = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $random.GetBytes($bytes) }
        finally { $random.Dispose() }
        [IO.File]::WriteAllBytes($keyPath, $bytes)
    }
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    & icacls.exe $keyPath /inheritance:r /grant:r 'SYSTEM:(F)' "*$($sid):(R)" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not restrict access to $keyPath" }
    $framePath = Join-Path $keyDirectory 'frames.map'
    $frameSize = [long]64 + 3 * ([long]16 + [long]3840 * 2160 * 4)
    $frame = [IO.File]::Open($framePath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::ReadWrite)
    try { $frame.SetLength($frameSize) }
    finally { $frame.Dispose() }
    & icacls.exe $framePath /inheritance:r /grant:r 'SYSTEM:(F)' "*$($sid):(F)" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not restrict access to $framePath" }
    Start-Service -Name FRDSecureCaptureProbe
    $service = Get-Service -Name FRDSecureCaptureProbe
    $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(15))
    [IO.File]::WriteAllText($result, 'OK: SYSTEM helper updated; UDP key restricted to SYSTEM and launching user.')
}
catch {
    [IO.File]::WriteAllText($result, "FAILED: $($_.Exception)")
    throw
}
