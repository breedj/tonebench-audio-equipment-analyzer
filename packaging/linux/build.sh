#!/usr/bin/env bash
# Builds the Linux packages: frontend -> dotnet publish (linux-x64) -> .deb + tar.gz, into dist/.
#
# The one script both a developer and .github/workflows/release.yml use, so the publish command and
# the package layout exist in exactly one place. Needs dpkg-deb (any Debian/Ubuntu box, or the CI
# runner); it does not run on Windows.
#
#   --version X        Package version. Defaults to <Version> in Directory.Build.props. A prerelease
#                      "1.0.0-rc1" becomes the Debian-legal "1.0.0~rc1" in the .deb.
#   --skip-frontend    Reuse the wwwroot the last `npm run build` wrote.
#   --no-publish       Skip `dotnet publish` and package --publish-dir as it is (the workflow, which
#                      has already published and smoke-tested that folder).
#   --publish-dir D    Where the publish output is (default publish/linux-x64).
#
# Layout of the .deb (package qa403-audio-analyzer):
#   /usr/lib/qa403-audio-analyzer/Qa403.App           the single-file publish, unchanged name
#   /usr/lib/qa403-audio-analyzer/QuestPDF.Fonts.Lato.br  QuestPDF's default font, beside the exe
#   /usr/bin/qa403-audio-analyzer                     a SYMLINK to it — not a wrapper script, because
#                                                     the exe must own its terminal: the terminal's
#                                                     close button (SIGHUP) and Ctrl+C are the
#                                                     graceful shutdown, and a shell in between would
#                                                     intercept them
#   /usr/lib/udev/rules.d/99-qa403.rules              non-root access to the QA403 for plugdev
#   /usr/share/applications/qa403-audio-analyzer.desktop
#   /usr/share/icons/hicolor/{scalable,256x256}/apps/qa403-audio-analyzer.{svg,png}
# Nothing under /usr is written at runtime: logs, diagnostics and the configuration database go to
# ~/.local/share/Qa403 (Qa403AppData), the single-file self-extraction to ~/.net/Qa403.App.
# Depends in deb/control is hand-maintained — dpkg-shlibdeps cannot see inside a single-file bundle.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PROJECT="$ROOT/src/Qa403.App/Qa403.App.csproj"
PUBLISH_DIR="$ROOT/publish/linux-x64"
DIST="$ROOT/dist"
VERSION=""
SKIP_FRONTEND=0
NO_PUBLISH=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version) VERSION="$2"; shift 2 ;;
        --skip-frontend) SKIP_FRONTEND=1; shift ;;
        --no-publish) NO_PUBLISH=1; shift ;;
        --publish-dir) PUBLISH_DIR="$(cd "$2" && pwd)"; shift 2 ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
done

if [[ -z "$VERSION" ]]; then
    VERSION="$(dotnet msbuild "$PROJECT" -getProperty:Version -nologo | tr -d '[:space:]')"
    [[ -n "$VERSION" ]] || { echo "error: could not read <Version> from the project" >&2; exit 1; }
fi
DEB_VERSION="${VERSION//-/\~}"
echo "Version $VERSION (deb $DEB_VERSION)"

if [[ "$NO_PUBLISH" -eq 0 ]]; then
    if [[ "$SKIP_FRONTEND" -eq 0 ]]; then
        (cd "$ROOT/web" && npm ci && npm run build)
    fi
    # The same one-liner as release.yml and README.md; the single-file/trimmed/self-contained shape
    # is in Qa403.App.csproj under the publish condition.
    dotnet publish "$PROJECT" -c Release -r linux-x64 -p:Version="$VERSION" -o "$PUBLISH_DIR" --nologo
fi

for required in Qa403.App QuestPDF.Fonts.Lato.br appsettings.json; do
    [[ -f "$PUBLISH_DIR/$required" ]] || { echo "error: publish output is missing $required in $PUBLISH_DIR" >&2; exit 1; }
