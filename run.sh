#!/usr/bin/env bash
# Single-process mode: build the frontend into Tonebench.App/wwwroot, then run just the backend,
# which serves the built UI and auto-opens your default browser.
#
# The POSIX counterpart of run.bat, and it serves Linux as well as macOS -- neither had a launch
# script before. Run it from a terminal: a .sh is not double-clickable on macOS (that would need a
# .command file), so unlike run.bat there is no "pause" branch to keep a window open. The output
# stays where you started it.
set -euo pipefail

# Resolve our own directory, which is run.bat's %~dp0. Doing it this way survives being invoked
# through a relative path or from another working directory.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# One instance at a time. The app replaces an earlier instance when it starts (see
# Program.ReplaceRunningInstance), and nothing listening is the ordinary case and is silent.
#
# NOTE the reason differs from run.bat's, and the Windows comment would be wrong here. On Unix
# there is no file lock to trip over: `dotnet build` replaces the binary by unlink-and-create and a
# running process keeps its old inode happily. This POST is about the *old* instance getting its
# graceful USB teardown and SQLite checkpoint, instead of being killed by the new instance's
# fallback. `|| true` is mandatory under `set -e`.
if curl -s -m 2 -o /dev/null -X POST http://127.0.0.1:8399/api/app/shutdown 2>/dev/null; then
    echo "Stopping a running instance..."
fi

echo "Building frontend..."
if ! (cd "$SCRIPT_DIR/web" && npm run build); then
    echo "Frontend build failed." >&2
    exit 1
fi

echo
echo "Building backend..."
if ! dotnet build "$SCRIPT_DIR/src/Tonebench.App/Tonebench.App.csproj" --nologo -v quiet; then
    echo "Backend build failed." >&2
    exit 1
fi

# Homebrew's prefix is not on dyld's default search path on Apple Silicon (/opt/homebrew/lib
# against dyld's /usr/local/lib and /usr/lib), and libusb is loaded by LibUsbDotNet's own
# DllImportResolver -- which already knows the macOS file name, libusb-1.0.0.dylib, but can only
# look where the loader looks. SetDllImportResolver allows one resolver per assembly and that slot
# is theirs, so this variable is the mechanism rather than a convenience. PortAudio needs none of
# this: its resolver is ours and carries the absolute paths itself.
#
# Export it in your shell profile too if you run `dotnet test` or launch the binary by hand, since
# those never go through this script. SIP strips DYLD_* only for hardened or system binaries, which
# a from-source build is not.
if [ "$(uname -s)" = "Darwin" ]; then
    for prefix in /opt/homebrew/lib /opt/local/lib; do
        [ -d "$prefix" ] || continue
        export DYLD_FALLBACK_LIBRARY_PATH="${DYLD_FALLBACK_LIBRARY_PATH:+$DYLD_FALLBACK_LIBRARY_PATH:}$prefix"
    done
fi

echo
echo "Starting Tonebench..."
# `exec`, and that is load-bearing rather than tidiness. It replaces this shell, so the app becomes
# the terminal's direct child and is the process that receives SIGHUP when the window is closed --
# which is what lets RegisterConsoleCloseShutdown run the USB teardown and the SQLite checkpoint.
# Without it bash sits between the terminal and the app, which is the same shape as the `dotnet run`
# prohibition in CLAUDE.md and has the same consequence: qa403.db was found damaged twice.
exec "$SCRIPT_DIR/src/Tonebench.App/bin/Debug/net10.0/Tonebench.App" "$@"
