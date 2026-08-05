@echo off
REM Single-process mode: build the frontend into Qa403.App/wwwroot, then run just the backend,
REM which serves the built UI and auto-opens your default browser.
setlocal

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
echo Starting QA403 Audio Analyzer...
dotnet run --project "%~dp0src\Qa403.App\Qa403.App.csproj"
