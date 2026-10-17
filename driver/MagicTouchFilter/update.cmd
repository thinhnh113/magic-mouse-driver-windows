@echo off
rem Installs the freshly built driver from out\ without needing a reboot.
rem Run from an ADMIN prompt. The app is closed first: while it holds the driver's control device open,
rem Windows can't unload the old driver and defers the update to the next reboot.
setlocal
set HERE=%~dp0
set APP=%HERE%..\..\src\MagicMouse.App\bin\x64\Release\net8.0-windows\MagicMouse.exe

net session >nul 2>&1 || (echo This script must run as Administrator. & exit /b 1)

echo Closing MagicMouse...
taskkill /im MagicMouse.exe /f >nul 2>&1
taskkill /im HidProbe.exe /f >nul 2>&1
timeout /t 1 /nobreak >nul

echo Installing driver...
pnputil /add-driver "%HERE%out\MagicTouchFilter.inf" /install || exit /b 1

echo Restarting Magic Mouse devices...
for /f "tokens=2,*" %%a in ('pnputil /enum-devices /connected /class HIDClass ^| findstr /i /c:"Instance ID:" ^| findstr /i "PID&0323 PID&0269 PID&030D PID&0310 PID_0323 PID_0269"') do (
  echo   %%b
  pnputil /restart-device "%%b" >nul
)

echo Starting MagicMouse...
start "" "%APP%" --minimized
echo Done. If pnputil reported "pending system reboot", reboot once to finish.
