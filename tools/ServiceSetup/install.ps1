# Installs, updates or removes the FRD secure-desktop helper service (frd-wintools.exe): UAC prompt and lock screen capture, and input
# for elevated windows such as Task Manager. The service runs as SYSTEM from %ProgramFiles%\FRD; the UAC prompt that grants this is yours to confirm.
#
# Runs inside a OneSetup installer window: lines starting with "." drive the window (.title .info .warn .error .progress .ask), answers arrive on stdin.
# Exit codes: 0 done, 1 failed, 2 cancelled.
#
# Environment (scripted installs and tests):  FRD_SERVICE_ACTION = install | uninstall   skips the questions and runs that action
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$action = $env:FRD_SERVICE_ACTION
$interactive = ($env:ONESETUP_SHELL -eq 'ui') -and [string]::IsNullOrEmpty($action)
$serviceName = 'FRDSecureCaptureProbe'
$installDirectory = Join-Path $env:ProgramFiles 'FRD'
$installedTool = Join-Path $installDirectory 'frd-wintools.exe'
$dataDirectory = Join-Path $env:ProgramData 'FRD\SecureDesktopProbe'

function Say([string]$line) {
    [Console]::Out.WriteLine($line); [Console]::Out.Flush()
    if ($env:FRD_SETUP_TRACE) { Add-Content -LiteralPath $env:FRD_SETUP_TRACE -Value $line -Encoding UTF8 }   # for tests: the installer window hides stdout
}
function Info([string]$text) { Say ".info $text" }
function Progress([int]$value) { Say ".progress $value" }
function Fail([string]$text, [int]$code = 1) { Say ".error $text"; exit $code }
function Ask([string]$question, [string]$yes = 'Yes', [string]$no = 'No') {
    Say ".ask $question | $yes | $no"
    return ([Console]::In.ReadLine() -eq 'yes')
}
function Sha([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

Say '.title FRD service helper / FRD 被控服务'
Progress 3

$version = (Get-Content -LiteralPath (Join-Path $here 'version.txt') -TotalCount 1).Trim()
$payloadTool = Join-Path $here 'frd-wintools.exe'
if (-not (Test-Path -LiteralPath $payloadTool)) { Fail 'The installer is missing frd-wintools.exe.' }
$expected = $null
foreach ($line in Get-Content -LiteralPath (Join-Path $here 'files.sha256')) {
    if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+)$' -and $Matches[2].Trim() -eq 'frd-wintools.exe') { $expected = $Matches[1].ToLowerInvariant() }
}
if (-not $expected -or (Sha $payloadTool) -ne $expected) { Fail 'frd-wintools.exe does not match its checksum; download the installer again. / 校验失败，请重新下载。' }
Progress 10

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$state = if ($service) { "installed, $($service.Status)" } else { 'not installed' }
Info "FRD $version service helper: $state / 状态: $(if ($service) { '已安装' } else { '未安装' })"

if ($interactive) {
    $now = if ($service) { 'installed' } else { 'not installed' }
    $question = "FRD service helper $version ($now). 1 = Install (overwrites an existing install), 2 = Uninstall. It runs as SYSTEM from $installDirectory and needs your approval in the Windows UAC prompt. / 1 安装（覆盖）  2 卸载（需在 UAC 提示中确认）"
    $action = if (Ask $question '1 Install (overwrite)' '2 Uninstall') { 'install' } else { 'uninstall' }
}
if ($action -notin @('install', 'uninstall')) { Fail "Unknown action '$action'." }
if ($action -eq 'uninstall' -and -not $service -and -not (Test-Path -LiteralPath $installDirectory)) { Info 'Nothing to remove.'; Progress 100; exit 0 }

if ($action -eq 'uninstall' -and (Get-Process -Name FRD -ErrorAction SilentlyContinue)) {
    Info 'FRD is running and keeps its frame buffer file open; close FRD first for a complete cleanup, otherwise that file is deleted at the next restart. / FRD 正在运行并占用一个文件；先关闭 FRD 可彻底清理，否则该文件在下次重启时删除。'
}

# ---- run the tool elevated ------------------------------------------------------------------------------------------------
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value   # the interactive user, who must be able to read the capture key
$arguments = if ($action -eq 'install') { @('--install-secure-helper', $sid) } else { @('--uninstall-secure-helper') }
Info $(if ($action -eq 'install') { 'Waiting for the UAC prompt... / 请在 UAC 提示中确认' } else { 'Waiting for the UAC prompt to remove the service... / 请在 UAC 提示中确认卸载' })
Progress 30
try { $process = Start-Process -FilePath $payloadTool -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -Wait -PassThru }
catch { Fail 'The UAC prompt was declined or could not be shown, so nothing was changed. / 未获得管理员授权，没有做任何改动。' 2 }
Progress 80
$exit = $process.ExitCode

# ---- check the result ----------------------------------------------------------------------------------------------------
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($action -eq 'install') {
    if ($exit -ne 0) {
        $detail = ''
        $errorFile = Join-Path $dataDirectory 'install-error.txt'
        if (Test-Path -LiteralPath $errorFile) { $detail = ' ' + ((Get-Content -LiteralPath $errorFile -TotalCount 1) -join '') }
        Fail "The installer exited with code $exit.$detail"
    }
    if (-not $service -or $service.Status -ne 'Running') { Fail "The service is not running after the install (status: $(if ($service) { $service.Status } else { 'missing' }))." }
    if (-not (Test-Path -LiteralPath $installedTool) -or (Sha $installedTool) -ne $expected) { Fail 'The installed frd-wintools.exe does not match this package.' }
    Progress 100
    Info "Installed and running: $installedTool / 已安装并运行。重新连接 FRD 即可使用安全桌面和管理员窗口控制。"
    exit 0
}
$leftovers = @($installDirectory, $dataDirectory) | Where-Object { Test-Path -LiteralPath $_ }
if ($service) { Fail "The service is still registered after the uninstall (exit code $exit); restart Windows and run the installer again." }
Progress 100
if ($leftovers) { Info "Service removed. Still in use and deleted at the next restart: $($leftovers -join ', ') / 服务已卸载，残留项将在下次重启时删除（或关闭 FRD 后手动删除）。" }
else { Info 'Service removed, files deleted. / 服务和文件已全部删除。' }
exit 0
