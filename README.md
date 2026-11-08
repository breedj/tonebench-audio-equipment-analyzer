# Tonebench - Audio Equipment Analyzer
Cross platform audio analyzer for **QA402** and **QA403** devices and **sound card**.
Developed with the help of Claude.ai

<img width="1666" height="807" alt="image" src="https://github.com/user-attachments/assets/f667c795-e92d-4f44-9f6a-183194f699bb" />
<img width="1244" height="906" alt="image" src="https://github.com/user-attachments/assets/17400dac-f2f4-41a7-9384-73924c95e5f7" />
<img width="1920" height="913" alt="image" src="https://github.com/user-attachments/assets/e45aa13e-4c2a-4663-a2ca-61d8da6bd0f2" />
<img width="908" height="786" alt="image" src="https://github.com/user-attachments/assets/2b61fafa-6f4c-4178-bd06-dc5f662cff35" />
<img width="1920" height="1080" alt="image" src="https://github.com/user-attachments/assets/e0e1f35a-488d-4182-8445-f96957ba2d28" />
<img width="1652" height="979" alt="image" src="https://github.com/user-attachments/assets/10e5a2b4-2072-4cb6-b46d-2c13db30fad4" />



## What it measures

Ten measurement kinds, each with its own settings, charts and stored results:

| Measurement | What it does |
|---|---|
| **FFT analysis** | Spectrum, THD, harmonics, IMD (SMPTE/DIN/CCIF), multitone TD+N, noise floor and weighted noise figures from a live or single capture, with an oscilloscope and spectrogram beside it. |
| **THD vs frequency** | A tone stepped across the band: THD, D2–D5 and D6+ against frequency. |
| **THD vs amplitude** | The drive level stepped at one frequency, against output voltage, output power or input voltage. |
| **IMD vs amplitude** | A two-tone pair stepped the same way, with its 2nd–5th order products. |
| **Frequency response** | A periodic chirp deconvolved to gain and phase, with −1 dB / −3 dB bandwidth corners, group and phase delay, and Bode, Nyquist, Nichols, pole–zero and H(s)-surface views. |
| **Stepped sine response** | The same curves one tone at a time — slower, but nothing else is playing while each point is measured. |
| **Impulse response** | The chirp deconvolved to an impulse, with a gated magnitude and phase, harmonic distortion against frequency, and cumulative-spectral-decay and burst-decay surfaces. |
| **Impedance** | Magnitude and phase in ohms through a series reference resistor, with open/short calibration and a Thiele–Small readout for drivers. |
| **Output impedance** | A two-load load step giving output impedance in ohms and damping factor against frequency. |
| **Crosstalk** | One channel driven, the other read: dB against frequency, either direction or both. |

A built-in simulator and a set of synthetic DUTs (tube amp, loudspeaker, phono stage, classic
filters, clipping stages, impedance fixtures) let every screen be used and verified without
hardware.

## Running

`dotnet run --project src/Tonebench.App` talks to a real QA403 by default. Pass `--simulate` to use a
synthetic device instead — no hardware required, useful for UI work or trying the app out:

```bash
dotnet run --project src/Tonebench.App -- --simulate
```

`--no-browser` skips the automatic browser launch. `--project <path>` opens that project file at
startup, ahead of the "Show welcome page on startup" preference.

For day-to-day use prefer the launch scripts — `run.bat` / `run-simulator.bat` on Windows, `run.sh` /
`run-simulator.sh` on macOS and Linux. They build the frontend and then start the **built binary
directly** rather than through `dotnet run`, which matters: `dotnet run` is a parent process that
holds the app and is killed first when the console window closes, so the USB teardown and the SQLite
checkpoint never run. That damaged a project database twice before the scripts existed.

## Projects

A project is one SQLite file with the `.tbench` extension, created and opened from the Project menu
(or the welcome screen) and placed wherever you like — `Documents\Tonebench Projects` (`~/Tonebench Projects`
on Linux) by default. The app's own configuration, including the impedance fixture and loopback
calibrations and the recent-projects list, lives separately in `%LOCALAPPDATA%\Tonebench\tonebench-config.db`
(`~/.local/share/Tonebench/` on Linux).

