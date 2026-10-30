@echo off
setlocal
cd /d "%~dp0"
echo.
echo   PCL 2.99.0.1 - Restore Download Feature
echo   --------------------------------------------------
echo   This tool patches "Plain Craft Launcher 2.exe" so that
echo   the vanilla Minecraft download page works again.
echo.
echo   Put this folder next to the PCL exe, then run this file.
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch_download.ps1" %*
set EXITCODE=%ERRORLEVEL%
exit /b %EXITCODE%
