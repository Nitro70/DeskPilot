#!/bin/sh
# Removes what install.sh installed, and nothing else:
#
#   ~/.local/bin/deskpilot
#   ~/.local/share/applications/deskpilot.desktop
#   ~/.local/share/icons/hicolor/256x256/apps/deskpilot.png
#   ~/.local/share/icons/hicolor/128x128/apps/deskpilot.png
#
# Your settings (~/.config/DeskPilot) and logs (~/.local/share/DeskPilot) stay; the script says where they are.
set -eu

case "${1-}" in
  -h|--help)
    sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'
    exit 0
    ;;
esac

if [ -z "${HOME-}" ] || [ ! -d "$HOME" ]; then
  echo "uninstall.sh: HOME is not set to a folder." >&2
  exit 1
fi

have() { command -v "$1" >/dev/null 2>&1; }

bin_dir="$HOME/.local/bin"
data_dir="${XDG_DATA_HOME-}"
case $data_dir in /*) ;; *) data_dir="$HOME/.local/share" ;; esac
config_dir="${XDG_CONFIG_HOME-}"
case $config_dir in /*) ;; *) config_dir="$HOME/.config" ;; esac
apps_dir="$data_dir/applications"
icons_dir="$data_dir/icons/hicolor"

removed=0
for f in \
  "$bin_dir/deskpilot" \
  "$apps_dir/deskpilot.desktop" \
  "$icons_dir/256x256/apps/deskpilot.png" \
  "$icons_dir/128x128/apps/deskpilot.png"
do
  if [ -e "$f" ] || [ -L "$f" ]; then
    rm -f "$f"
    echo "Removed $f"
    removed=$((removed + 1))
  fi
done

if [ "$removed" -eq 0 ]; then
  echo "DeskPilot was not installed for $(id -un) (nothing to remove)."
else
  # Same cache refresh as install.sh: refresh caches that exist, never create one.
  if [ -f "$apps_dir/mimeinfo.cache" ] && have update-desktop-database; then
    update-desktop-database -q "$apps_dir" 2>/dev/null || true
  fi
  [ -d "$icons_dir" ] && touch "$icons_dir" 2>/dev/null || true
  if [ -f "$icons_dir/icon-theme.cache" ] && have gtk-update-icon-cache; then
    gtk-update-icon-cache -q -t -f "$icons_dir" 2>/dev/null || true
  fi
  echo "DeskPilot is uninstalled."
fi

kept=""
[ -d "$config_dir/DeskPilot" ] && kept="$kept
  $config_dir/DeskPilot (settings, API keys)"
[ -d "$data_dir/DeskPilot" ] && kept="$kept
  $data_dir/DeskPilot (logs)"
if [ -n "$kept" ]; then
  echo "Kept your DeskPilot data; delete these folders yourself if you no longer want them:$kept"
fi
