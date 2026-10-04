# Tonebench - Audio Equipment Analyzer
Cross platform audio analyzer for qa40x devices with sound card support.
Developed with the help of Claude.ai

## Running

`dotnet run --project src/Tonebench.App` talks to a real QA403 by default. Pass `--simulate` to use a
synthetic device instead — no hardware required, useful for UI work or trying the app out:

```bash
dotnet run --project src/Tonebench.App -- --simulate
```

`--no-browser` skips the automatic browser launch. `--project <path>` opens that project file at
startup, ahead of the "Show welcome page on startup" preference.

## Projects

A project is one SQLite file with the `.tbench` extension, created and opened from the Project menu
(or the welcome screen) and placed wherever you like — `Documents\Tonebench Projects` (`~/Tonebench Projects`
on Linux) by default. The app's own configuration, including the impedance fixture and loopback
calibrations and the recent-projects list, lives separately in `%LOCALAPPDATA%\Tonebench\tonebench-config.db`
(`~/.local/share/Tonebench/` on Linux).

## Installing

The user-facing guide is [INSTALL.md](INSTALL.md); the short version follows.

## Installing on Windows

Download `tonebench-<version>-win-x64-setup.exe` from the
[releases page](https://github.com/breedj/tonebench-audio-equipment-analyzer/releases) and run it. It installs
per user (into `%LOCALAPPDATA%\Programs\Tonebench - Audio Equipment Analyzer`, no administrator prompt) and adds
**Tonebench - Audio Equipment Analyzer** and **Tonebench - Audio Equipment Analyzer (simulator)** to the Start Menu. The binaries are
not code-signed, so SmartScreen shows "Windows protected your PC" the first time — click *More info*,
then *Run anyway*.

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

## Where your data lives

| | Windows | Linux |
|---|---|---|
| Projects (`.tbench` files) | `Documents\Tonebench Projects` | `~/Tonebench Projects` |
| Configuration (`tonebench-config.db`) | `%LOCALAPPDATA%\Tonebench` | `~/.local/share/Tonebench` |
| Logs (`tonebench-<date>.log`, 14 days kept) | `%LOCALAPPDATA%\Tonebench\logs` | `~/.local/share/Tonebench/logs` |
| Diagnostic captures | `%LOCALAPPDATA%\Tonebench\diagnostics` | `~/.local/share/Tonebench/diagnostics` |

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
