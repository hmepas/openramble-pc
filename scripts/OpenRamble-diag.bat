@echo off
rem Restart the installed OpenRamble with the diagnostic log written next to this file.
rem
rem Meant to sit beside OpenRamble-win-Setup.exe on a share: install, close the
rem setup window, double-click this. The log lands in the same folder as the .bat,
rem so it can be read from another machine while the app is still running.
rem
rem %~dp0 is the folder this .bat lives in - a UNC path when run from a share, which
rem is fine as an argument even though cmd cannot cd into it.
setlocal

set "LOG=%~dp0openramble-%COMPUTERNAME%.log"
set "APP=%LocalAppData%\OpenRamble\current\OpenRamble.App.exe"

if not exist "%APP%" (
    echo OpenRamble is not installed for this user: "%APP%" not found.
    echo Run OpenRamble-win-Setup.exe first.
    pause
    exit /b 1
)

rem The running copy holds the single-instance lock; a second copy would exit at once.
tasklist /FI "IMAGENAME eq OpenRamble.App.exe" 2>nul | find /I "OpenRamble.App.exe" >nul
if not errorlevel 1 (
    echo Stopping the running OpenRamble...
    taskkill /IM OpenRamble.App.exe /F >nul 2>&1
    timeout /t 2 /nobreak >nul
)

echo Starting OpenRamble with diagnostics.
echo Log: "%LOG%"
start "" "%APP%" --diag "%LOG%"

echo.
echo Reproduce the problem, then send the log file above.
echo (This window can be closed; the app keeps running from the tray.)
timeout /t 10
endlocal
