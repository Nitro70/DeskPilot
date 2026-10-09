#!/usr/bin/env bash
# Publishes the Linux app for one runtime and packs it:
#   bash packaging/linux/build-package.sh linux-x64      (or linux-arm64)
#
# Writes to artifacts/:
#   DeskPilot-<version>-<rid>.tar.gz          the program, install.sh, uninstall.sh, menu entry, icons, README, LICENSE
#   DeskPilot-<version>-x86_64.AppImage       linux-x64 only
#
# The AppImage needs appimagetool (https://github.com/AppImage/appimagetool): put it on PATH or set APPIMAGETOOL to
# its path; APPIMAGE_RUNTIME may name a type2 runtime file so nothing is downloaded while packing. When appimagetool
# is missing or cannot run, the AppImage is skipped with a warning and the tar.gz is still written.
set -euo pipefail

rid="${1:-linux-x64}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$root"
here="$root/packaging/linux"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)"

warn() {
  if [ -n "${GITHUB_ACTIONS:-}" ]; then echo "::warning::$*"; else echo "WARNING: $*" >&2; fi
}

dotnet publish src/DeskPilot.Linux/DeskPilot.Linux.csproj -c Release -r "$rid"
program="bin/publish-$rid/deskpilot"
[ -f "$program" ] || { echo "dotnet publish did not produce $program" >&2; exit 1; }

out="$root/artifacts"
mkdir -p "$out"
stage_root="$(mktemp -d)"
trap 'rm -rf "$stage_root"' EXIT

# ------------------------------------------------------------------ tar.gz

pkg="DeskPilot-$version-$rid"
stage="$stage_root/$pkg"
mkdir -p "$stage"
install -m 755 "$program" "$stage/deskpilot"
install -m 755 "$here/install.sh" "$here/uninstall.sh" "$stage/"
install -m 644 "$here/deskpilot.desktop" "$here/deskpilot.png" "$here/deskpilot-128.png" "$stage/"
install -m 644 README.md LICENSE "$stage/"
tar -C "$stage_root" --owner=0 --group=0 --numeric-owner -czf "$out/$pkg.tar.gz" "$pkg"
echo "Wrote $out/$pkg.tar.gz"

# ------------------------------------------------------------------ AppImage

case "$rid" in
  linux-x64) arch=x86_64 ;;
  *) echo "No AppImage for $rid (the AppImage is built for linux-x64 only)."; exit 0 ;;
esac

tool="${APPIMAGETOOL:-}"
if [ -z "$tool" ]; then
  tool="$(command -v appimagetool || command -v appimagetool-x86_64.AppImage || true)"
fi
if [ -z "$tool" ]; then
  warn "appimagetool not found (put it on PATH or set APPIMAGETOOL): skipping the AppImage."
  exit 0
fi

# appimagetool is itself an AppImage. Extract-and-run needs no FUSE, which containers and CI runners often lack.
export APPIMAGE_EXTRACT_AND_RUN=1
if ! probe="$("$tool" --version 2>&1)"; then
  warn "appimagetool ($tool) cannot run here: skipping the AppImage. Its output: $(printf '%s' "$probe" | tail -n 3 | tr '\n' ' ')"
  exit 0
fi
echo "Using $(printf '%s' "$probe" | head -n 1)"

appdir="$stage_root/DeskPilot.AppDir"
mkdir -p "$appdir/usr/bin" "$appdir/usr/share/applications" \
  "$appdir/usr/share/icons/hicolor/256x256/apps" "$appdir/usr/share/icons/hicolor/128x128/apps"
install -m 755 "$program" "$appdir/usr/bin/deskpilot"
cat > "$appdir/AppRun" <<'EOF'
#!/bin/sh
# Entry point of the DeskPilot AppImage: runs the bundled program with all arguments (including --mcp-bridge).
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/deskpilot" "$@"
EOF
chmod 755 "$appdir/AppRun"
install -m 644 "$here/deskpilot.desktop" "$appdir/deskpilot.desktop"
install -m 644 "$here/deskpilot.desktop" "$appdir/usr/share/applications/deskpilot.desktop"
install -m 644 "$here/deskpilot.png" "$appdir/deskpilot.png"
install -m 644 "$here/deskpilot.png" "$appdir/usr/share/icons/hicolor/256x256/apps/deskpilot.png"
install -m 644 "$here/deskpilot-128.png" "$appdir/usr/share/icons/hicolor/128x128/apps/deskpilot.png"
ln -s deskpilot.png "$appdir/.DirIcon"

image="$out/DeskPilot-$version-$arch.AppImage"
args=(--no-appstream)
if [ -n "${APPIMAGE_RUNTIME:-}" ]; then args+=(--runtime-file "$APPIMAGE_RUNTIME"); fi
rm -f "$image"
ARCH="$arch" "$tool" "${args[@]}" "$appdir" "$image"
[ -f "$image" ] || { echo "appimagetool did not write $image" >&2; exit 1; }
chmod 755 "$image"
echo "Wrote $image"
