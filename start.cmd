@echo off
rem ---------------------------------------------------------------------------
rem  Monitor Source Switcher - launcher
rem
rem  Double-click this, or the desktop shortcut to it.
rem
rem  Pressing it twice is harmless: if the app is already listening it just
rem  reopens the web UI instead of failing on a busy port.
rem
rem  The app runs in THIS window, so its log is visible here and closing the
rem  window is how you stop it. It opens the web UI itself once it is really
rem  listening, rather than guessing with a sleep.
rem ---------------------------------------------------------------------------
setlocal
set "PORT=8152"
cd /d "%~dp0"

set "DLL=bin\Release\net9.0\MonitorSourceSwitcher.dll"

rem ---------------------------------------------------------------------------
rem  Helper machine (optional).
rem
rem  A second computer sitting on a monitor input this PC cannot reach over
rem  DDC/CI. With this set, the app switches that monitor by ssh-ing here and
rem  running m1ddc. Leave it empty and the app still runs - it will just report
rem  that monitor as unmovable, which is the honest state of the hardware.
rem
rem  Set it to your own machine if you fork this.
rem ---------------------------------------------------------------------------
set "MAC=melsfeir@192.168.1.204"
set "MAC_ARGS="
if not "%MAC%"=="" set "MAC_ARGS=--mac %MAC%"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo .NET is not on PATH. Install the .NET 9 runtime or SDK from
    echo    https://dotnet.microsoft.com/download
    echo.
    pause
    goto done
)

rem Already listening? Then just open the UI and get out of the way.
netstat -ano | findstr /c:":%PORT%" | findstr /c:"LISTENING" >nul 2>&1
if not errorlevel 1 (
    echo Monitor Source Switcher is already running on port %PORT% - opening the UI.
    start "" "http://127.0.0.1:%PORT%/"
    goto done
)

echo Starting Monitor Source Switcher on port %PORT%
if not "%MAC%"=="" echo Switching unreachable monitors via %MAC%
echo Close this window to stop it.
echo.

if exist "%DLL%" (
    dotnet "%DLL%" --port %PORT% --open %MAC_ARGS%
) else (
    rem No build yet: let the SDK build and run it. Slower on first launch.
    echo No build found - building with the .NET SDK first.
    echo.
    dotnet run -- --port %PORT% --open %MAC_ARGS%
)

:done
endlocal
