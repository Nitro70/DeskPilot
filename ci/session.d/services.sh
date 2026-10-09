# Linux services module: accessibility for the AT-SPI tests (sourced inside the session; must not block).
# GTK 3 loads the AT-SPI bridge (libatk-adaptor) unless NO_AT_BRIDGE=1; GTK 4 speaks AT-SPI itself.
unset NO_AT_BRIDGE
export GTK_A11Y=atspi
# Qt only publishes its tree when asked to.
export QT_LINUX_ACCESSIBILITY_ALWAYS_ON=1
echo "services: accessibility bus: $(timeout 5 dbus-send --session --print-reply --dest=org.a11y.Bus /org/a11y/bus org.a11y.Bus.GetAddress 2>&1 | tail -1)"
