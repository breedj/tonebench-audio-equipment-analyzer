# qa40x-audio-analyzer-cp
Cross platform audio analyzer for qa40x devices
Developed with the help of Claude.ai

## Linux setup: USB permissions

The QA403 is a driverless USB device; by default only root can open it. Install the udev rule
once so it's accessible to your user account:

```bash
sudo cp packaging/udev/99-qa403.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules
sudo udevadm trigger
sudo usermod -aG plugdev $USER   # log out/in (or reboot) for the group change to take effect
```

Without this, `dotnet run --project src/Qa403.App` (which talks to real hardware by default —
pass `--simulate` to use the synthetic device instead) will fail to open the device with a
permissions error.
