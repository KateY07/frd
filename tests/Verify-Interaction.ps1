param([Parameter(Mandatory)][string]$ControllerReport, [Parameter(Mandatory)][string]$FixtureReport, [Parameter(Mandatory)][string]$OutputReport)
$ErrorActionPreference = 'Stop'
$controller = Get-Content -LiteralPath $ControllerReport -Raw -Encoding UTF8 | ConvertFrom-Json
$fixture = Get-Content -LiteralPath $FixtureReport -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $controller.passed) { throw 'Controller interaction regression failed.' }
$inputs = @($fixture.inputs | Where-Object injected)
$clicks = @($inputs | Where-Object message -eq 513)
$checks = [Collections.Generic.List[object]]::new()
if ($clicks.Count -ne $controller.expectedInput.Count) { throw "Expected $($controller.expectedInput.Count) injected clicks, received $($clicks.Count)." }
for ($i = 0; $i -lt $clicks.Count; $i++) {
    $expected = $controller.expectedInput[$i]
    $distance = [Math]::Max([Math]::Abs($clicks[$i].screenX - $expected.targetX), [Math]::Abs($clicks[$i].screenY - $expected.targetY))
    $passed = $distance -le $expected.pixelTolerance
    $checks.Add(@{name="Native click $i at $($expected.scale)x"; passed=$passed; errorPixels=$distance; tolerance=$expected.pixelTolerance})
    if (-not $passed) { throw "Incorrect click position at step $i." }
}
$keyDown = @($inputs | Where-Object { $_.message -eq 256 -and (($_.lParam -shr 16) -band 255) -eq 45 }).Count
$keyUp = @($inputs | Where-Object { $_.message -eq 257 -and (($_.lParam -shr 16) -band 255) -eq 45 }).Count
$wheel = @($inputs | Where-Object { $_.message -eq 522 -and (($_.wParam -shr 16) -band 65535) -eq 120 }).Count
if ($keyDown -ne 9 -or $keyUp -ne 9 -or $wheel -ne 9) { throw "Native keyboard/wheel mismatch: down=$keyDown up=$keyUp wheel=$wheel" }
$checks.Add(@{name='Native target receives 9 scan-code down/up pairs and 9 wheel events';passed=$true})
$result = @{passed=$true; clicks=$clicks.Count; keyDown=$keyDown; keyUp=$keyUp; wheel=$wheel; checks=$checks; scope='Actual Win32 target messages after production TCP forwarding and SendInput; three controller sizes and transmission scales.'}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputReport -Encoding UTF8
Write-Output "PASS: 9 native clicks, 9 key pairs, 9 wheel events across three sizes/scales."
