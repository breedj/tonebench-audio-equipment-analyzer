# Rebuilding `native/win-x64/portaudio.dll`

PortAudio backs the sound-card analyzer (`src/Tonebench.Device/SoundCard`). Unlike libusb, **upstream
ships source only** — there is no official Windows binary — so the vendored DLL is one we build,
**with ASIO, and with one local patch**. This file is the recipe, so the binary in the tree is
reproducible rather than folklore.

Linux does not use this: the .deb depends on the distro's `libportaudio2`, the same bundle-vs-depend
tradeoff as libusb (`docs/tonebench-stack-spec.md` §9.4). ALSA and JACK do real full-duplex, so
none of the Windows difficulties below apply there.

## What is in the vendored DLL

Built 2026-09-30 from PortAudio `19.8` (git `17967f3`) plus `apply-asio-guard.py`, against ASIO SDK
2.3.4, MSVC 14.51 (Visual Studio 2026 Community), x64, Release.

| Host API | Built in | What it is worth on Windows |
|---|---|---|
| **ASIO** | **yes** | The only one that presents a card as a single **full-duplex** device, and therefore the only one this app can actually measure through. |
| WASAPI | yes | Exclusive mode only. Shared is pinned to the Windows mixer's rate and resamples. |
| WDM-KS | yes | Unmixed, but refused a duplex open on the bench hardware. |
| DirectSound | yes | Resamples in software; accepts any rate. Untrustworthy for a measurement. |
| WMME | yes | As DirectSound. |

**Why ASIO is not optional.** Measured on a MOTU M4, 2026-09-30: of 57 non-ASIO devices enumerated,
**not one carries both directions** — the card appears as separate `In 1-2 (MOTU M Series)` and
`Out 1-2 (MOTU M Series)` on every host API. A WASAPI-exclusive duplex stream across those two
endpoints opens and accepts writes but **never delivers input** (`Pa_GetStreamReadAvailable` frozen
at 240 frames), and WDM-KS refuses to open one at all. With ASIO the same card is one device,
8 in / 4 out, accepting 48/96/192 kHz, and a duplex stream reports 43 ms in / 69 ms out — against
the 2730 ms WASAPI exclusive allocates.

Three properties worth re-checking after any rebuild, because none shows up until a user's machine:

- **x64**;
- **no VC++ runtime dependency** — `PA_DLL_LINK_WITH_STATIC_RUNTIME` defaults ON, rewriting `/MD` to
  `/MT`, so no redistributable is needed. Worth re-checking specifically after an ASIO build, which
  pulls in C++ sources;
- **`PaAsio_*` exported**, which is the quickest proof ASIO really went in.

```
dumpbin /headers    portaudio.dll | findstr machine      REM expect 8664 (x64)
dumpbin /dependents portaudio.dll                        REM expect no VCRUNTIME*/MSVCP*
dumpbin /exports    portaudio.dll | findstr PaAsio_
```

## The local patch, and why it exists

`Pa_Initialize` calls **`ASIOInit` on every registered ASIO driver**, because PortAudio's API has to
report every device up front. A driver that faults there takes the whole host process down, and
every driver listed behind it becomes unreachable. ARTA and similar hosts dodge this by initialising
only the driver the user picks; PortAudio cannot.

Measured on this bench, where four ASIO drivers are registered:

| Driver | `IASIO::init()` |
|---|---|
| Creative Sound Blaster ASIO | **access violation** |
| Focusrite Thunderbolt ASIO | returns 0 — declines cleanly, hardware absent |
| Focusrite USB ASIO | returns 0 — declines cleanly |
| MOTU M Series | returns 1 — succeeds |

So a single third-party driver made the analyzer unusable, and the MOTU sitting after it in the list
was never reached. `apply-asio-guard.py` wraps the per-driver `ASIOInit` and `ASIOGetChannels` in
structured exception handling, so a faulting driver is reported absent exactly as a polite one that
returns an error would be. It is idempotent, and it must be re-applied after every fresh checkout.

