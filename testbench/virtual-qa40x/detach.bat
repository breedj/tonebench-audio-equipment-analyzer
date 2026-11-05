@echo off
REM Take the virtual QA402 back out of the Windows device tree and stop the emulator in WSL.
REM A thin wrapper over bench.ps1 -Detach. Safe to run when nothing is attached: it detaches whatever
REM usbip ports exist and says so if there was no server running.
REM
REM Worth running rather than just leaving it: a bench left attached and idle comes back with its
REM register reply FIFO desynced, and the next run then fails every test while usbip port and the
REM device tree both still look healthy. See "Things that will bite" in README.md.
setlocal

REM -ExecutionPolicy Bypass and -NoProfile for the reasons attach.bat gives.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0bench.ps1" -Detach %*

REM Pauses on failure only, as attach.bat and run.bat do: double-clicked, the window would otherwise
REM close on the message naming what went wrong. The likely cause here is WSL not being reachable.
if errorlevel 1 (
    echo.
    echo Detach failed -- see the message above, or testbench\virtual-qa40x\README.md.
    pause
    exit /b 1
)

exit /b 0
