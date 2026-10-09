# X11 module: record which X extensions the test server offers (XTEST, RANDR, XFIXES, XKEYBOARD), for the CI log.
if [ "${DESKPILOT_TEST_SESSION:-}" = "x11" ] && command -v xdpyinfo >/dev/null 2>&1; then
  xdpyinfo -queryExtensions 2>/dev/null | grep -E "XTEST|RANDR|XFIXES|XKEYBOARD|MIT-SHM" | sed 's/^/x11 extension: /' || true
fi
