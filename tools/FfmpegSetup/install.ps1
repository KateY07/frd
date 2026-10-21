# Installs the FFmpeg libraries next to FRD.exe (folder "ffmpeg"). Runs inside a OneSetup installer window:
# lines starting with "." drive the window (.title .info .warn .error .progress .ask .choose), answers arrive on stdin.
# Exit codes: 0 installed, 1 failed, 2 cancelled.
#
# Environment (for scripted installs and tests):  FRD_DIR  folder that contains FRD.exe (skips the search and the questions)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
# Questions are only asked inside the installer window. Without it (headless, -y, scripts) nothing is guessed: FRD_DIR must name the target,
# so an unattended run can never write into some other program's copy of FRD.
$interactive = [string]::IsNullOrEmpty($env:FRD_DIR) -and ($env:ONESETUP_SHELL -eq 'ui')

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
function Choose([string]$prompt, [string[]]$options) {
    Say (".choose $prompt; " + ($options -join '; '))
    $answer = [Console]::In.ReadLine()
    if ($answer -match '^\d+$') { return [int]$answer }
    return 0
}

Say '.title FFmpeg extension for FRD / FRD 的 FFmpeg 扩展'
Progress 3

# ---- where is FRD? -------------------------------------------------------------------------------------------------
$candidates = New-Object 'System.Collections.Generic.List[string]'
function Add-Candidate([string]$directory) {
    if ([string]::IsNullOrWhiteSpace($directory)) { return }
    if (-not (Test-Path -LiteralPath (Join-Path $directory 'FRD.exe'))) { return }
    $full = (Resolve-Path -LiteralPath $directory).Path
    if (-not $candidates.Contains($full)) { $candidates.Add($full) }
}

if ($env:FRD_DIR) {
    if (-not (Test-Path -LiteralPath (Join-Path $env:FRD_DIR 'FRD.exe'))) { Fail "FRD_DIR does not contain FRD.exe: $env:FRD_DIR" }
    Add-Candidate $env:FRD_DIR
}
elseif (-not $interactive) {
    Fail 'Unattended install: set FRD_DIR to the folder that contains FRD.exe. / 无人值守安装需要设置 FRD_DIR 为 FRD.exe 所在目录。'
}
else {
    Info 'Looking for FRD.exe / 正在查找 FRD.exe'
    # 1) the folder the setup program itself was started from: the first ancestor process that is not a shell and not in %TEMP% or Windows
    try {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$PID"
        for ($i = 0; $i -lt 8 -and $process; $i++) {
            $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($process.ParentProcessId)"
            $path = $process.ExecutablePath
            if ($path -and $path -notmatch '\\(cmd|powershell|conhost)\.exe$' -and -not $path.StartsWith($env:TEMP, [StringComparison]::OrdinalIgnoreCase) -and -not $path.StartsWith($env:windir, [StringComparison]::OrdinalIgnoreCase)) {
                Add-Candidate (Split-Path -Parent $path); break
            }
        }
    }
    catch { }
    # 2) a running FRD
    Get-Process -Name FRD -ErrorAction SilentlyContinue | ForEach-Object { try { Add-Candidate (Split-Path -Parent $_.Path) } catch { } }
    # 3) the usual per-user install folder, newest version first
    $programs = Join-Path $env:LOCALAPPDATA 'Programs\FRD'
    if (Test-Path -LiteralPath $programs) {
        Get-ChildItem -LiteralPath $programs -Directory | Sort-Object LastWriteTime -Descending | ForEach-Object { Add-Candidate $_.FullName }
    }
}
Progress 12

$target = $null
if ($candidates.Count -eq 1) {
    $target = $candidates[0]
    if ($interactive -and -not (Ask "Install the FFmpeg libraries next to FRD in $target ?" 'Install' 'Cancel')) { Fail 'Cancelled.' 2 }
}
elseif ($candidates.Count -gt 1) {
    $shown = @($candidates | Select-Object -First 4) + 'Another folder... / 其他目录...'
    $pick = Choose 'Which FRD should get the FFmpeg libraries? / 装到哪个 FRD？' $shown
    if ($pick -eq 0) { Fail 'Cancelled.' 2 }
    if ($pick -le [Math]::Min(4, $candidates.Count)) { $target = $candidates[$pick - 1] }
}
if (-not $target) {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = 'Select the folder that contains FRD.exe / 选择包含 FRD.exe 的文件夹'
    if ($dialog.ShowDialog() -ne 'OK') { Fail 'Cancelled.' 2 }
    if (-not (Test-Path -LiteralPath (Join-Path $dialog.SelectedPath 'FRD.exe'))) { Fail "FRD.exe is not in $($dialog.SelectedPath) / 所选目录里没有 FRD.exe" }
    $target = $dialog.SelectedPath
}
Info "FRD folder: $target"
Progress 25

# ---- copy -----------------------------------------------------------------------------------------------------------
$source = Join-Path $here 'ffmpeg'
$destination = Join-Path $target 'ffmpeg'
if ((Test-Path -LiteralPath (Join-Path $destination 'avcodec-62.dll')) -and $interactive) {
    $current = (Get-Item -LiteralPath (Join-Path $destination 'avcodec-62.dll')).VersionInfo.FileVersion
    if (-not (Ask "FFmpeg libraries are already installed (avcodec $current). Replace them?" 'Replace' 'Cancel')) { Fail 'Cancelled.' 2 }
}
Info 'Copying FFmpeg libraries / 正在复制 FFmpeg 库'
try {
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    $files = Get-ChildItem -LiteralPath $source -File
    $index = 0
    foreach ($file in $files) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination $file.Name) -Force
        $index++
        Progress (25 + [int](60 * $index / $files.Count))
    }
}
catch {
    Fail "Could not write to $destination ($($_.Exception.Message)). Close FRD and try again. / 无法写入，请先关闭 FRD 再试。"
}

# ---- verify ---------------------------------------------------------------------------------------------------------
Info 'Verifying the copy / 正在校验'
$expected = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $here 'files.sha256')) {
    if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+)$') { $expected[$Matches[2].Trim()] = $Matches[1].ToLowerInvariant() }
}
foreach ($name in $expected.Keys) {
    $path = Join-Path $destination $name
    if (-not (Test-Path -LiteralPath $path)) { Fail "Missing after copy: $name" }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected[$name]) { Fail "Checksum mismatch after copy: $name" }
}
Progress 97
$version = (Get-Item -LiteralPath (Join-Path $destination 'avcodec-62.dll')).VersionInfo.FileVersion
Info "Installed FFmpeg libraries ($($expected.Count) files, avcodec $version) in $destination"
Info 'Restart FRD to use the FFmpeg presets. / 重启 FRD 即可使用 FFmpeg 预设。'
Progress 100
exit 0
