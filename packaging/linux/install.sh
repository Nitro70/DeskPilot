#!/bin/sh
# Installs DeskPilot for the current user. No root, no sudo: everything goes into your home folder.
#
#   ~/.local/bin/deskpilot                                    the program
#   ~/.local/share/applications/deskpilot.desktop             the menu entry (with the absolute program path)
#   ~/.local/share/icons/hicolor/256x256/apps/deskpilot.png   the icon
#   ~/.local/share/icons/hicolor/128x128/apps/deskpilot.png
#
# Then it lists the helper packages your desktop session still needs. uninstall.sh removes exactly these files.
#
#   ./install.sh              install the deskpilot program that sits next to this script
#   ./install.sh FILE         install FILE as the program instead (for example your own dotnet publish output)
#   ./install.sh --check      only list the helper packages this session needs, install nothing
set -eu

here=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P)

if [ -z "${HOME-}" ] || [ ! -d "$HOME" ]; then
  echo "install.sh: HOME is not set to a folder." >&2
  exit 1
fi

bin_dir="$HOME/.local/bin"
data_dir="${XDG_DATA_HOME-}"
case $data_dir in /*) ;; *) data_dir="$HOME/.local/share" ;; esac
config_dir="${XDG_CONFIG_HOME-}"
case $config_dir in /*) ;; *) config_dir="$HOME/.config" ;; esac
apps_dir="$data_dir/applications"
icons_dir="$data_dir/icons/hicolor"

# ------------------------------------------------------------------ helper checks
# The same checks as LinuxDesktopFactory.DescribeMissingTools in the app (keep them in step): which session this
# is, then which tools that session's route needs.

have() { command -v "$1" >/dev/null 2>&1; }

lower() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }

trim() { printf '%s' "$1" | sed 's/^[[:space:]]*//; s/[[:space:]]*$//'; }

# x11, wayland or none, like LinuxSession.Detect: DESKPILOT_SESSION overrides, then XDG_SESSION_TYPE, then the
# display variables (WAYLAND_DISPLAY wins over an Xwayland DISPLAY).
session_kind() {
  _display=$(trim "${DISPLAY-}")
  _wayland=$(trim "${WAYLAND_DISPLAY-}")
  _forced=$(lower "$(trim "${DESKPILOT_SESSION-}")")
  _type=$(lower "$(trim "${XDG_SESSION_TYPE-}")")
  case $_forced in x11|wayland) echo "$_forced"; return ;; esac
  if [ "$_type" = wayland ] && { [ -n "$_wayland" ] || [ -z "$_display" ]; }; then echo wayland; return; fi
  if [ "$_type" = x11 ] && [ -n "$_display" ]; then echo x11; return; fi
  if [ -n "$_wayland" ]; then echo wayland; elif [ -n "$_display" ]; then echo x11; else echo none; fi
}

# True when XDG_CURRENT_DESKTOP (a colon-separated list) names one of the arguments, ignoring case.
desktop_is() {
  _list=$(lower "$(trim "${XDG_CURRENT_DESKTOP-}")")
  [ -n "$_list" ] || return 1
  for _want in "$@"; do
    _want=$(lower "$_want")
    case ":$_list:" in *":$_want:"*) return 0 ;; esac
  done
  return 1
}

# True when the shared library (e.g. libXtst.so.6) can be loaded.
have_lib() {
  for _ldc in ldconfig /sbin/ldconfig /usr/sbin/ldconfig; do
    if have "$_ldc"; then
      if "$_ldc" -p 2>/dev/null | awk -v lib="$1" '$1 == lib { found = 1 } END { exit !found }'; then return 0; fi
      break
    fi
  done
  for _dir in /usr/lib/x86_64-linux-gnu /usr/lib/aarch64-linux-gnu /usr/lib64 /usr/lib /lib/x86_64-linux-gnu /lib/aarch64-linux-gnu /lib64 /lib; do
    [ -e "$_dir/$1" ] && return 0
  done
  return 1
}

# True when a session-bus name is owned now or can be activated (its D-Bus service file is installed).
have_bus_name() {
  for _d in "$data_dir/dbus-1/services" /usr/local/share/dbus-1/services /usr/share/dbus-1/services; do
    [ -f "$_d/$1.service" ] && return 0
  done
  if have busctl && busctl --user --no-pager list 2>/dev/null | awk -v n="$1" '$1 == n { found = 1 } END { exit !found }'; then
    return 0
  fi
  if have dbus-send && dbus-send --session --print-reply --dest=org.freedesktop.DBus /org/freedesktop/DBus \
      org.freedesktop.DBus.ListNames 2>/dev/null | grep -qF "\"$1\""; then
    return 0
  fi
  return 1
}

