@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Installer\scripts\Uninstall.ps1"
if errorlevel 1 (
  echo.
  echo Uninstall did not finish. See the message above.
)
echo.
pause
