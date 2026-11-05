@echo off
REM Put the virtual QA402 in the Windows device tree, so Tonebench opens it as real hardware.
REM A thin wrapper over bench.ps1 -Attach; README.md in this directory says what the bench is, what
REM it can establish and what it cannot.
REM
REM QA402 is the default and needs no argument. That is deliberate: Qa40xTransport.Open probes
REM Qa40xModel.All with the QA403 first, so a virtual QA403 would be both indistinguishable from a
REM real one and unreachable whenever one is plugged in.
setlocal

REM First time on a machine, or after moving to a new one, build the patched emulator once:
REM     testbench\virtual-qa40x\bench.ps1 -Build
REM The prebuilt release binaries are no use -- they carry the crossed-channel bug.

REM -ExecutionPolicy Bypass because these scripts are unsigned, and a desktop Windows default of
REM RemoteSigned refuses a script that carries the downloaded-from-the-internet mark. -NoProfile so a
REM personal $PROFILE cannot change what this does on one machine and not another.
REM %* is forwarded, so `attach.bat -Serial 1234_5678` works.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0bench.ps1" -Attach %*

REM The failure branch pauses, for the reason run.bat gives: started by double-clicking, the console
REM window closes the moment the batch exits and takes the message with it. That matters more here
REM than anywhere else, because those messages are the whole user interface of this thing -- a real
REM QA403 being attached and winning the probe, no patched build yet, usbip-win2 not installed. Run
REM from a terminal the text stays anyway, and the pause costs one keypress.
if errorlevel 1 (
    echo.
    echo Attach failed -- see the message above, or testbench\virtual-qa40x\README.md.
    pause
    exit /b 1
)

REM No pause on success: the three status lines above are the confirmation, and they stay on screen
REM when this is run from a terminal. Double-clicked they flash past, so check with:
REM     testbench\virtual-qa40x\bench.ps1 -Status
exit /b 0
