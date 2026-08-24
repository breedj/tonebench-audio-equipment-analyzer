# qa40x-audio-analyzer-cp
Cross platform audio analyzer for qa40x devices
Developed with the help of Claude.ai

## Running

`dotnet run --project src/Qa403.App` talks to a real QA403 by default. Pass `--simulate` to use a
synthetic device instead — no hardware required, useful for UI work or trying the app out:

```bash
dotnet run --project src/Qa403.App -- --simulate
```

`--no-browser` skips the automatic browser launch.

## Linux setup: USB permissions

The QA403 is a driverless USB device; by default only root can open it. Run the install script
once so it's accessible to your user account:

```bash
./packaging/linux/install.sh
```

This installs the udev rule, adds you to the `plugdev` group, and ensures `libusb-1.0-0` is
installed via `apt` (a no-op if it's already there, which — being such a common transitive
dependency — it usually is). Log out and back in (or reboot) afterwards for the group change to
apply.

Equivalent manual steps, if you'd rather not run the script:

```bash
sudo cp packaging/udev/99-qa403.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules
sudo udevadm trigger
sudo usermod -aG plugdev $USER   # log out/in (or reboot) for the group change to take effect
sudo apt-get install -y libusb-1.0-0   # only if not already installed
```

Without this, the app will fail to open the device with a permissions error.

## Building a release

Releases are self-contained, trimmed, single-file publishes — no .NET runtime install required on
the target machine. Build the frontend first, then publish for the target platform:

```bash
cd web && npm ci && npm run build && cd ..

dotnet publish src/Qa403.App/Qa403.App.csproj -c Release -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true -p:PublishTrimmed=true -p:TrimMode=partial \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:InvariantGlobalization=true

dotnet publish src/Qa403.App/Qa403.App.csproj -c Release -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true -p:PublishTrimmed=true -p:TrimMode=partial \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:InvariantGlobalization=true
```

Output lands in each project's `bin/Release/net10.0/<rid>/publish/` — a single executable plus a
`logs/` folder created on first run. `TrimMode=partial` (not `full`) because ASP.NET Core's own
trimming support is itself partial; `full` is unsupported for web apps. Measured single-file
executable size: ~21.5 MB (win-x64), ~21.6 MB (linux-x64).

Trimming removes System.Text.Json's reflection-based fallback entirely, which breaks every JSON
request/response unless it has source-generated metadata — see `Qa403.App/Json/AppJsonContext.cs`.
Adding a new request/response DTO to any endpoint means adding it (or confirming it's reachable
from an existing root type's property graph) to that context, or the trimmed build will 500 on
that route with `JsonTypeInfo metadata for type '...' was not provided`. This is caught by actually
running the published binary, not by `dotnet build`/`dotnet test` — see the smoke test below.

`.github/workflows/release.yml` runs this same publish for both platforms on every push (with a
smoke test that boots the published binary in `--simulate` mode and hits an endpoint before
declaring success), and attaches the artifacts to a GitHub Release whenever a `v*` tag is pushed.

## Update checking

On startup, and every 6 hours, the app checks GitHub Releases for a newer version and shows a
dismissible banner if one exists — it never downloads or installs anything itself. This can be
turned off in the Configuration dialog ("Check for updates") for lab/production machines that are
deliberately offline; the check also fails silently on any network error, so an air-gapped machine
never sees an error dialog over it.
