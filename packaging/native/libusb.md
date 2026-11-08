# Choosing `native/win-x64/libusb-1.0.dll`

LibUsbDotNet 3.0.224 (`src/Tonebench.Device`) wraps native libusb-1.0, which the NuGet package does
**not** bundle, so the DLL is vendored. Unlike PortAudio, upstream ships ready-made Windows binaries
— several of them, and **which one is not a free choice**. This file is why, because the wrong choice
fails on someone else's machine and on nobody's here.

Linux does not use this: the .deb depends on the distro's `libusb-1.0-0`, the same bundle-vs-depend
tradeoff as PortAudio (`docs/tonebench-stack-spec.md` §9.4).

## What is vendored

Upstream **libusb 1.0.30**, the **`MinGW64/dll`** build, taken unmodified from the official
`libusb-1.0.30.7z` binary archive.

| | |
|---|---|
| size | 243 444 bytes |
| md5 | `2e920a9e5d411a14ae714e088b108348` |
| vendored | 2026-11-07, replacing `VS2022/MS64/dll` (`fc55db1ad8c0eee7f99821be50262546`) |

## The rule: the MinGW build, never an MS one

Every `VS20xx/MS64/dll` build links the CRT **dynamically** (`/MD` — upstream's `README.txt` says
so), so it imports **`VCRUNTIME140.dll`**. That is the Visual C++ 2015–2022 redistributable. It is
**not part of Windows**, and `packaging/windows/tonebench.iss` is `PrivilegesRequired=lowest` by
design, so this installer cannot install one — nor should it have to, `INSTALL.md` promising that no
redistributable is needed. The MinGW build needs only `msvcrt.dll`, which is in-box on every
Windows.

```
                                  vendored (MinGW64)   rejected (VS2022/MS64)
KERNEL32.dll                             yes                   yes
msvcrt.dll                               yes                    -
USER32.dll                               yes                   yes
VCRUNTIME140.dll                          -                  >>> yes <<<
api-ms-win-crt-*-l1-1-0.dll               -                    yes   (in-box since Win10)
```

`packaging/windows/build.ps1` fails the build if a vendored DLL names `VCRUNTIME` or `MSVCP`, so
this cannot quietly come back on the next libusb update. It scans the import names rather than
running `dumpbin`, which is not on every runner. Verified in both directions: it accepts the two
DLLs in the tree and rejects the VS2022 and VS2025 builds.

## What it cost before the rule existed

Reported 2026-11-05 by a QA402 user on Windows 10 1903 (`10.0.18362`, 4 cores, 4 GB), against 0.5.0
and then 0.6.0. The analyzer could never attach; the log said, every time:

```
[WRN] Could not connect to the QA403 at startup (Dll was not found.)
System.DllNotFoundException: Dll was not found.
   at LibUsbDotNet.NativeMethods.Init(IntPtr& ctx)
   at LibUsbDotNet.LibUsb.UsbContext..ctor()
```

