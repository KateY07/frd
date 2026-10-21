@echo off
setlocal
rem Installs the FRD secure-desktop helper service (UAC prompt and lock screen capture/input). Needs administrator rights; the UAC prompt is yours to confirm.
cd /d "%~dp0"
if not exist "frd-wintools.exe" (
  echo frd-wintools.exe was not found next to this script.
  pause
  exit /b 1
)
net session >nul 2>&1
if errorlevel 1 (
  rem Remember who is asking so the elevated process can grant that user read access to the capture key.
  for /f "usebackq delims=" %%S in (`powershell -NoProfile -Command "[Security.Principal.WindowsIdentity]::GetCurrent().User.Value"`) do set "FRD_USER_SID=%%S"
  echo Requesting administrator rights...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%FRD_USER_SID%' -Verb RunAs"
  exit /b
)
if not "%~1"=="" (
  "%~dp0frd-wintools.exe" --install-secure-helper %~1
) else (
  "%~dp0frd-wintools.exe" --install-secure-helper
)
set "CODE=%ERRORLEVEL%"
if "%CODE%"=="0" (echo Installed. The helper runs from "%ProgramFiles%\FRD".) else (echo Install failed with exit code %CODE%. See "%ProgramData%\FRD\SecureDesktopProbe\install-error.txt".)
pause
exit /b %CODE%
