# Installing QA403 Audio Analyzer

This guide is for people who want to *use* the analyzer. Building it from source is described in
[README.md](README.md).

Every release on the [releases page](https://github.com/breedj/qa40x-audio-analyzer-cp/releases)
comes with four downloads:

| File | For |
|---|---|
| `qa403-audio-analyzer-<version>-win-x64-setup.exe` | Windows 10/11, 64-bit — the installer (recommended) |
| `qa403-audio-analyzer-<version>-win-x64.zip` | Windows — portable, no installer |
| `qa403-audio-analyzer_<version>_amd64.deb` | Debian, Ubuntu, Linux Mint and other apt-based distributions |
| `qa403-audio-analyzer-<version>-linux-x64.tar.gz` | Any other 64-bit Linux |

Nothing else is needed: the .NET runtime is built into the program. The analyzer runs as a small
local web server and opens its screen in your normal browser (Chrome, Edge or Firefox — any
current one).

---

## Windows

### Install

1. Download `qa403-audio-analyzer-<version>-win-x64-setup.exe` and run it.
2. Windows SmartScreen will say **"Windows protected your PC"**, because the program is not signed
   with a paid certificate. Click **More info**, then **Run anyway**.
3. Click through the wizard. It installs for your user account only (into
   `%LOCALAPPDATA%\Programs\QA403 Audio Analyzer`), so **no administrator password** is asked and
   nothing changes for other users of the PC. A desktop icon is optional.

No USB driver needs to be installed. Plug the QA403 into a USB port and Windows binds it on its
own; the front-panel LED does not need to be any particular colour.

### Start and stop

Start **QA403 Audio Analyzer** from the Start Menu. A black console window opens — that is the
program — and a moment later your browser opens on `http://127.0.0.1:8399` with the analyzer.

- **To quit, close the console window** (or press Ctrl+C in it). The program then shuts the QA403
  down cleanly and saves its data; wait for the window to disappear before unplugging the device.
  Closing only the browser tab leaves the analyzer running — just open the address again.
- **QA403 Audio Analyzer (simulator)** in the Start Menu runs without any hardware, against a
  simulated device and several simulated amplifiers and loudspeakers. Use it to explore the
  measurements before connecting anything.

If the browser shows "connection refused", the console window has probably closed — start the
program again and read what the window says.

### Portable use (zip)

Unzip `qa403-audio-analyzer-<version>-win-x64.zip` anywhere — a USB stick works — and run
`Qa403.App.exe`. Everything works the same; you just get no Start Menu entry and no uninstaller.
The program never writes into its own folder, so it can sit on read-only media.

### Upgrade

Run the new installer. If the analyzer is running, the installer tells you so and can close it for
you; it shuts down cleanly before the files are replaced. Your projects and settings are untouched.

### Uninstall

*Settings › Apps › Installed apps › QA403 Audio Analyzer › Uninstall*, or the **Uninstall** entry
in the Start Menu group. Your projects, settings and logs are deliberately left in place (see
[Where your data is](#where-your-data-is)); delete those folders yourself if you want them gone.

---

## Linux

### Debian, Ubuntu, Mint (.deb)

```bash
sudo apt install ./qa403-audio-analyzer_<version>_amd64.deb
sudo usermod -aG plugdev $USER
```

Then **log out and log back in** (or reboot) — the group change only applies to new logins. The
package installs:

- the program, started as `qa403-audio-analyzer` or from your applications menu (category
  *Audio/Video*),
- the udev rule that lets members of the `plugdev` group open the QA403 (without it only root can),
- its two library dependencies, `libusb-1.0-0` and `libfontconfig1`.

Adding *you* to `plugdev` is the one step a package cannot do, hence the second command.

The analyzer runs in a terminal window and opens your browser on `http://127.0.0.1:8399`. **To
quit, close the terminal window or press Ctrl+C** in it; the QA403 is shut down cleanly first. The
menu entry has a right-click action *QA403 Audio Analyzer (simulator)* for running without hardware
(`qa403-audio-analyzer --simulate` from a shell).

Upgrade by installing the newer .deb the same way. Remove with
`sudo apt remove qa403-audio-analyzer`; your projects and settings are left in place.

### Other distributions (tar.gz)

```bash
tar -xzf qa403-audio-analyzer-<version>-linux-x64.tar.gz
cd qa403-audio-analyzer-linux-x64
./install.sh
```

Run `install.sh` as your normal user; it asks for `sudo` itself. It copies the udev rule, adds you
to the `plugdev` group and installs `libusb-1.0-0` and `libfontconfig1` with apt. On a
distribution without apt it stops at that last step — install those two libraries (or your
distribution's equivalents, e.g. `libusb1` and `fontconfig` on Fedora) by hand, then run the manual
steps below. Log out and back in afterwards.

Manual equivalent of the script:

```bash
sudo cp 99-qa403.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules
sudo udevadm trigger
sudo usermod -aG plugdev $USER      # then log out and back in
```

Start the analyzer with `./Qa403.App` from the unpacked folder (`./Qa403.App --simulate` for the
simulator). You can move the folder anywhere; the program never writes into it.

---

## Where your data is

| | Windows | Linux |
|---|---|---|
| Your projects (`.qa403` files) | `Documents\QA403 Projects` | `~/QA403 Projects` |
| Settings and calibrations | `%LOCALAPPDATA%\Qa403\qa403-config.db` | `~/.local/share/Qa403/qa403-config.db` |
| Logs (last 14 days) | `%LOCALAPPDATA%\Qa403\logs` | `~/.local/share/Qa403/logs` |

Projects can be saved anywhere you like from the *Project* menu; the folder above is only the
default. Settings include the impedance-fixture and loopback calibrations, which describe *your*
QA403 and *your* test leads — that is why they are kept apart from projects, which you may copy to
another machine.

Copy the projects folder and, if you want your calibrations too, the settings file to back up
everything. Uninstalling never removes any of it.

---

## If something does not work

**The status bar says "Disconnected".**
Check the USB cable and try another port. On Linux, make sure you logged out and back in after
being added to `plugdev`, and that `ls -l /dev/bus/usb/*/*` shows the QA403's device node with group
`plugdev` (find the device with `lsusb -d 16c0:4e39`). Then press **Connect** in the status bar; a
restart of the program is not needed.

**The QA403 does not respond even after replugging.**
Quit the analyzer, unplug the QA403, wait a few seconds, plug it back in and start the analyzer
again. Being hard-killed mid-measurement (a crash, a forced power-off) can leave the device waiting
for a command it never gets; a power cycle clears that.

**The browser opens on a different port than 8399.**
Something else was using 8399, and the analyzer picked the next free one (up to 8409). The console
window shows the address it is actually using.

**PDF reports fail on Linux.**
`libfontconfig1` is missing; the .deb and `install.sh` install it, a manual setup may not have.

**Something else.**
The console window shows what the program is doing; the same text is in the log folder above.
Please attach the day's log file when you [open an issue](https://github.com/breedj/qa40x-audio-analyzer-cp/issues).

---

## Updates

On startup, and every six hours, the analyzer checks GitHub for a newer release and shows a banner
if there is one. It never downloads or installs anything itself — you download the new package and
install it as above. The check can be turned off in *Configuration › Check for updates*, and it
fails silently on a machine without internet access.
