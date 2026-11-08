# Running Tonebench on a Mac — test run

Tonebench drives a QuantAsylum QA403 analyzer over raw USB, or any sound card through PortAudio. It
is built and used on Windows and Linux. The macOS port exists but **has never been run** — not once,
by anyone. You would be the first.

So this is a test run rather than an install. Nothing here is expected to be smooth, and a failure is
a useful result rather than a wasted afternoon. The stages below are ordered so that each one you
reach rules something out, and each says what a pass looks like and what to send back when it doesn't.

About an hour, less if you stop early. The first five stages need no measurement hardware at all.

`README.md`'s *Installing on macOS* section is the short version of the same thing, for anyone who
only wants to get it running.

## What you need

- **An Apple Silicon Mac** (M1 or later) on macOS 14 or newer. Intel Macs are not covered — nothing
  was built for them and nothing was tried.
- **The .NET 10 SDK, arm64 build** — from <https://dotnet.microsoft.com/download>. Not the x64
  build, and not under Rosetta: an x64 process cannot load the arm64 libraries this needs.
- **Node 22 or later**, and **Homebrew**.
- **Git.** The repository is public, so the clone in stage 1 needs no account and no access request.
- *Optional:* a QA403 analyzer, and a USB audio interface with a cable from an output back to an
  input. Each unlocks later stages; neither is needed for the first five.

## Three things that will waste your time if nobody tells you

**A denied microphone permission gives silence, not an error.** The first time the app captures audio
from a sound card, macOS asks for microphone access — and because the app runs as a plain binary
rather than a packaged `.app`, the request is attributed to **the terminal**, not to Tonebench. Allow
it. If you deny it, or denied it for that terminal at some point in the past, there is no second
prompt and **Core Audio hands the app digital zeros instead of an error**. On a measurement
instrument that reads as a dead input or an impossibly perfect noise floor. Fix it under System
Settings ▸ Privacy & Security ▸ Microphone, or run `tccutil reset Microphone` and relaunch to be
asked again. This does not affect the QA403, which is a USB device rather than an audio device.

**Core Audio resamples instead of retuning the card.** PortAudio's macOS backend converts the sample
rate rather than setting the card's, so a measurement at a rate the interface is not actually running
at is the converter's work, not the card's. Set the interface to the rate you intend to measure in
**Audio MIDI Setup** first. The app shows a warning saying so; the warning is correct and is not a
bug to report.

**Homebrew's libraries are not where the loader looks.** On Apple Silicon, Homebrew installs into
`/opt/homebrew/lib`, and the dynamic loader searches only `/usr/local/lib` and `/usr/lib`. `run.sh`
handles this for you. If you start the app any other way — `dotnet run`, `dotnet test`, or the built
binary by hand — add this to your shell profile:

```bash
export DYLD_FALLBACK_LIBRARY_PATH="/opt/homebrew/lib:$DYLD_FALLBACK_LIBRARY_PATH"
```

## Stage 0 — toolchain, and the one assumption everything rests on

```bash
uname -m                 # expect: arm64
dotnet --info            # expect: SDK 10.x, RID osx-arm64
node --version           # expect: v22 or later
brew install libusb portaudio
ls -l "$(brew --prefix)/lib" | grep -E 'libusb-1.0|libportaudio'
```

**Pass:** `arm64`, an `osx-arm64` runtime, and both libraries present. **Note the directory they are
in** — it should be `/opt/homebrew/lib`. If it is somewhere else, say so: that is the single likeliest
thing to need changing.

**Stop:** if `dotnet --info` reports `osx-x64` you have the Intel SDK. Nothing further will work.
Install the arm64 one.

## Stage 1 — get the code and build it

```bash
git clone -b macos-support https://github.com/breedj/tonebench-audio-equipment-analyzer.git
cd tonebench-audio-equipment-analyzer/code

cd web && npm ci && npm run build && npm test && cd ..
dotnet build
dotnet test --filter "Category!=Hardware&Category!=VirtualDevice"
```

**Pass:** a clean build, about **2025** frontend tests and about **7229** .NET tests, all green.
Those are the counts on Windows; a small difference is fine, a failure is not.

**If it fails:** send the full list of failing test names rather than patching anything. One test is
expected to behave differently per platform (`RecentProjectsTests`, which checks how file paths
compare on a case-insensitive disk) — if that is the one that fails, it is interesting and worth
reporting verbatim.

## Stage 2 — first run, with no hardware at all

```bash
./run-simulator.sh
```

This builds the UI, starts the app against a synthetic analyzer, and opens your browser. No cables,
no instruments.

**Pass:** the browser opens on <http://127.0.0.1:8399> and the app loads. The terminal prints three
lines worth checking: a startup line naming the OS and architecture, ending in something like
`Arm64, .NET 10.0.x`; a `listening on http://127.0.0.1:8399 (simulate=True)` line; and a
`Tonebench data:` line giving the logs, diagnostics and configuration paths. **Those paths must be
under `~/Library/Application Support/Tonebench`.**

**Stop and report:** if the data paths are under `~/.local/share/Tonebench` instead, stop here and
say so. An assumption that several decisions rest on would be wrong, and the fix is not something to
improvise.

**If it fails:** a `DllNotFoundException` mentioning `portaudio` means the library was not found
where the app looked. Send the error, plus the output of
`ls -l "$(brew --prefix)/lib" | grep portaudio`.

## Stage 3 — make the simulator measure something

With the simulator running, work through the app: run an **FFT**, then a **THD vs frequency** sweep,
then a **frequency response**. Save a result. Then export a **PDF report** from the Project menu.