# The Wayland desktop, like WaylandContext.DetectDesktop: XDG_CURRENT_DESKTOP first, then the variables each
# compositor sets for its session.
wayland_desktop() {
  if desktop_is Hyprland; then echo hyprland
  elif desktop_is sway; then echo sway
  elif desktop_is river wayfire labwc niri; then echo wlroots
  elif desktop_is KDE plasma; then echo kde
  elif desktop_is GNOME Unity ubuntu; then echo gnome
  elif desktop_is COSMIC; then echo cosmic
  elif [ -n "${HYPRLAND_INSTANCE_SIGNATURE-}" ]; then echo hyprland
  elif [ -n "${SWAYSOCK-}" ]; then echo sway
  elif [ -n "${KDE_FULL_SESSION-}" ]; then echo kde
  elif [ -n "${GNOME_SETUP_DISPLAY-}" ] || [ -n "${GNOME_SHELL_SESSION_MODE-}" ]; then echo gnome
  else echo unknown
  fi
}

# yes, no, or unknown when the compositor's globals cannot be listed (wayland-info is not installed).
wayland_has_global() {
  have wayland-info || { echo unknown; return; }
  _globals=$(wayland-info 2>/dev/null) || { echo unknown; return; }
  [ -n "$_globals" ] || { echo unknown; return; }
  case $_globals in *"$1"*) echo yes ;; *) echo no ;; esac
}

# True when the GNOME Shell extension "Window Calls" answers on the session bus.
have_gnome_window_calls() {
  have gdbus || return 1
  gdbus introspect --session --dest org.gnome.Shell --object-path /org/gnome/Shell/Extensions/Windows 2>/dev/null |
    grep -qF org.gnome.Shell.Extensions.Windows
}

distro_family() {
  _ids=""
  [ -r /etc/os-release ] && _ids=$(sed -n 's/^ID=//p; s/^ID_LIKE=//p' /etc/os-release | tr -d "\"'" | tr '\n' ' ')
  for _id in $_ids; do
    case $_id in
      debian|ubuntu) echo debian; return ;;
      fedora|rhel|centos) echo fedora; return ;;
      arch|archlinux) echo arch; return ;;
      opensuse*|suse|sles) echo suse; return ;;
    esac
  done
  echo other
}

# Package name of a helper for a distribution family.
pkg() {
  case "$1:$2" in
    debian:libxtst) echo libxtst6 ;;     fedora:libxtst) echo libXtst ;;    arch:libxtst) echo libxtst ;;    suse:libxtst) echo libXtst6 ;;
    debian:libxrandr) echo libxrandr2 ;; fedora:libxrandr) echo libXrandr ;; arch:libxrandr) echo libxrandr ;; suse:libxrandr) echo libXrandr2 ;;
    *:portal) echo xdg-desktop-portal ;;
    *:portal-gnome) echo xdg-desktop-portal-gnome ;;
    *:portal-kde) echo xdg-desktop-portal-kde ;;
    *:portal-cosmic) echo xdg-desktop-portal-cosmic ;;
    *:atspi) echo at-spi2-core ;;
    *) echo "$2" ;;
  esac
}

missing_notes=""
missing_keys=""
hint_notes=""

need() { # need "sentence" key...
  missing_notes="$missing_notes
  - $1"
  shift
  for _k in "$@"; do
    case " $missing_keys " in *" $_k "*) ;; *) missing_keys="$missing_keys $_k" ;; esac
  done
}

hint() {
  hint_notes="$hint_notes
  - $1"
}

check_x11() {
  have_lib libXtst.so.6 || have_lib libXtst.so || need "Mouse and keyboard control on X11 needs libXtst." libxtst
  have_lib libXrandr.so.2 || have_lib libXrandr.so || need "Detecting monitors on X11 needs libXrandr." libxrandr
  if ! have xclip && ! have xsel; then need "Clipboard access on X11 needs xclip or xsel." xclip; fi
}