done
command -v dpkg-deb >/dev/null 2>&1 || { echo "error: dpkg-deb not found (this script needs a Debian/Ubuntu system)" >&2; exit 1; }

mkdir -p "$DIST"

# ---- .deb --------------------------------------------------------------------------------------
PKG="qa403-audio-analyzer"
STAGE="$DIST/deb/${PKG}_${DEB_VERSION}_amd64"
rm -rf "$STAGE"
mkdir -p "$STAGE/DEBIAN" \
         "$STAGE/usr/lib/$PKG" \
         "$STAGE/usr/bin" \
         "$STAGE/usr/lib/udev/rules.d" \
         "$STAGE/usr/share/applications" \
         "$STAGE/usr/share/icons/hicolor/scalable/apps" \
         "$STAGE/usr/share/icons/hicolor/256x256/apps" \
         "$STAGE/usr/share/doc/$PKG"

install -m 0755 "$PUBLISH_DIR/Qa403.App" "$STAGE/usr/lib/$PKG/Qa403.App"
install -m 0644 "$PUBLISH_DIR/QuestPDF.Fonts.Lato.br" "$STAGE/usr/lib/$PKG/"
install -m 0644 "$PUBLISH_DIR/appsettings.json" "$STAGE/usr/lib/$PKG/"
ln -s "../lib/$PKG/Qa403.App" "$STAGE/usr/bin/$PKG"
install -m 0644 "$ROOT/packaging/udev/99-qa403.rules" "$STAGE/usr/lib/udev/rules.d/"
install -m 0644 "$SCRIPT_DIR/deb/$PKG.desktop" "$STAGE/usr/share/applications/"
install -m 0644 "$SCRIPT_DIR/$PKG.svg" "$STAGE/usr/share/icons/hicolor/scalable/apps/"
install -m 0644 "$SCRIPT_DIR/$PKG.png" "$STAGE/usr/share/icons/hicolor/256x256/apps/"
install -m 0644 "$SCRIPT_DIR/deb/copyright" "$STAGE/usr/share/doc/$PKG/"

sed "s/@VERSION@/$DEB_VERSION/" "$SCRIPT_DIR/deb/control" > "$STAGE/DEBIAN/control"
install -m 0755 "$SCRIPT_DIR/deb/postinst" "$STAGE/DEBIAN/postinst"
install -m 0755 "$SCRIPT_DIR/deb/postrm" "$STAGE/DEBIAN/postrm"
find "$STAGE" -type d -exec chmod 0755 {} +

DEB="$DIST/${PKG}_${DEB_VERSION}_amd64.deb"
rm -f "$DEB"
dpkg-deb --build --root-owner-group "$STAGE" "$DEB"
dpkg-deb --info "$DEB"
dpkg-deb --contents "$DEB"

# ---- tar.gz ------------------------------------------------------------------------------------
# For non-Debian systems: the binary with the udev rule and the setup script beside it, which is
# where install.sh now looks for the rule. tar rather than zip to keep the executable bit.
TAR_STAGE="$DIST/tar/qa403-audio-analyzer-linux-x64"
rm -rf "$TAR_STAGE"
mkdir -p "$TAR_STAGE"
install -m 0755 "$PUBLISH_DIR/Qa403.App" "$TAR_STAGE/"
install -m 0644 "$PUBLISH_DIR/QuestPDF.Fonts.Lato.br" "$PUBLISH_DIR/appsettings.json" "$TAR_STAGE/"
install -m 0755 "$SCRIPT_DIR/install.sh" "$TAR_STAGE/"
install -m 0644 "$ROOT/packaging/udev/99-qa403.rules" "$TAR_STAGE/"
TARBALL="$DIST/qa403-audio-analyzer-${VERSION}-linux-x64.tar.gz"
tar -C "$(dirname "$TAR_STAGE")" -czf "$TARBALL" "$(basename "$TAR_STAGE")"

echo
echo "Packages:"
ls -l "$DEB" "$TARBALL"