Inside a project you keep a list of *measurements* — each one a kind plus the settings you gave it,
reorderable in the sidebar — and every run you save becomes a stored result under it, with its
notes, its full-resolution capture and the parameters that produced it. Results are listed in a
history panel, can be overlaid on the live chart to compare runs, renamed, annotated, and deleted
into a trash that keeps them for a while before purging. *Save as* copies a project with or
without its results, so a project doubles as a template for the next device on the bench.

## Reports and export

Any result exports to CSV (one row per FFT bin, per sweep point or per trace column) and any chart
to PNG, at a size you choose. Beyond that there is a PDF report, built from a dialog that lets you
tick which blocks it carries — charts, readings, metric tiles, harmonic levels, the measurement's
settings, your notes — either for a single result or as a project report covering every measurement
in the file, optionally in black and white for printing.

## Importing a simulation

LTspice and QSpice runs can be imported and drawn beside a measurement, so a change to a schematic
can be checked against the bench without leaving the app. A **transient** run (`.raw` or `.qraw`)
is analysed with the same code the live FFT screen uses, so its THD, THD+N, SNR and harmonic
figures mean exactly what a measured capture's do; an **AC** run imports as a frequency-response
result with gain and phase. The traces you pick are stored inside the project file — simulators
overwrite and often auto-delete their output — and imported runs appear in the history list under
their own heading, to be overlaid, re-analysed, exported and reported on like any measured result.

## The manual

