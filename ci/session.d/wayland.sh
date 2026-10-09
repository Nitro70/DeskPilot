# Wayland layer diagnostics, sourced inside the test session (bash). Prints what the compositor offers; never blocks.
if [ "${DESKPILOT_TEST_SESSION:-}" = "wayland" ]; then
  echo "wayland: sway $(sway --version 2>/dev/null || echo '?'), grim $(command -v grim || echo missing), wtype $(command -v wtype || echo missing), swaybg $(command -v swaybg || echo missing)"
  swaymsg -t get_seats > "${CI_LOGS:-/tmp}/sway-seats.json" 2>&1 || true
  swaymsg -t get_outputs > "${CI_LOGS:-/tmp}/sway-outputs.json" 2>&1 || true
fi
