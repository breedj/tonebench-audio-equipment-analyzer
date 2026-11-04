# Installing Tonebench - Audio Equipment Analyzer

This guide is for people who want to *use* the analyzer. Building it from source is described in
[README.md](README.md).

Every release on the [releases page](https://github.com/breedj/tonebench-audio-equipment-analyzer/releases)
comes with four downloads:

| File | For |
|---|---|
| `tonebench-<version>-win-x64-setup.exe` | Windows 10/11, 64-bit — the installer (recommended) |
| `tonebench-<version>-win-x64.zip` | Windows — portable, no installer |
| `tonebench_<version>_amd64.deb` | Debian, Ubuntu, Linux Mint and other apt-based distributions |
| `tonebench-<version>-linux-x64.tar.gz` | Any other 64-bit Linux |

Beside them is `SHA256SUMS.txt`, a checksum for each of the four — see
[Verifying your download](#verifying-your-download).

Nothing else is needed to run it: the .NET runtime is built into the program, no USB driver is
needed for the QA403, and no Visual C++ redistributable is needed either. The analyzer runs as a
small local web server and opens its screen in your normal browser (Chrome, Edge or Firefox — any
current one).

There is **one** prerequisite the installer cannot supply, and only if you want to measure through
a **sound card** rather than a QA403: that card's own **ASIO driver**. Windows' other audio paths
present a card as a separate input device and a separate output device, and a measurement needs
both at once on one clock — so a card with no ASIO driver does not appear in the sound-card picker's
list, however well it works for playback. Interfaces sold for recording (MOTU, Focusrite, RME,
Behringer…) ship one; a laptop's built-in audio generally does not, and the generic
[ASIO4ALL](https://asio4all.org) is the usual stand-in there. A QA403 needs none of this.

---

## Windows

### Install

1. Download `tonebench-<version>-win-x64-setup.exe` and run it.
2. Windows SmartScreen will say **"Windows protected your PC"** and name an unknown publisher.
   Click **More info**, then **Run anyway**. See
   [Why Windows warns about this download](#why-windows-warns-about-this-download) below if you
   would rather check before you click — the SHA256 checksum on the release page is there for
   exactly that.
3. Click through the wizard. It installs for your user account only (into
   `%LOCALAPPDATA%\Programs\Tonebench`), so **no administrator password** is asked and
   nothing changes for other users of the PC. A desktop icon is optional.

No USB driver needs to be installed. Plug the QA403 into a USB port and Windows binds it on its
own; the front-panel LED does not need to be any particular colour.

### Start and stop

Start **Tonebench - Audio Equipment Analyzer** from the Start Menu. A black console window opens — that is the
program — and a moment later your browser opens on `http://127.0.0.1:8399` with the analyzer.

- **To quit, close the console window** (or press Ctrl+C in it). The program then shuts the QA403
  down cleanly and saves its data; wait for the window to disappear before unplugging the device.
  Closing only the browser tab leaves the analyzer running — just open the address again.
- **Tonebench - Audio Equipment Analyzer (simulator)** in the Start Menu runs without any hardware, against a
  simulated device and several simulated amplifiers and loudspeakers. Use it to explore the
  measurements before connecting anything.

If the browser shows "connection refused", the console window has probably closed — start the
program again and read what the window says.

### Portable use (zip)

Unzip `tonebench-<version>-win-x64.zip` anywhere — a USB stick works — and run
`Tonebench.App.exe`. Everything works the same; you just get no Start Menu entry and no uninstaller.
The program never writes into its own folder, so it can sit on read-only media.

### Upgrade

Run the new installer. If the analyzer is running, the installer tells you so and can close it for
you; it shuts down cleanly before the files are replaced. Your projects and settings are untouched.

Coming from *QA403 Audio Analyzer* (0.2.0 or earlier) is a rename rather than an upgrade — see
[Upgrading from QA403 Audio Analyzer](#upgrading-from-qa403-audio-analyzer).

### Uninstall

*Settings › Apps › Installed apps › Tonebench - Audio Equipment Analyzer › Uninstall*, or the **Uninstall** entry
in the Start Menu group. Your projects, settings and logs are deliberately left in place (see
[Where your data is](#where-your-data-is)); delete those folders yourself if you want them gone.

---

## Why Windows warns about this download

Tonebench is not code-signed, so Windows has no publisher name to show for it and SmartScreen says
"Windows protected your PC" the first time you run a new version. The portable zip behaves the same
way, and on a Windows 11 machine with Smart App Control switched on the file may be blocked outright
rather than offered a **Run anyway** button.

Signing is planned once the project's source is public. It is worth being straight about what that
will and will not do, because it is widely misunderstood: Microsoft's own
[guidance for developers](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation)
says a signed application **still** shows the warning until it has built up a reputation over
"several weeks and hundreds of clean installs", and that Extended Validation certificates no longer
bypass SmartScreen at all. What a certificate changes is that the dialog names a publisher instead
of saying "unknown", and that each release inherits the trust earlier ones earned rather than
starting from zero. A small project will see this dialog either way for a while.

### Verifying your download

Every release carries a `SHA256SUMS.txt` with one checksum per file. Checking against it confirms
the download is byte-for-byte the file that was built and published — which is the question a
signature would otherwise answer for you.

On Windows, in PowerShell:

```powershell
Get-FileHash tonebench-<version>-win-x64-setup.exe -Algorithm SHA256
```

Compare the hash it prints with the line for that filename in `SHA256SUMS.txt`.

On Linux, with `SHA256SUMS.txt` saved next to the package:

```bash
sha256sum -c --ignore-missing SHA256SUMS.txt
```

---

## Linux

### Debian, Ubuntu, Mint (.deb)

```bash
sudo apt install ./tonebench_<version>_amd64.deb
sudo usermod -aG plugdev $USER
```

Then **log out and log back in** (or reboot) — the group change only applies to new logins. The
package installs:

- the program, started as `tonebench` or from your applications menu (category
  *Audio/Video*),
- the udev rule that lets members of the `plugdev` group open the QA403 (without it only root can),
- its two library dependencies, `libusb-1.0-0` and `libfontconfig1`.

Adding *you* to `plugdev` is the one step a package cannot do, hence the second command.

The analyzer runs in a terminal window and opens your browser on `http://127.0.0.1:8399`. **To
quit, close the terminal window or press Ctrl+C** in it; the QA403 is shut down cleanly first. The
menu entry has a right-click action *Tonebench - Audio Equipment Analyzer (simulator)* for running without hardware
(`tonebench --simulate` from a shell).

Upgrade by installing the newer .deb the same way. Remove with
`sudo apt remove tonebench`; your projects and settings are left in place.

### Other distributions (tar.gz)

```bash
tar -xzf tonebench-<version>-linux-x64.tar.gz
cd tonebench-linux-x64
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

Start the analyzer with `./Tonebench.App` from the unpacked folder (`./Tonebench.App --simulate` for the
simulator). You can move the folder anywhere; the program never writes into it.

---

## Upgrading from QA403 Audio Analyzer

The app was called *QA403 Audio Analyzer* up to and including version 0.2.0. Three things are worth
knowing on the first run of a later version.

**Uninstall the old version first.** The installer treats Tonebench as a separate program, so
running it over a QA403 Audio Analyzer install leaves both listed in *Installed apps*. Uninstalling
the old one removes nothing of yours: projects, settings and logs are never touched by an uninstall.
On Debian-based Linux, `sudo apt remove qa403-audio-analyzer` does the same.

**Your settings and calibrations come across by themselves.** On first start, Tonebench copies the
configuration database out of the old data folder — `%LOCALAPPDATA%\Qa403` on Windows,
`~/.local/share/Qa403` on Linux — into its own. That file holds your settings, your recent-projects
list and, the part that cannot be recreated, your loopback and impedance-fixture calibrations. It is
a copy: the old folder is left exactly as it was, so nothing is lost if you want to go back. The
startup console says so when it happens. The old logs, diagnostic captures and any WAV recordings
stay behind — delete that folder when you are satisfied, or keep it as a backup.

**Your existing projects open unchanged.** Project files used to end in `.qa403` and new ones now end
in `.tbench`. Both are listed by *Project › Open* and both open normally — a project file is
recognised by what is inside it, not by its name. A `.qa403` file keeps its own name when you save
it; use *Save As* if you would rather have the new extension.

---

## Where your data is

| | Windows | Linux |
|---|---|---|
| Your projects (`.tbench` files) | `Documents\Tonebench Projects` | `~/Tonebench Projects` |
| Settings and calibrations | `%LOCALAPPDATA%\Tonebench\tonebench-config.db` | `~/.local/share/Tonebench/tonebench-config.db` |
| Logs (last 14 days) | `%LOCALAPPDATA%\Tonebench\logs` | `~/.local/share/Tonebench/logs` |

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
being added to `plugdev`, and that `ls -l /dev/bus/usb/*/*` shows the analyzer's device node with
group `plugdev` (find it with `lsusb -d 16c0:4e39` for a QA403, or `lsusb -d 16c0:4e37` for a
QA402). Then press **Connect** in the status bar; a restart of the program is not needed.

**The QA403 does not respond even after replugging.**
Quit the analyzer, unplug the QA403, wait a few seconds, plug it back in and start the analyzer
again. Being hard-killed mid-measurement (a crash, a forced power-off) can leave the device waiting
for a command it never gets; a power cycle clears that.

**The browser opens on a different port than 8399.**
Something else was using 8399, and the analyzer picked the next free one (up to 8409). The console
window shows the address it is actually using.

**My sound card is not in the sound-card list.**
It has no ASIO driver, or the driver was installed after the analyzer was started. Install the
manufacturer's ASIO driver (or ASIO4ALL for a card without one) and **restart the analyzer** — a
newly registered ASIO driver is only picked up on a start, which is true of every ASIO host. The
log line beginning `Audio devices enumerated by` lists the host APIs the analyzer found, which
tells you whether ASIO was there at all.

**PDF reports fail on Linux.**
`libfontconfig1` is missing; the .deb and `install.sh` install it, a manual setup may not have.

**Something else.**
The console window shows what the program is doing; the same text is in the log folder above.
Please attach the day's log file when you [open an issue](https://github.com/breedj/tonebench-audio-equipment-analyzer/issues).

---

## Updates

On startup, and every six hours, the analyzer checks GitHub for a newer release and shows a banner
if there is one. It never downloads or installs anything itself — you download the new package and
install it as above. The check can be turned off in *Configuration › Check for updates*, and it
fails silently on a machine without internet access.