The app carries its own manual, opened from the book button in the header, in two editions over the
same contents: a **Simple guide** (15 pages, plain language and no formulas — what to plug in, what
to press, what the numbers mean) and the **Full manual** (55 pages: every screen, control and
convention, the wiring diagrams, the simulator's DUT models and the measured figures behind them).
Each measurement screen also has a **?** button that opens straight at its own page.

## Installing

The user-facing guide is [INSTALL.md](INSTALL.md); the short version follows.

## Installing on Windows

Download `tonebench-<version>-win-x64-setup.exe` from the
[releases page](https://github.com/breedj/tonebench-audio-equipment-analyzer/releases) and run it. It installs
per user (into `%LOCALAPPDATA%\Programs\Tonebench`, no administrator prompt) and adds
**Tonebench - Audio Equipment Analyzer** to the Start Menu — one entry, the simulator being a choice in
the app's own Analyzer dropdown rather than a second shortcut. The binaries are
not code-signed, so SmartScreen shows "Windows protected your PC" the first time — click *More info*,
then *Run anyway*, or check the download against the release's `SHA256SUMS.txt` first.
[INSTALL.md](INSTALL.md#why-windows-warns-about-this-download) explains the warning, and why a
certificate would not simply remove it.

The app runs as a console window with the analyzer open in your browser; **closing that console
window is how you quit**, and it shuts the QA403 down cleanly before it goes. No USB driver is
installed: the QA403 binds to WinUSB on its own. Uninstall from *Settings > Apps* (or the Start Menu
entry); your projects, configuration and logs are left in place.

The zip beside the installer holds the same files for a portable install — unzip anywhere and run
`Tonebench.App.exe`.

## Installing on Linux

### Debian / Ubuntu (.deb)

```bash
sudo apt install ./tonebench_<version>_amd64.deb
sudo usermod -aG plugdev $USER   # log out and back in (or reboot) for the group change to take effect
```

The package brings the udev rule that lets the `plugdev` group open the QA403, its two library
dependencies (`libusb-1.0-0`, `libfontconfig1`) and an applications-menu entry; adding *yourself* to
`plugdev` is the one step a package cannot do. Start it from the menu or with `tonebench`
(`--simulate` for the synthetic device). It runs in a terminal on purpose: the terminal is the log,
and closing it (or Ctrl+C) is how you quit — the app catches that and shuts the device down cleanly.

### Other distributions (tar.gz)

The QA403 is a driverless USB device; by default only root can open it. Unpack the tar.gz and run
the setup script once, as your normal user:

```bash
tar -xzf tonebench-<version>-linux-x64.tar.gz
cd tonebench-linux-x64
./install.sh
```

It installs the udev rule that sits beside it, adds you to the `plugdev` group, and installs
`libusb-1.0-0` and `libfontconfig1` via `apt` (a no-op if they are already there). Log out and back
in (or reboot) afterwards for the group change to apply. Equivalent manual steps, if you'd rather
not run the script:

```bash
sudo cp 99-qa403.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules
sudo udevadm trigger
sudo usermod -aG plugdev $USER   # log out/in (or reboot) for the group change to take effect
sudo apt-get install -y libusb-1.0-0 libfontconfig1   # or your distribution's equivalents
```

Without this, the app will fail to open the device with a permissions error. Then run `./Tonebench.App`.

## Installing on macOS

**There is no macOS download — macOS runs from source only, and it is unverified.** Apple Silicon
only; nothing has been tried on an Intel Mac. Nobody on the project owns a Mac, so treat a first run
as a test rather than as a supported install, and please send the log if something is wrong (Help
has a *Report an issue* button that collects it for you).

If you are the one doing that first run, **[`MACOS-TESTING.md`](MACOS-TESTING.md)** is written for
you: the same setup as an ordered test, with what a pass looks like at each stage, the three traps
that waste an afternoon, and what to send back.

```bash
brew install libusb portaudio
# .NET 10 SDK (arm64 build, not the x64 one under Rosetta): https://dotnet.microsoft.com/download
# Node 22 or later
cd code
./run.sh                 # or ./run-simulator.sh, which needs no hardware
```

**No driver and no permissions step are needed for the QA403.** It presents a vendor-specific bulk
USB interface that no macOS system driver claims, so libusb can open it directly — there is no udev
equivalent to install, no group to join, no kernel extension and no entitlement. If you were looking
for the macOS counterpart of `99-qa403.rules`, there isn't one and that is correct.

`run.sh` points the dynamic loader at Homebrew's library directory for you. If you launch the app
some other way — `dotnet run`, `dotnet test`, or the built binary by hand — libusb will not be found,
because Homebrew on Apple Silicon installs into `/opt/homebrew/lib` and that is not on the loader's
default search path. Add this to your shell profile:

```bash
export DYLD_FALLBACK_LIBRARY_PATH="/opt/homebrew/lib:$DYLD_FALLBACK_LIBRARY_PATH"
```

Two macOS-specific things to know before you trust a measurement:

- **A sound card is read through Core Audio, which resamples rather than retuning the card.** Set
  the interface to the sample rate you are measuring in *Audio MIDI Setup* first; otherwise
  PortAudio converts and the numbers are the converter's. The Sound card dialog says so too. The
  QA403 itself is unaffected — it is a USB device, not an audio device.
- **The first capture asks for microphone permission, and a denial is silent.** Because the app runs
  as a plain binary rather than an `.app` bundle, macOS attributes the request to the terminal that
  launched it. If you deny it — or denied it for that terminal some time ago — Core Audio hands the
  app **digital silence instead of an error**, which looks like a dead input or an impossibly good
  noise floor. Enable your terminal under System Settings ▸ Privacy & Security ▸ Microphone, or run
  `tccutil reset Microphone` and relaunch to be asked again.

The QA451 programmable load is **not supported on macOS**: its transport is written for Windows'
`hid.dll` and Linux's `hidraw`, and macOS would need a third one over IOKit.

## Where your data lives

| | Windows | Linux | macOS |
|---|---|---|---|
| Projects (`.tbench` files) | `Documents\Tonebench Projects` | `~/Documents/Tonebench Projects` | `~/Documents/Tonebench Projects` |
| Configuration (`tonebench-config.db`) | `%LOCALAPPDATA%\Tonebench` | `~/.local/share/Tonebench` | `~/Library/Application Support/Tonebench` |
| Logs (`tonebench-<date>.log`, 14 days kept) | `%LOCALAPPDATA%\Tonebench\logs` | `~/.local/share/Tonebench/logs` | `~/Library/Application Support/Tonebench/logs` |
| Diagnostic captures | `%LOCALAPPDATA%\Tonebench\diagnostics` | `~/.local/share/Tonebench/diagnostics` | `~/Library/Application Support/Tonebench/diagnostics` |

The projects folder follows the OS's own Documents folder, so on Linux it honours
`$XDG_DOCUMENTS_DIR` where that is set. (It was documented as `~/Tonebench Projects` until
2026-11-08; that had been wrong since the move to .NET 8, which changed where .NET reports the
Documents folder on both Linux and macOS.)

On macOS, if you have iCloud's *Desktop & Documents Folders* syncing switched on, keep projects
somewhere else — the Project menu will save anywhere. A `.tbench` file is a SQLite database, and a
folder that evicts files to iCloud placeholders is not a safe place for one.

Nothing is ever written next to the executable, which is what makes an upgrade a plain file
replacement and an uninstall safe. The startup console prints these paths. (The single-file
executable unpacks its native libraries to `%TEMP%\.net\Tonebench.App` / `~/.net/Tonebench.App` on first
run; that cache is harmless and can be deleted.)

## Building a release

Releases are self-contained, trimmed, single-file publishes — no .NET runtime install required on
the target machine. The publish flags live in `Tonebench.App.csproj` under a condition that only
`dotnet publish` with a runtime identifier triggers, so the whole command is:

```bash
cd web && npm ci && npm run build && cd ..
dotnet publish src/Tonebench.App/Tonebench.App.csproj -c Release -r win-x64 -o publish/win-x64
dotnet publish src/Tonebench.App/Tonebench.App.csproj -c Release -r linux-x64 -o publish/linux-x64
```

The publish folder holds `Tonebench.App[.exe]` (with `libusb-1.0.dll` inside the bundle on Windows),
`QuestPDF.Fonts.Lato.br` (QuestPDF's default font, which the bundler does not absorb) and
`appsettings.json`; the loose `wwwroot` copy is redundant with the embedded one. `TrimMode=partial`
(not `full`) because ASP.NET Core's own trimming support is itself partial; `full` is unsupported for
web apps. Measured single-file executable size: ~28 MB.

The packages are built by two scripts that run the same publish and then wrap it:

```bash
powershell -File packaging/windows/build.ps1     # Windows: Inno Setup 6 installer into dist/
./packaging/linux/build.sh                        # Linux: .deb and tar.gz into dist/ (needs dpkg-deb)
```

Both take `-Version`/`--version` (default: `<Version>` in `Directory.Build.props`) and
`-SkipFrontend`/`--skip-frontend`. The installer script is `packaging/windows/tonebench.iss`; the
package layout, `Depends` and maintainer scripts are under `packaging/linux/deb/`. The app icon is
`packaging/icons/tonebench.svg`; `npm run icons` in `web/` regenerates the `.ico`, the `.png` and the
favicon from it.

Trimming removes System.Text.Json's reflection-based fallback entirely, which breaks every JSON
request/response unless it has source-generated metadata — see `Tonebench.App/Json/AppJsonContext.cs`.
Adding a new request/response DTO to any endpoint means adding it (or confirming it's reachable
from an existing root type's property graph) to that context, or the trimmed build will 500 on
that route with `JsonTypeInfo metadata for type '...' was not provided`. This is caught by actually
running the published binary, not by `dotnet build`/`dotnet test` — see the smoke test below.

`.github/workflows/release.yml` runs this same publish for both platforms on every push, smoke-tests
the published binary in `--simulate` mode (boot, hit an endpoint, render a PDF), builds the installer
and the .deb with the scripts above, installs each one on the runner and runs the *installed* app
the same way — then uninstalls and checks the user's data folder survived. Whenever a `v*` tag is
pushed, the installer, the zip, the .deb and the tar.gz are attached to a GitHub Release.

## Update checking

On startup, and every 6 hours, the app checks GitHub Releases for a newer version and shows a
dismissible banner if one exists — it never downloads or installs anything itself. This can be
turned off in the Configuration dialog ("Check for updates") for lab/production machines that are
deliberately offline; the check also fails silently on any network error, so an air-gapped machine
never sees an error dialog over it.
