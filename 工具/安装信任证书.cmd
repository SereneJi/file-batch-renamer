@echo off
rem Install the local self-signed certificate into CurrentUser Root + TrustedPublisher.
setlocal
cd /d "%~dp0.."
set "PS=pwsh"
where pwsh >nul 2>nul || set "PS=powershell"
echo.
echo === Install trust certificate (CurrentUser only) ===
echo.
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "build\install-trust.ps1" %*
echo.
pause
