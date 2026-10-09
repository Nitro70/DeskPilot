#!/usr/bin/env bash
# Runs the Linux test project inside a real (virtual) desktop session, so the X11 and Wayland code paths
# are exercised against real apps without any screen.
#   bash ci/run-linux-tests.sh x11      Xvfb + openbox + D-Bus session + AT-SPI
#   bash ci/run-linux-tests.sh wayland  headless sway (wlroots) + D-Bus session
set -euo pipefail

session="${1:-x11}"
root="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$root/ci-logs"
export XDG_RUNTIME_DIR="$(mktemp -d)"
chmod 700 "$XDG_RUNTIME_DIR"
export DESKPILOT_TEST_SESSION="$session"
unset WAYLAND_DISPLAY DISPLAY XDG_SESSION_TYPE

test_cmd=(dotnet test "$root/tests/DeskPilot.Linux.Tests" -c Release --no-build
  --logger "console;verbosity=normal" --logger "trx;LogFileName=linux-$session.trx")

case "$session" in
  x11)
    cat > "$root/ci-logs/x11-session.sh" <<'EOS'
#!/usr/bin/env bash
set -u
export XDG_SESSION_TYPE=x11
export XDG_CURRENT_DESKTOP=openbox
openbox >"$CI_LOGS/openbox.log" 2>&1 &
# Accessibility bus for AT-SPI tests.
/usr/libexec/at-spi-bus-launcher --launch-immediately >"$CI_LOGS/at-spi.log" 2>&1 &
sleep 2
"$@"
EOS
    chmod +x "$root/ci-logs/x11-session.sh"
    export CI_LOGS="$root/ci-logs"
    xvfb-run -a -s "-screen 0 1920x1080x24" dbus-run-session -- "$root/ci-logs/x11-session.sh" "${test_cmd[@]}"
    ;;
  wayland)
    conf="$root/ci/sway-headless.conf"
    export WLR_BACKENDS=headless WLR_RENDERER=pixman WLR_LIBINPUT_NO_DEVICES=1 WLR_HEADLESS_OUTPUTS=1
    export XDG_SESSION_TYPE=wayland XDG_CURRENT_DESKTOP=sway
    dbus-run-session -- bash -c '
      sway -c "$0" >"$1/sway.log" 2>&1 &
      for i in $(seq 1 50); do
        sock=$(ls "$XDG_RUNTIME_DIR" | grep -E "^wayland-[0-9]+$" | head -1 || true)
        [ -n "$sock" ] && break
        sleep 0.2
      done
      if [ -z "${sock:-}" ]; then echo "sway did not start"; cat "$1/sway.log"; exit 1; fi
      export WAYLAND_DISPLAY="$sock"
      export SWAYSOCK=$(ls "$XDG_RUNTIME_DIR"/sway-ipc.* 2>/dev/null | head -1)
      echo "Wayland display $WAYLAND_DISPLAY, sway ipc $SWAYSOCK"
      shift 2
      "$@"
    ' "$conf" "$root/ci-logs" "${test_cmd[@]}"
    ;;
  *)
    echo "usage: $0 x11|wayland" >&2
    exit 2
    ;;
esac
