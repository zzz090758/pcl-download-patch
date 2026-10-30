@echo off
setlocal
cd /d "%~dp0"
echo.
echo   Restore the original (unpatched) Plain Craft Launcher 2.exe
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch_download.ps1" -Restore
exit /b %ERRORLEVEL%
