#!/usr/bin/env bash
# Runs the Linux test project inside a real (virtual) desktop session, so the X11 and Wayland code paths
# are exercised against real apps without any screen.
#   bash ci/run-linux-tests.sh x11      Xvfb + openbox + D-Bus session + AT-SPI bus
#   bash ci/run-linux-tests.sh wayland  headless sway (wlroots) + D-Bus session
# Every ci/session.d/*.sh is sourced inside the session just before the tests (modules add their own).
set -euo pipefail

session="${1:-x11}"
root="$(cd "$(dirname "$0")/.." && pwd)"
export CI_ROOT="$root" CI_LOGS="$root/ci-logs"
mkdir -p "$CI_LOGS"
export XDG_RUNTIME_DIR="$(mktemp -d)"
chmod 700 "$XDG_RUNTIME_DIR"
export DESKPILOT_TEST_SESSION="$session"
unset WAYLAND_DISPLAY DISPLAY XDG_SESSION_TYPE

cat > "$CI_LOGS/in-session.sh" <<'EOS'
#!/usr/bin/env bash
set -u
for f in "$CI_ROOT"/ci/session.d/*.sh; do
  # shellcheck disable=SC1090
  source "$f"
done
cd "$CI_ROOT"
dotnet test tests/DeskPilot.Linux.Tests -c Release --no-build \
  --logger "console;verbosity=normal" --logger "trx;LogFileName=linux-$DESKPILOT_TEST_SESSION.trx"
EOS
chmod +x "$CI_LOGS/in-session.sh"

case "$session" in
  x11)
    cat > "$CI_LOGS/x11-session.sh" <<'EOS'
#!/usr/bin/env bash
set -u
export XDG_SESSION_TYPE=x11
export XDG_CURRENT_DESKTOP=openbox
openbox >"$CI_LOGS/openbox.log" 2>&1 &
/usr/libexec/at-spi-bus-launcher --launch-immediately >"$CI_LOGS/at-spi.log" 2>&1 &
sleep 2
exec "$CI_LOGS/in-session.sh"
EOS
    chmod +x "$CI_LOGS/x11-session.sh"
    xvfb-run -a -s "-screen 0 1920x1080x24" dbus-run-session -- "$CI_LOGS/x11-session.sh"
    ;;
  wayland)
    cat > "$CI_LOGS/wayland-session.sh" <<'EOS'
#!/usr/bin/env bash
set -u
export WLR_BACKENDS=headless WLR_RENDERER=pixman WLR_LIBINPUT_NO_DEVICES=1 WLR_HEADLESS_OUTPUTS=1
export XDG_SESSION_TYPE=wayland XDG_CURRENT_DESKTOP=sway
sway -c "$CI_ROOT/ci/sway-headless.conf" >"$CI_LOGS/sway.log" 2>&1 &
sock=""
for _ in $(seq 1 50); do
  sock=$(ls "$XDG_RUNTIME_DIR" | grep -E '^wayland-[0-9]+$' | head -1 || true)
  [ -n "$sock" ] && break
  sleep 0.2
done
if [ -z "$sock" ]; then echo "sway did not start"; cat "$CI_LOGS/sway.log"; exit 1; fi
export WAYLAND_DISPLAY="$sock"
SWAYSOCK=$(ls "$XDG_RUNTIME_DIR"/sway-ipc.* 2>/dev/null | head -1 || true)
export SWAYSOCK
echo "Wayland display $WAYLAND_DISPLAY, sway ipc $SWAYSOCK"
exec "$CI_LOGS/in-session.sh"
EOS
    chmod +x "$CI_LOGS/wayland-session.sh"
    dbus-run-session -- "$CI_LOGS/wayland-session.sh"
    ;;
  *)
    echo "usage: $0 x11|wayland" >&2
    exit 2
    ;;
esac
