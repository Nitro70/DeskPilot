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

portal_dir=/usr/share/xdg-desktop-portal/portals

# True when an installed xdg-desktop-portal backend (gnome, kde, cosmic...) implements the interface.
portal_has() {
  _backend=$1 _iface=$2
  [ -f "$portal_dir/$_backend.portal" ] && grep -q "org.freedesktop.impl.portal.$_iface" "$portal_dir/$_backend.portal"
}

have_portal_service() { [ -f /usr/share/dbus-1/services/org.freedesktop.portal.Desktop.service ]; }

have_atspi() {
  [ -f /usr/share/dbus-1/services/org.a11y.Bus.service ] && return 0
  for _f in /usr/libexec/at-spi-bus-launcher /usr/lib/at-spi2-core/at-spi-bus-launcher /usr/lib/at-spi-bus-launcher /usr/libexec/at-spi2/at-spi-bus-launcher; do
    [ -x "$_f" ] && return 0
  done
  return 1
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
    debian:libx11) echo libx11-6 ;;      fedora:libx11) echo libX11 ;;      arch:libx11) echo libx11 ;;      suse:libx11) echo libX11-6 ;;
    debian:libxtst) echo libxtst6 ;;     fedora:libxtst) echo libXtst ;;    arch:libxtst) echo libxtst ;;    suse:libxtst) echo libXtst6 ;;
    debian:libxrandr) echo libxrandr2 ;; fedora:libxrandr) echo libXrandr ;; arch:libxrandr) echo libxrandr ;; suse:libxrandr) echo libXrandr2 ;;
    debian:libxfixes) echo libxfixes3 ;; fedora:libxfixes) echo libXfixes ;; arch:libxfixes) echo libxfixes ;; suse:libxfixes) echo libXfixes3 ;;
    debian:libxi) echo libxi6 ;;         fedora:libxi) echo libXi ;;        arch:libxi) echo libxi ;;        suse:libxi) echo libXi6 ;;
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
  _keys="" _names=""
  for _l in libx11:libX11.so.6 libxtst:libXtst.so.6 libxrandr:libXrandr.so.2 libxfixes:libXfixes.so.3 libxi:libXi.so.6; do
    if ! have_lib "${_l#*:}"; then
      _keys="$_keys ${_l%%:*}"
      _names="$_names ${_l#*:}"
    fi
  done
  # shellcheck disable=SC2086
  [ -z "$_keys" ] || need "Screenshots, mouse and keyboard on X11 need the X libraries$_names." $_keys
  if ! have xclip && ! have xsel; then need "The clipboard tools need xclip (or xsel)." xclip; fi
}

check_wayland() {
  if desktop_is sway Hyprland river wayfire labwc niri; then
    have grim || need "Screenshots need grim." grim
    have wtype || need "Typing and key presses need wtype." wtype
    if desktop_is sway; then
      have swaymsg || need "Windows and the mouse on sway need swaymsg (part of sway)." sway
    elif desktop_is Hyprland; then
      have hyprctl || need "Windows and the mouse on Hyprland need hyprctl (part of Hyprland)." hyprland
    elif ! have wlrctl && ! have ydotool && ! have dotool; then
      need "The mouse needs wlrctl (or ydotool or dotool)." wlrctl
    fi
  else
    if desktop_is GNOME Unity ubuntu; then _backend=gnome
    elif desktop_is KDE plasma; then _backend=kde
    elif desktop_is COSMIC; then _backend=cosmic
    else _backend=""
    fi
    have_portal_service || need "Screenshots and input on this desktop go through xdg-desktop-portal, which is not installed." portal
    if [ -n "$_backend" ]; then
      portal_has "$_backend" Screenshot || need "Screenshots need the $_backend portal backend (xdg-desktop-portal-$_backend)." "portal-$_backend"
      if ! portal_has "$_backend" RemoteDesktop && ! have ydotool && ! have dotool; then
        need "Mouse and keyboard need the remote-desktop portal of xdg-desktop-portal-$_backend, or ydotool (or dotool)." ydotool
      fi
    elif ! have ydotool && ! have dotool; then
      need "Mouse and keyboard on this compositor need ydotool (or dotool)." ydotool
    fi
    hint "The first screenshot shows a permission prompt from the desktop, and the first mouse or keyboard action asks once to allow remote control."
  fi
  if ! have wl-copy || ! have wl-paste; then need "The clipboard tools need wl-clipboard (wl-copy, wl-paste)." wl-clipboard; fi
  if have ydotool || have dotool; then
    hint "ydotool needs its daemon (ydotoold) running and dotool needs access to /dev/uinput (usually the input group)."
  fi
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
  [ "$_kind" = none ] || have_atspi || need "Reading buttons and fields (ui_elements) needs the AT-SPI accessibility bus." atspi

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
