# Sourced inside the test session before the tests run (bash). Each module may add its own ci/session.d/<module>.sh.
echo "session: ${DESKPILOT_TEST_SESSION:-?} DISPLAY=${DISPLAY:-} WAYLAND_DISPLAY=${WAYLAND_DISPLAY:-}"
