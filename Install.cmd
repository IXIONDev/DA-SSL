@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Installer\scripts\Install.ps1"
if errorlevel 1 (
  echo.
  echo Installation did not finish. See the message above.
)
echo.
pause