check_wayland() {
  _wd=$(wayland_desktop)
  case $_wd in
    sway) _name=sway ;;
    hyprland) _name=Hyprland ;;
    gnome) _name=GNOME ;;
    kde) _name="KDE Plasma" ;;
    cosmic) _name=COSMIC ;;
    *) _name=$(trim "${XDG_CURRENT_DESKTOP-}"); [ -n "$_name" ] || _name="this Wayland desktop" ;;
  esac

  case $_wd in
    sway|hyprland|wlroots)
      have grim || need "Screenshots on $_name need grim." grim
      have wtype || need "Typing and key presses on $_name need wtype." wtype
      if [ "$_wd" = sway ] && ! have swaymsg && [ -z "${SWAYSOCK-}" ]; then
        need "Listing and focusing windows on sway needs swaymsg (part of the sway package) or the SWAYSOCK variable sway sets for its session."
      fi
      if [ "$_wd" = hyprland ] && ! have hyprctl; then
        need "Listing and focusing windows on Hyprland needs hyprctl, which comes with Hyprland. Make sure it is on PATH."
      fi
      if [ "$(wayland_has_global zwlr_virtual_pointer_manager_v1)" = no ] && ! have ydotool && ! have dotool; then
        need "$_name does not offer the virtual pointer protocol, so mouse control needs ydotool with ydotoold running." ydotool
      fi
      ;;
    *)
      if ! have_bus_name org.freedesktop.portal.Desktop; then
        case $_wd in kde) _backend=portal-kde ;; cosmic) _backend=portal-cosmic ;; *) _backend=portal-gnome ;; esac
        need "Screenshots, mouse and keyboard on $_name go through xdg-desktop-portal, which is not running. Install it with your desktop's backend. For mouse and keyboard, ydotool with ydotoold running also works." portal "$_backend"
      fi
      if [ "$_wd" = kde ] && ! have kdotool; then
        hint "Optional: kdotool lets DeskPilot list and focus windows on KDE Plasma (cargo install kdotool, or the kdotool package from the AUR on Arch). Without it DeskPilot works from screenshots only."
      fi
      if [ "$_wd" = gnome ] && ! have_gnome_window_calls; then
        hint "Optional: the GNOME Shell extension \"Window Calls\" (extensions.gnome.org) lets DeskPilot list and focus windows on GNOME. Without it DeskPilot works from screenshots only."
      fi
      hint "The first screenshot shows a permission prompt from the desktop, and the first mouse or keyboard action asks once to allow remote control."
      ;;
  esac

  if ! have wl-copy || ! have wl-paste; then need "Clipboard access on Wayland needs wl-clipboard." wl-clipboard; fi
  # Not a DescribeMissingTools check: the app itself needs X11, so this only matters before it first runs.
  if [ -z "$(trim "${DISPLAY-}")" ]; then
    hint "DeskPilot's own window uses X11 through Xwayland, and DISPLAY is not set in this session: enable Xwayland in your compositor."
  fi
}

report_session() {
  _kind=$(session_kind)
  _desktop=$(trim "${XDG_CURRENT_DESKTOP-}")
  case $_kind in
    x11) _label="X11" ;;
    wayland) _label="Wayland" ;;
    *) _label="none detected" ;;
  esac
  echo "Desktop session: $_label${_desktop:+ ($_desktop)}"

  case $_kind in
    x11) check_x11 ;;
    wayland) check_wayland ;;
    *)
      echo "No graphical session found (DISPLAY and WAYLAND_DISPLAY are empty). Run ./install.sh --check from a"
      echo "terminal inside your desktop session to see which helper packages it needs."
      ;;
  esac
  if [ "$_kind" != none ] && ! have_bus_name org.a11y.Bus; then
    need "Reading buttons and fields of windows (ui_elements) needs the accessibility bus from at-spi2-core, which is not running. Install it and log in again." atspi
  fi

  if [ -n "$missing_notes" ]; then
    echo "Missing helper tools:$missing_notes"
    _family=$(distro_family)
    _pkgs=""
    for _k in $missing_keys; do _pkgs="$_pkgs $(pkg "$_family" "$_k")"; done
    case $_family in
      debian) echo "Install them with:"; echo "  sudo apt install$_pkgs" ;;
      fedora) echo "Install them with:"; echo "  sudo dnf install$_pkgs" ;;
      arch) echo "Install them with:"; echo "  sudo pacman -S --needed$_pkgs" ;;
      suse) echo "Install them with:"; echo "  sudo zypper install$_pkgs" ;;
      *) echo "Install these packages with your package manager:$_pkgs" ;;
    esac
  elif [ "$_kind" != none ]; then
    echo "All helper tools for this session are installed."
  fi
  [ -z "$hint_notes" ] || echo "Good to know:$hint_notes"

  if ! have claude && [ ! -x "$HOME/.local/bin/claude" ] && [ ! -x "$HOME/.claude/local/claude" ]; then
    echo "Claude Code (the default model provider) was not found. Install it from https://claude.com/claude-code and"
    echo "run 'claude' once to log in, or pick another provider in DeskPilot's settings."
  fi
}

# ------------------------------------------------------------------ install

