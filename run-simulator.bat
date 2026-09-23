@echo off
REM Single-process mode: build the frontend into Qa403.App/wwwroot, then run just the backend
REM against the simulated device, which serves the built UI and auto-opens your default browser.
setlocal

REM One instance at a time. The app itself replaces an earlier instance when it starts (see
REM Program.ReplaceRunningInstance), but the build below cannot overwrite an exe that is still
REM running -- so a running instance is asked to shut down first, gracefully, over its own API.
REM Nothing listening is the ordinary case and is silent.
curl -s -m 2 -o nul -X POST http://127.0.0.1:8399/api/app/shutdown 2>nul && echo Stopping a running instance...

echo Building frontend...
pushd "%~dp0web"
call npm run build
if errorlevel 1 (
    echo Frontend build failed.
    popd
    exit /b 1
)
popd

echo.
echo Building backend...
dotnet build "%~dp0src\Qa403.App\Qa403.App.csproj" --nologo -v quiet
if errorlevel 1 (
    echo Backend build failed.
    exit /b 1
)

echo.
echo Starting QA403 Audio Analyzer (simulator)...
REM The backend is started as the built exe, NOT with `dotnet run`. `dotnet run` is a parent process
REM that holds the app in a job object: when this console window is closed, the launcher's own close
REM handler returns at once, Windows terminates it, and the job kills the app within milliseconds --
REM before its graceful shutdown (USB teardown, SQLite checkpoint) has run. Ten days of logs showed
REM every window close ending that way, and qa403.db was found damaged twice. Run directly, the app
REM is the only process on this console and gets the ~5 s Windows allows its close handler.
"%~dp0src\Qa403.App\bin\Debug\net10.0\Qa403.App.exe" --simulate
