#!/usr/bin/env bash
# Sets up USB access for the QA403 on Linux: installs the udev rule, adds the invoking user to
# plugdev, and ensures libusb-1.0-0 is installed via apt.
#
# Usage: ./install.sh   (from the extracted release tar.gz, or ./packaging/linux/install.sh in the
# repo; run as the normal user — it re-execs itself with sudo for the parts that need root, so your
# $USER/$SUDO_USER stays correct throughout). Users of the .deb do not need this: the package
# carries the rule and the dependencies, and only the usermod line below still applies.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TARGET_USER="${SUDO_USER:-$USER}"

# The rule sits beside this script in the release tar.gz and under packaging/udev/ in the repo.
UDEV_RULE=""
for candidate in "$SCRIPT_DIR/99-qa403.rules" "$SCRIPT_DIR/../udev/99-qa403.rules"; do
    if [[ -f "$candidate" ]]; then
        UDEV_RULE="$candidate"
        break
    fi
done

if [[ "$(id -u)" -ne 0 ]]; then
    echo "Re-running with sudo for the parts that need root..."
    exec sudo --preserve-env=SUDO_USER "$0" "$@"
fi

if [[ -z "$UDEV_RULE" ]]; then
    echo "error: 99-qa403.rules not found beside $SCRIPT_DIR or in ../udev" >&2
    exit 1
fi

echo "Installing udev rule..."
cp "$UDEV_RULE" /etc/udev/rules.d/99-qa403.rules
udevadm control --reload-rules
udevadm trigger

echo "Adding $TARGET_USER to the plugdev group..."
usermod -aG plugdev "$TARGET_USER"

# libusb-1.0-0 is such a common transitive dependency (Bluetooth stacks, various USB tools) that
# it's very likely already present, but `apt-get install` on an up-to-date package is already a
# fast no-op ("0 upgraded, 0 newly installed") — simpler and more reliable than pre-checking via
# `ldconfig -p`, which on a freshly-booted VM in testing reported the library missing even though
# it demonstrably was not (a transient ldconfig-cache read, not a real gap).
#
# libfontconfig1 is for the PDF reports: QuestPDF's bundled native Skia links against it even
# though the app only ever uses the font QuestPDF ships. Present on any desktop install; a minimal
# server image may lack it, and the symptom is the report endpoint failing to load the library.
echo "Ensuring libusb-1.0-0 and libfontconfig1 are installed..."
if ! command -v apt-get >/dev/null 2>&1; then
    echo "error: apt-get not found. Install libusb-1.0-0 and libfontconfig1 (or your distro's equivalent packages) manually, then re-run this script." >&2
    exit 1
fi
apt-get update
apt-get install -y libusb-1.0-0 libfontconfig1

echo
echo "Done. Log out and back in (or reboot) for the plugdev group change to take effect."
