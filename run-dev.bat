@echo off
REM Dev mode: backend + frontend dev server in separate windows, with hot-reload on the frontend.
setlocal

REM The backend is started as the built exe, NOT with `dotnet run`. `dotnet run` is a parent process
REM that holds the app in a job object: when this console window is closed, the launcher's own close
REM handler returns at once, Windows terminates it, and the job kills the app within milliseconds --
REM before its graceful shutdown (USB teardown, SQLite checkpoint) has run. Ten days of logs showed
REM every window close ending that way, and qa403.db was found damaged twice. Run directly, the app
REM is the only process on this console and gets the ~5 s Windows allows its close handler.
echo Starting QA403 backend (http://127.0.0.1:8399)...
start "QA403 Backend" cmd /k "cd /d "%~dp0" && dotnet build src\Qa403.App\Qa403.App.csproj --nologo -v quiet && src\Qa403.App\bin\Debug\net10.0\Qa403.App.exe --no-browser"

echo Starting QA403 frontend dev server (http://localhost:5173)...
start "QA403 Frontend" cmd /k "cd /d "%~dp0web" && npm run dev"

echo.
echo Both servers are starting in separate windows.
echo Once ready, open http://localhost:5173 in your browser.
echo Close each window (or Ctrl+C inside it) to stop that server.
