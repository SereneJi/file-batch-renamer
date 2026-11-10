@echo off
rem Remove the "downloaded from the Internet" mark (Zone.Identifier) from all files.
setlocal
cd /d "%~dp0.."
set "PS=pwsh"
where pwsh >nul 2>nul || set "PS=powershell"
echo.
echo === Unblock files (remove Zone.Identifier) ===
echo.
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "build\unblock-files.ps1" %*
echo.
pause