**Nothing about that message points at the cause, and three things about it actively mislead.** The
DLL was present and correct: it travels *inside* the single-file exe and is self-extracted at
startup to `%TEMP%\.net\Tonebench.App\<bundle-id>\`, so it was sitting on disk next to an
`e_sqlite3.dll` that had loaded **35 ms earlier** — the configuration database's `quick_check` line
immediately above it in the same log is the proof that extraction worked. Windows reports a missing
*dependency* with the same status code as a missing *file*, and .NET renders that as the sentence
above. And because the installer ships no loose copy, the obvious check — look in
`%LOCALAPPDATA%\Programs\Tonebench` — finds no `libusb-1.0.dll` there and appears to confirm the
wrong diagnosis.

So: if this ever recurs, the question is not "is the file there" but "what does the file need".

## Checking a new libusb before vendoring it

The public ABI is what matters, and the two builds' export tables are **not** textually identical,
which is expected and harmless:

- both export the same **104 undecorated `libusb_*` functions** — this is the part that must match;
- the MS builds *additionally* export 103 `libusb_xxx@N` stdcall-decorated aliases, a legacy
  artefact of `libusb-1.0.def`. Nothing here wants them: x64 has one calling convention, and .NET's
  `DllImport` resolves the plain name;
- the MinGW build *additionally* re-exports ~59 internal symbols (`usbi_*`, `windows_*`, the
  `GUID_*` device interfaces) because of `--export-all-symbols`. Noise, not a difference in
  behaviour.

Upstream's `README.txt` states the MinGW and MS DLLs are "fully interchangeable, provided that you
use the import libs provided or generate one from the .def" — and that proviso does not even apply
here, since LibUsbDotNet resolves `libusb-1.0` by name at runtime and no import library is ever
linked.

```
dumpbin /headers    libusb-1.0.dll | findstr machine     REM expect 8664 (x64)
dumpbin /dependents libusb-1.0.dll                       REM expect no VCRUNTIME*/MSVCP*
dumpbin /exports    libusb-1.0.dll | findstr libusb_init
```

Note `UsbDkHelper.dll` appears in a *strings* scan of the DLL but is not a static import:
per upstream, the UsbDk backend is "a run-time choice rather than a compile-time choice", so it is
`LoadLibrary`d only if used. Do not read it as a shipped dependency.

Then load it, which is the one check that exercises exactly the call that failed above — `dumpbin`
reads a table, this runs the code:

```powershell
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class Lu {
  [DllImport(@"<abs path>\libusb-1.0.dll", CallingConvention=CallingConvention.Cdecl)]
  public static extern int libusb_init(out IntPtr ctx);
  [DllImport(@"<abs path>\libusb-1.0.dll", CallingConvention=CallingConvention.Cdecl)]
  public static extern void libusb_exit(IntPtr ctx);
}
"@
$c = [IntPtr]::Zero; [Lu]::libusb_init([ref]$c); [Lu]::libusb_exit($c)   # expect 0
```

The vendored DLL answers `0`, reports version 1.0.30 through `libusb_get_version`, enumerated 15
devices on the development machine and exited cleanly (2026-11-07).

**None of the above substitutes for a bench run.** This library is the USB transport under every
hardware measurement, and the failure modes this codebase has learned the hard way — an interface
left claimed, `libusb_close` with transfers still in flight, a device that then needs a power cycle
— all live in it. After any change here, run `dotnet test --filter "Category=Hardware"` against a
real QA403, and watch a streaming acquisition start and tear down cleanly.

## If MinGW ever stops being an option

Build libusb from source with `/MT`, the way `portaudio.md` does it — upstream's `VS2022/MS64/static`
holds only `libusb-1.0.lib`, a static library, so it is not a drop-in for a P/Invoke that needs a
DLL.

## Where this is wired in

- `src/Tonebench.App/Tonebench.App.csproj` — the `None` item that copies it next to the exe, on a
  condition that deliberately also covers a RID-less `dotnet build` (i.e. every dev run).
- `packaging/windows/build.ps1` — the VC++ runtime check above, plus the existing check that the
  single-file exe really absorbed the DLL (a `--simulate` smoke test never loads libusb, so a
  publish that lost it would otherwise pass).
- `packaging/udev/99-qa403.rules` — the Linux counterpart, one rule per `Qa40xModel.All` entry.

## macOS

Nothing is vendored, as on Linux: `brew install libusb` and depend on it. Two things make it
different from the Linux case, and both are why `run.sh` exists.

**The file name is already handled, and not by us.** LibUsbDotNet 3.0.224 registers its *own*
`DllImportResolver` and its candidate list covers all three platforms — `libusb-1.0.dll`,
`libusb-1.0.so.0`, `libusb-1.0.0.dylib`. (That is also the answer to a question this file never
addressed: the `.deb` depends on `libusb-1.0-0`, which ships only the versioned `.so.0`, and the
runtime's own probing for a bare `libusb-1.0` would never find it. Their resolver is what does.)
So there is nothing to add on our side, and nothing we *could* add — `SetDllImportResolver` allows
one resolver per assembly and that slot is theirs. Contrast PortAudio, whose resolver is ours
because the `DllImport` is ours.

**What is not handled is where the loader looks.** Homebrew on Apple Silicon installs into
`/opt/homebrew/lib`, and dyld's default search path is `/usr/local/lib` and `/usr/lib` — so a
resolver that asks for a leaf name finds nothing on exactly the machine this is written for. Hence
`run.sh` exporting `DYLD_FALLBACK_LIBRARY_PATH`, and the README telling anyone who launches the app
another way (`dotnet run`, `dotnet test`, the binary by hand) to put it in their shell profile.
A future `.app` bundle should carry the dylib in `Contents/Frameworks` instead: the hardened runtime
a notarized bundle needs strips every `DYLD_*` variable.

**No udev counterpart, and none needed.** The QA403's interface is vendor-specific and no macOS
system driver claims it, so libusb opens it with no kext, dext or entitlement.