# Exec value for a desktop entry: quoted and escaped when the path needs it (Desktop Entry spec, Exec key).
desktop_exec() {
  case $1 in
    *[!A-Za-z0-9/._+@,:=-]*)
      printf '"%s"' "$(printf '%s' "$1" | sed -e 's/\\/\\\\\\\\/g' -e 's/["`$]/\\\\&/g' -e 's/%/%%/g')" ;;
    *) printf '%s' "$1" ;;
  esac
}

find_program() {
  if [ -f "$here/deskpilot" ]; then echo "$here/deskpilot"; return 0; fi
  # Run from a source checkout: use the dotnet publish output for this machine.
  case $(uname -m) in
    x86_64|amd64) _rid=linux-x64 ;;
    aarch64|arm64) _rid=linux-arm64 ;;
    *) _rid="" ;;
  esac
  if [ -n "$_rid" ] && [ -f "$here/../../bin/publish-$_rid/deskpilot" ]; then
    (CDPATH='' cd -- "$here/../../bin/publish-$_rid" && printf '%s/deskpilot\n' "$(pwd -P)")
    return 0
  fi
  return 1
}

case "${1-}" in
  -h|--help)
    sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'
    exit 0
    ;;
  --check)
    report_session
    exit 0
    ;;
esac

if [ "$(id -u)" -eq 0 ]; then
  echo "install.sh installs DeskPilot for one user into their home folder. Run it as that user, without sudo." >&2
  exit 1
fi

if [ $# -gt 0 ]; then
  program=$1
  [ -f "$program" ] || { echo "install.sh: $program is not a file." >&2; exit 1; }
else
  program=$(find_program) || {
    echo "install.sh: no deskpilot program next to this script. Run it from the extracted DeskPilot folder," >&2
    echo "or pass the program's path: ./install.sh path/to/deskpilot" >&2
    exit 1
  }
fi

for f in deskpilot.desktop deskpilot.png; do
  [ -f "$here/$f" ] || { echo "install.sh: $f is missing next to this script." >&2; exit 1; }
done

mkdir -p "$bin_dir" "$apps_dir" "$icons_dir/256x256/apps" "$icons_dir/128x128/apps"

# Copy then rename, so updating works while DeskPilot is running (no "text file busy").
tmp="$bin_dir/.deskpilot.install.$$"
trap 'rm -f "$tmp" "$apps_dir/.deskpilot.desktop.$$"' EXIT
cp "$program" "$tmp"
chmod 755 "$tmp"
mv -f "$tmp" "$bin_dir/deskpilot"

exec_value=$(desktop_exec "$bin_dir/deskpilot")
while IFS= read -r line || [ -n "$line" ]; do
  case $line in
    Exec=*) printf 'Exec=%s\n' "$exec_value" ;;
    *) printf '%s\n' "$line" ;;
  esac
done < "$here/deskpilot.desktop" > "$apps_dir/.deskpilot.desktop.$$"
chmod 644 "$apps_dir/.deskpilot.desktop.$$"
mv -f "$apps_dir/.deskpilot.desktop.$$" "$apps_dir/deskpilot.desktop"

cp "$here/deskpilot.png" "$icons_dir/256x256/apps/deskpilot.png"
icons="$icons_dir/256x256/apps/deskpilot.png"
if [ -f "$here/deskpilot-128.png" ]; then
  cp "$here/deskpilot-128.png" "$icons_dir/128x128/apps/deskpilot.png"
  icons="$icons, $icons_dir/128x128/apps/deskpilot.png"
fi
chmod 644 "$icons_dir/256x256/apps/deskpilot.png" "$icons_dir/128x128/apps/deskpilot.png" 2>/dev/null || true

# Refresh the menu and icon caches where those tools exist. Caches are refreshed, never created: a cache file
# left in the user's folders would be one more file to clean up, and a stale icon cache hides icons other
# programs install later. Menus and icon themes notice new files without a cache anyway.
refreshed=""
if [ -f "$apps_dir/mimeinfo.cache" ] && have update-desktop-database && update-desktop-database -q "$apps_dir" 2>/dev/null; then
  refreshed="$refreshed update-desktop-database"
fi
touch "$icons_dir" 2>/dev/null || true
if [ -f "$icons_dir/icon-theme.cache" ] && have gtk-update-icon-cache && gtk-update-icon-cache -q -t -f "$icons_dir" 2>/dev/null; then
  refreshed="$refreshed gtk-update-icon-cache"
fi

echo "DeskPilot is installed for $(id -un):"
echo "  program     $bin_dir/deskpilot"
echo "  menu entry  $apps_dir/deskpilot.desktop"
echo "  icons       $icons"
[ -z "$refreshed" ] || echo "  refreshed  $refreshed"
case ":${PATH-}:" in
  *":$bin_dir:"*) echo "Start it from your app menu, or run: deskpilot" ;;
  *) echo "Start it from your app menu, or run: $bin_dir/deskpilot ($bin_dir is not on your PATH)" ;;
esac
echo "Settings are kept in $config_dir/DeskPilot and logs in $data_dir/DeskPilot/logs."
echo "To remove DeskPilot again, run uninstall.sh."
echo
report_session
