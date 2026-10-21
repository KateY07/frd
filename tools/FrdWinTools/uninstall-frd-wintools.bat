@echo off
setlocal
rem Removes the FRD secure-desktop helper service, its program folder and its key/frame files. Needs administrator rights.
rem Works from the unzipped package and from "%ProgramFiles%\FRD" (the exe is run from a temporary copy so its own folder can be deleted).
cd /d "%~dp0"
if not exist "frd-wintools.exe" (
  echo frd-wintools.exe was not found next to this script.
  pause
  exit /b 1
)
net session >nul 2>&1
if errorlevel 1 (
  echo Requesting administrator rights...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
set "TOOL=%TEMP%\frd-wintools-uninstall.exe"
copy /y "%~dp0frd-wintools.exe" "%TOOL%" >nul
if errorlevel 1 (
  echo Could not copy frd-wintools.exe to %TEMP%.
  pause
  exit /b 1
)
"%TOOL%" --uninstall-secure-helper
set "CODE=%ERRORLEVEL%"
del /f /q "%TOOL%" >nul 2>&1
if "%CODE%"=="0" (echo Uninstalled.) else (echo Finished with exit code %CODE%. If files remain, delete "%ProgramFiles%\FRD" and "%ProgramData%\FRD\SecureDesktopProbe" manually.)
pause
exit /b %CODE%
