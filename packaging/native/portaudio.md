# Rebuilding `native/win-x64/portaudio.dll`

PortAudio backs the sound-card analyzer (`src/Qa403.Device/SoundCard`). Unlike libusb, **upstream
ships source only** — there is no official Windows binary — so the vendored DLL is one we build.
This file is the recipe, so the binary in the tree is reproducible and the ASIO rebuild below is a
single documented step rather than folklore.

Linux does not use this: the .deb depends on the distro's `libportaudio2`, the same bundle-vs-depend
tradeoff as libusb (`docs/qa403-app-stack-spec.md` §9.4).

## What is in the vendored DLL

Built 2026-09-30 from PortAudio `19.8` (git `17967f3`), MSVC 14.51 (Visual Studio 2026 Community),
x64, Release — 302 080 bytes.

| Host API | In this build | Notes |
|---|---|---|
| WASAPI | yes | What the app asks for. Exclusive mode via `PaWasapi_*`, which this build exports. |
| WDM-KS | yes | Kernel streaming, the other route to an unmixed stream. |
| DirectSound | yes | Fallback only. |
| WMME | yes | Fallback only. |
| ASIO | **no** | Needs Steinberg's SDK — see below. |

Two properties worth re-checking after any rebuild, because both are easy to lose and neither shows
up until a user's machine:

- **x64**, and
- **no VC++ runtime dependency.** `PA_DLL_LINK_WITH_STATIC_RUNTIME` defaults ON, which rewrites
  `/MD` to `/MT`, so the DLL links the runtime statically and needs no redistributable installed.
  Verified with `dumpbin /dependents`: the only imports are `WINMM`, `ole32`, `SETUPAPI`,
  `KERNEL32`, `USER32` and `ADVAPI32` — all Windows' own.

```
dumpbin /headers    portaudio.dll | findstr machine      REM expect 8664 (x64)
dumpbin /dependents portaudio.dll                        REM expect no VCRUNTIME*/MSVCP*
dumpbin /exports    portaudio.dll | findstr Pa_Initialize
```

## The build

Visual Studio 2026 ships its own CMake and Ninja, which is the easy way in: the standalone CMake on
this machine (3.31.5) predates VS 2026 and has no generator for it, and the VS 2022 that is also
installed has no C++ workload.

**Build from a short path.** MSVC caps an object file's full path at 250 characters, and the
scratchpad directory alone is past that — the failure is a `C1041: cannot open program database`
from the compiler check, which reads like a broken toolchain rather than a path-length problem.
`C:\Users\joost\AppData\Local\Temp\pa` is short enough.

```bat
git clone -c core.longpaths=true --depth 1 https://github.com/PortAudio/portaudio.git ^
  C:\Users\joost\AppData\Local\Temp\pa\portaudio

set "SRC=C:\Users\joost\AppData\Local\Temp\pa\portaudio"
set "VSDIR=C:\Program Files\Microsoft Visual Studio\18\Community"
set "VSCMAKE=%VSDIR%\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
set "VSNINJA=%VSDIR%\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe"

call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat"

"%VSCMAKE%" -S "%SRC%" -B "%SRC%\build" -G Ninja ^
  -DCMAKE_BUILD_TYPE=Release ^
  -DCMAKE_MAKE_PROGRAM="%VSNINJA%" ^
  -DPA_BUILD_SHARED_LIBS=ON ^
  -DPA_USE_ASIO=OFF ^
  -DPA_USE_WASAPI=ON -DPA_USE_WDMKS=ON -DPA_USE_DS=ON -DPA_USE_WMME=ON ^
  -DPA_BUILD_TESTS=OFF -DPA_BUILD_EXAMPLES=OFF

"%VSCMAKE%" --build "%SRC%\build"
```

Then copy `%SRC%\build\portaudio.dll` over `code/native/win-x64/portaudio.dll`.

`git clone` needs `-c core.longpaths=true` only when cloning into a deep directory; it is harmless
otherwise.

## Adding ASIO

ASIO support is a property of **this DLL, not of the app**. The host-API list comes from
`Pa_GetHostApiCount` at runtime, so an ASIO-enabled rebuild dropped into `native/win-x64/` makes
ASIO devices appear in the picker with no code change anywhere.

To build one:

1. Download the ASIO SDK from Steinberg and accept its licence agreement. **The SDK is not in this
   repository and must not be committed** — its licence does not permit redistributing the sources.
   Distributing a compiled binary that uses it is a separate question, governed by that same
   agreement; read it before shipping an ASIO-enabled DLL to anyone else.
2. Extract it, and point CMake at it: add `-DPA_USE_ASIO=ON -DASIOSDK_PATH_HINT=<path to the SDK>`
   to the configure line above.
3. Rebuild and copy the DLL over as before, then re-run the `dumpbin` checks — an ASIO build pulls
   in C++ sources, so the static-runtime check is the one most worth repeating.

CI keeps building the app against whatever binary is vendored here; it never builds PortAudio
itself.

## Where this is wired in

- `src/Qa403.App/Qa403.App.csproj` copies it next to `libusb-1.0.dll`, on the same RID condition,
  and `IncludeNativeLibrariesForSelfExtract` absorbs it into the single-file publish.
- `packaging/windows/build.ps1` greps the published exe for `portaudio.dll` and fails the build if
  it is not in the bundle — the `--simulate` smoke test never loads it, so nothing else would catch
  a publish that lost it.
- `packaging/windows/qa403.iss` needs **no** entry: a native `.dll` ends up inside the exe.
- `packaging/linux/deb/control` and `packaging/linux/install.sh` name `libportaudio2` instead.
