#!/usr/bin/env bash
# Publishes the Linux app for one runtime and packs it: bash packaging/linux/build-package.sh linux-x64
set -euo pipefail

rid="${1:-linux-x64}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$root"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)"

dotnet publish src/DeskPilot.Linux/DeskPilot.Linux.csproj -c Release -r "$rid"

out="$root/artifacts"
mkdir -p "$out"
pkg="DeskPilot-$version-$rid"
stage_root="$(mktemp -d)"
stage="$stage_root/$pkg"
mkdir -p "$stage"
cp "bin/publish-$rid/deskpilot" "$stage/"
chmod +x "$stage/deskpilot"
for f in install.sh uninstall.sh deskpilot.desktop deskpilot.png; do
  [ -f "packaging/linux/$f" ] && cp "packaging/linux/$f" "$stage/"
done
cp README.md LICENSE "$stage/"
tar -C "$stage_root" -czf "$out/$pkg.tar.gz" "$pkg"
echo "Wrote $out/$pkg.tar.gz"