**Pass:** all three produce plausible curves, and the PDF opens. The PDF matters more than it looks —
it is the only check on a separate graphics library that also has to work on macOS.

## Stage 4 — shut it down cleanly (the stage that matters most)

Two separate shutdowns, in this order. First press **Ctrl+C** in the terminal. Then start it again
and this time **close the terminal window** with its red button instead.

```bash
pgrep -fl Tonebench.App   # expect: nothing, after each shutdown
```

**Pass:** both paths end with `Host stopped cleanly; process exiting` in the log, and no process is
left behind.

**Why this one:** an unclean exit leaves the USB device claimed and a database write unfinished. That
damaged a project file twice on Windows before it was fixed, which is why the launch script is
written the way it is. The window-close case is reasoned from how Linux behaves and has never been
observed on a Mac, so it is the single most valuable thing you can confirm. If the log is cut off
mid-teardown, send both logs.

## Stage 5 — projects, and how paths compare

Create a project and save it. Close it and reopen it from the recent list. Then save it again, giving
a path that differs from the first **only in upper and lower case** — for example
`~/Documents/amp.tbench` and then `~/documents/AMP.tbench`.

**Pass:** the recent-projects list holds **one** entry, not two. A Mac disk treats those as the same
file, and the app now does too.

## Stage 6 — a sound card, if you have an interface

Pick your interface from the **Analyzer** dropdown in the status bar. Allow the microphone prompt
when it appears (see the warning above). Then patch an output back to an input and measure the
loopback.

**Pass:** the card appears in the list **without** ticking Advanced, its host API reads **Core
Audio**, and it shows up once as a single full-duplex device. A signal sent out at −6 dBFS reads back
at roughly −6 dBFS.

**The question we most want answered:** set the interface to **48 kHz** in Audio MIDI Setup. Then ask
the app to measure at 96 kHz and at 192 kHz. **Does it refuse, or does it open happily anyway?** A
refusal is the good answer. If every rate opens regardless of what the card is set to, the numbers
are being quietly resampled, and that tells us something we currently have to assume. Please report
what happens either way.

**Silence?** If the input reads nothing at all, check the microphone permission before reporting
anything else. That is far and away the likeliest cause.

## Stage 7 — a QA403, if you have one

Start with **nothing plugged in**:

```bash
./run.sh
```

**Pass, step one:** the app starts without a device and the log says `No QA40x analyzer found on USB
— starting without one.` That exact sentence is the proof that the USB library loaded and
successfully looked at an empty bus. It is a real result, not a failure.

Now plug the analyzer in and press **Connect** in the status bar. Then run the hardware tests and a
loopback measurement across the input ranges.

```bash
dotnet test --filter "Category=Hardware&FullyQualifiedName!~Qa451"
```

**Note the filter:** the `!~Qa451` part is required. Seven test classes drive a QA451 programmable
load, which is not supported on macOS, and they **fail rather than skip** there. Without that filter
you will see seven failures that mean nothing. (`Category=VirtualDevice` is not runnable on a Mac at
all.)

**Pass, step two:** the status bar names the model, serial number and firmware version, and the
telemetry row shows three live sensor readings. The hardware tests pass. No driver, permission dialog
or kernel extension is involved at any point — if you went looking for one, there isn't one and that
is correct.

## Failure signatures worth recognising

Three failures have a known shape. If you hit one of these, the right action is in the last column
rather than a fix.

| What you see | What it means | What to do |
|---|---|---|
| `DllNotFoundException` naming `portaudio` or `libusb-1.0` | The library is installed but not where the app looked for it. | Retry with `DYLD_FALLBACK_LIBRARY_PATH=/opt/homebrew/lib ./run.sh`, and report whether that fixed it plus the real path from stage 0. |
| "Another process still holds it" with nothing else running | Probably a permission failure wearing the wrong message. On macOS this interface should need no permission at all. | Check `pgrep -fl Tonebench` first. If empty, send the output of `ioreg -p IOUSB -l -w0 \| grep -i -A5 quantasylum`. |
| `Register N read returned no data.` | A 20 ms timeout on the USB control path is too tight for macOS. Plausible, never observed. | Don't change it locally. Report it, and say whether it happens on every start or occasionally. |

## What to send back

Whatever happens, these three things are enough:

1. **Which stages passed**, and where you stopped.
2. **The newest log file** from `~/Library/Application Support/Tonebench/logs/`. Its first line
   already records your macOS version, the architecture and the runtime, so it answers most of the
   questions anyone would ask you.
3. **The terminal output**, particularly anything in red.

Easier still: the app has a **Report an issue** button in its header. It asks a few questions and
collects the log, the settings and a screenshot into a single zip under
`~/Library/Application Support/Tonebench/issue-reports`, then offers to open the folder. Nothing is
uploaded anywhere — sending it is your decision.

## Known limits, so you don't report them as faults

- **The QA451 programmable load does not work on macOS.** Its USB plumbing is written for Windows and
  Linux only. Choosing it in Configuration will produce a message saying exactly that.
- **There is no installer, no `.dmg` and no `.app`.** Running from source is the whole of what
  exists, deliberately.
- **Intel Macs are out of scope** — untested, and not claimed to work.
- **Keep projects out of an iCloud-synced Documents folder.** A project file is a live database, and a
  folder that can evict files to the cloud is not a safe place for one. The Project menu will save
  anywhere you like.
- **Audio glitches may behave differently here.** The thread-priority trick that prevents dropouts on
  Windows has no effect on macOS, so a dropped-sample count is a genuine finding rather than a known
  bug — please report it with the log.

---

Windows and Linux are the tested platforms; everything above is the first attempt at a third one.
Thank you for trying it.