Two notes. The SEH helpers are leaf functions because MSVC forbids `__try` in a function that also
needs C++ unwinding. And this is **not** the null-`sysRef` bug it first looks like: PortAudio's
`#ifdef WINDOWS` guard around `systemSpecific = GetDesktopWindow()` is never compiled by the CMake
build, so it passes a null window handle — but defining `WINDOWS` and passing a real one does not
stop the crash, so the guard is still needed. The define is kept anyway, because passing the desktop
window is plainly what that code intends.

## The build

Visual Studio 2026 ships its own CMake and Ninja, which is the way in: the standalone CMake on this
machine (3.31.5) predates VS 2026 and has no generator for it, and the VS 2022 also installed has no
C++ workload.

**Build from a short path.** MSVC caps an object file's full path at 250 characters, and a
scratchpad directory alone can exceed that. The failure is a `C1041: cannot open program database`
from the compiler check, which reads like a broken toolchain rather than a path-length problem.

**The ASIO SDK is not in this repository and must not be committed** — its licence does not permit
redistributing the sources. Download it from Steinberg, accept the licence, and extract it so that a
directory matching `asiosdk*` sits directly under whatever you pass as `CMAKE_PREFIX_PATH`;
`FindASIO.cmake` globs for `asiosdk*/common/asio.h` and will not find a differently-named folder.

```bat
set "SRC=C:\Temp\pa\portaudio"
set "SDKROOT=C:/Temp/pa/sdkroot"          REM must contain asiosdk\common\asio.h
set "VSDIR=C:\Program Files\Microsoft Visual Studio\18\Community"
set "VSCMAKE=%VSDIR%\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
set "VSNINJA=%VSDIR%\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe"

git clone -c core.longpaths=true --depth 1 https://github.com/PortAudio/portaudio.git "%SRC%"
python packaging\native\apply-asio-guard.py "%SRC%\src\hostapi\asio\pa_asio.cpp"

call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat"

"%VSCMAKE%" -S "%SRC%" -B "%SRC%\build-asio" -G Ninja ^
  -DCMAKE_BUILD_TYPE=Release ^
  -DCMAKE_MAKE_PROGRAM="%VSNINJA%" ^
  -DCMAKE_PREFIX_PATH="%SDKROOT%" ^
  -DCMAKE_C_FLAGS=/DWINDOWS -DCMAKE_CXX_FLAGS=/DWINDOWS ^
  -DPA_BUILD_SHARED_LIBS=ON ^
  -DPA_USE_ASIO=ON ^
  -DPA_USE_WASAPI=ON -DPA_USE_WDMKS=ON -DPA_USE_DS=ON -DPA_USE_WMME=ON ^
  -DPA_BUILD_TESTS=OFF -DPA_BUILD_EXAMPLES=OFF

"%VSCMAKE%" --build "%SRC%\build-asio"
```

Then copy `%SRC%\build-asio\portaudio.dll` over `code/native/win-x64/portaudio.dll` and run the
hardware tests (`dotnet test --filter "Category=Hardware"`), which enumerate through the real
library and open a duplex stream.

CI keeps building the app against whatever binary is vendored here; it never builds PortAudio.

## Calling it: the STA rule

ASIO is COM, and **every PortAudio call must be made from a single-threaded apartment**. From an
ordinary .NET thread — MTA by default — `Pa_Initialize` reports the ASIO host API with *zero*
devices although four drivers are registered, and a second `Pa_Initialize` after a `Pa_Terminate`
faults. `StaDispatcher` is what guarantees the apartment; `PortAudioNative.Shared` is what
guarantees the library is initialised exactly once for the process, since re-initialising crashes
regardless of apartment.

## Where this is wired in

- `src/Tonebench.App/Tonebench.App.csproj` copies it next to `libusb-1.0.dll`, on the same RID condition,
  and `IncludeNativeLibrariesForSelfExtract` absorbs it into the single-file publish.
- `src/Tonebench.Device.Tests/Tonebench.Device.Tests.csproj` copies it for the hardware tests.
- `packaging/windows/build.ps1` greps the published exe for `portaudio.dll` and fails the build if
  it is missing — the `--simulate` smoke test never loads it, so nothing else would catch it.
- `packaging/windows/tonebench.iss` needs **no** entry: a native `.dll` ends up inside the exe.
- `packaging/linux/deb/control` and `packaging/linux/install.sh` name `libportaudio2` instead.
