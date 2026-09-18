param([string]$Executable = '', [switch]$Qsv)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable = Join-Path $root 'bin/live-demo/FRD.exe' }
$Executable = [IO.Path]::GetFullPath($Executable)
$output = Join-Path $root 'results/release-v1.pre1'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$hostLog = Join-Path $output 'remote-host.stderr.log'
$hostProcess = $null
$controller = $null
$negative = $null
try {
    $hostProcess = Start-Process -FilePath $Executable -WorkingDirectory (Split-Path $Executable) -ArgumentList '--host --listen 127.0.0.1 --port 45170 --token regression-local-45170' -WindowStyle Normal -RedirectStandardError $hostLog -RedirectStandardOutput (Join-Path $output 'remote-host.stdout.log') -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    do {
        Start-Sleep -Milliseconds 100
        if ($hostProcess.HasExited) { throw 'Regression host exited before listening.' }
        $ready = (Test-Path -LiteralPath $hostLog) -and ((Get-Content -LiteralPath $hostLog -Raw) -match 'Remote listener ready')
    } until ($ready -or [DateTime]::UtcNow -gt $deadline)
    if (-not $ready) { throw 'Regression host did not start listening.' }
    $harness = Join-Path $PSScriptRoot 'Remote/bin/Release/net10.0-windows10.0.26100.0/RemoteTests.dll'
    $config = Join-Path (Split-Path $Executable) 'codec-config.json'
    $arguments = @($harness, $config, (Join-Path $output 'remote.json'))
    if ($Qsv) { $arguments += '--qsv' }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Remote control regression failed.' }
    $report = Join-Path $output 'remote-ui.json'
    $controller = Start-Process -FilePath $Executable -WorkingDirectory (Split-Path $Executable) -ArgumentList ('--connect 127.0.0.1 --port 45170 --token regression-local-45170 --test-seconds 4 --report "' + $report + '"') -WindowStyle Normal -RedirectStandardError (Join-Path $output 'remote-ui.stderr.log') -RedirectStandardOutput (Join-Path $output 'remote-ui.stdout.log') -PassThru
    if (-not $controller.WaitForExit(25000)) { throw 'Remote UI regression timed out.' }
    if ($controller.ExitCode -ne 0 -or -not (Get-Content -LiteralPath $report -Raw | ConvertFrom-Json).Passed) { throw 'Remote GPU presentation regression failed.' }
    $failedReport = Join-Path $output 'remote-ui-rejected.json'
    $negative = Start-Process -FilePath $Executable -WorkingDirectory (Split-Path $Executable) -ArgumentList ('--connect 127.0.0.1 --port 45170 --token invalid-regression-token --test-seconds 1 --report "' + $failedReport + '"') -WindowStyle Normal -PassThru
    if (-not $negative.WaitForExit(15000) -or $negative.ExitCode -ne 1 -or (Get-Content -LiteralPath $failedReport -Raw | ConvertFrom-Json).Passed) { throw 'Rejected UI connection did not return failure.' }
    Write-Output 'Remote control and real desktop GPU presentation: PASS'
}
finally {
    foreach ($process in @($negative, $controller, $hostProcess)) {
        if ($null -eq $process -or $process.HasExited) { continue }
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(8000)) { Write-Warning ('Regression process did not close: ' + $process.Id); $process.Kill(); $process.WaitForExit() }
    }
}
