@echo off
REM Dev mode: backend + frontend dev server in separate windows, with hot-reload on the frontend.
setlocal

echo Starting QA403 backend (http://127.0.0.1:8399)...
start "QA403 Backend" cmd /k "cd /d "%~dp0" && dotnet run --project src\Qa403.App\Qa403.App.csproj -- --no-browser"

echo Starting QA403 frontend dev server (http://localhost:5173)...
start "QA403 Frontend" cmd /k "cd /d "%~dp0web" && npm run dev"

echo.
echo Both servers are starting in separate windows.
echo Once ready, open http://localhost:5173 in your browser.
echo Close each window (or Ctrl+C inside it) to stop that server.
