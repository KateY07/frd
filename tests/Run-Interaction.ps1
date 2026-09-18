param([string]$BinaryDirectory = '', [switch]$NoInput)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $BinaryDirectory) { $BinaryDirectory = Join-Path $root 'bin/interaction-tests' }
$directory = Join-Path $root ('results/interaction/local-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$fixtureDirectory = Join-Path $directory 'fixture'
New-Item -ItemType Directory -Force -Path $fixtureDirectory | Out-Null
$fixture = $null; $server = $null; $controller = $null
try {
    $fixtureMode = if($NoInput) { '--clipboard-fixture' } else { '--fixture' }
    $fixture = Start-Process -FilePath (Join-Path $BinaryDirectory 'InteractionTests.exe') -ArgumentList @($fixtureMode, $fixtureDirectory) -WindowStyle Hidden -RedirectStandardError (Join-Path $directory 'fixture.err.log') -RedirectStandardOutput (Join-Path $directory 'fixture.out.log') -PassThru
    $traceBefore = $env:FRD_TRACE_INPUT
    try {
        $env:FRD_TRACE_INPUT = '1'
        $server = Start-Process -FilePath (Join-Path $BinaryDirectory 'FRD.exe') -WorkingDirectory $BinaryDirectory -ArgumentList '--host --listen 127.0.0.1 --port 45180 --token local-interaction-test' -WindowStyle Normal -RedirectStandardError (Join-Path $directory 'host.err.log') -RedirectStandardOutput (Join-Path $directory 'host.out.log') -PassThru
    }
    finally { $env:FRD_TRACE_INPUT = $traceBefore }
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path (Join-Path $fixtureDirectory 'state.json')) -or -not ((Get-Content (Join-Path $directory 'host.err.log') -Raw) -match 'Remote listener ready')) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Test fixture/host did not start.' }
        Start-Sleep -Milliseconds 100
    }
    $s = Get-Content (Join-Path $fixtureDirectory 'state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $NoInput -and (-not $s.visible -or -not $s.targetReady)) { throw 'Fixture must be visible and own target coordinates before sending input.' }
    $scriptPath = Join-Path $directory 'script.json'
    @{sourceWidth=$s.sourceWidth; sourceHeight=$s.sourceHeight; left=$s.left; top=$s.top; width=$s.width; height=$s.height; fixtureWindow=$s.fixtureWindow; input=(-not $NoInput); report=(Join-Path $directory 'controller.json')} | ConvertTo-Json | Set-Content $scriptPath -Encoding UTF8
    $arguments = @('--connect', '127.0.0.1', '--port', '45180', '--token', 'local-interaction-test', '--interaction-script', $scriptPath, '--report', (Join-Path $directory 'ui.json'))
    $controller = Start-Process -FilePath (Join-Path $BinaryDirectory 'FRD.exe') -WorkingDirectory $BinaryDirectory -ArgumentList $arguments -WindowStyle Normal -RedirectStandardError (Join-Path $directory 'controller.err.log') -RedirectStandardOutput (Join-Path $directory 'controller.out.log') -PassThru
    if (-not $controller.WaitForExit(45000)) { throw 'Interaction UI timed out.' }
    Set-Content (Join-Path $fixtureDirectory 'stop') 'done'
    [void]$fixture.WaitForExit(5000)
    if ($controller.ExitCode -ne 0) { throw 'Interaction UI returned failure.' }
    if(-not $NoInput) { & (Join-Path $PSScriptRoot 'Verify-Interaction.ps1') -ControllerReport (Join-Path $directory 'controller.json') -FixtureReport (Join-Path $fixtureDirectory 'state.json') -OutputReport (Join-Path $directory 'verified.json') }
    if (-not (Get-Content (Join-Path $directory 'ui.json') -Raw | ConvertFrom-Json).Passed) { throw 'GPU UI regression failed.' }
    Write-Output $directory
}
finally {
    Set-Content (Join-Path $fixtureDirectory 'stop') 'done'
    foreach ($process in @($controller, $fixture, $server)) {
        if ($null -eq $process -or $process.HasExited) { continue }
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { Write-Warning "Test process did not exit: $($process.Id)"; $process.Kill() }
    }
}
