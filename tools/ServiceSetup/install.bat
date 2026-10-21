@echo off
rem Entry point run by OneSetup (the installer extracts this folder and runs this file). Keep this file ASCII.
rem All the work, and all the localised text, is in install.ps1, which speaks the OneSetup window protocol on stdout.
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
exit /b %ERRORLEVEL%
