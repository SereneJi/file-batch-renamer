@echo off
rem Remove the local self-signed certificate from CurrentUser Root + TrustedPublisher.
setlocal
cd /d "%~dp0.."
set "PS=pwsh"
where pwsh >nul 2>nul || set "PS=powershell"
echo.
echo === Remove trust certificate ===
echo.
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "build\uninstall-trust.ps1" %*
echo.
pause
