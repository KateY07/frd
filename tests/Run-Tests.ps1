# Builds and runs the test programs that still compile and need no special setup. Prints one line per test and exits non-zero if any failed.
# stdout/stderr of each test go to stdout.txt / stderr.txt in its build folder under %TEMP%.
# Some tests need the real desktop (DXGI) or spawn FRD.exe child processes; run this from an interactive, signed-in session.
param([int]$TimeoutSeconds = 180)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tests = @(
    @{ Name = 'Stats';       Project = 'tests/Stats/StatsTests.csproj';                 Args = @() },
    @{ Name = 'Diagnostics'; Project = 'tests/Diagnostics/DiagnosticTests.csproj';      Args = @() },
    @{ Name = 'Core';        Project = 'tests/Core/Core.csproj';                        Args = @('--transport', (Join-Path ([IO.Path]::GetTempPath()) 'frd-core-transport.json')) }
)
$failed = 0
foreach ($test in $tests) {
    $out = Join-Path ([IO.Path]::GetTempPath()) ('frd-test-' + $test.Name)
    & dotnet build (Join-Path $root $test.Project) -c Release -nologo -v q -o $out | Out-Null
    if ($LASTEXITCODE -ne 0) { "{0,-12} BUILD FAILED" -f $test.Name; $failed++; continue }
    $exe = Get-ChildItem -LiteralPath $out -Filter '*.exe' | Select-Object -First 1
    $start = @{ FilePath = $exe.FullName; WorkingDirectory = $out; PassThru = $true; NoNewWindow = $true; RedirectStandardOutput = (Join-Path $out 'stdout.txt'); RedirectStandardError = (Join-Path $out 'stderr.txt') }
    if ($test.Args.Count -gt 0) { $start.ArgumentList = $test.Args }
    $process = Start-Process @start
    $null = $process.Handle   # keeps the exit code readable after the process ends
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { $process.Kill(); "{0,-12} TIMEOUT after {1}s" -f $test.Name, $TimeoutSeconds; $failed++; continue }
    if ($process.ExitCode -eq 0) { "{0,-12} PASS" -f $test.Name } else { "{0,-12} FAIL (exit {1})" -f $test.Name, $process.ExitCode; $failed++ }
}
exit $failed
