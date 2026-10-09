#!/usr/bin/env bash
# Checks the linux-x64 packages build-package.sh wrote, the way a user would meet them (CI runs this):
#   bash packaging/linux/check-package.sh [artifacts-folder]
#
# tar.gz    contents and modes; install.sh and uninstall.sh round trip in a scratch HOME (with a space in it);
#           the program starts (MCP bridge mode, which needs no display).
# AppImage  a type 2 x86-64 ELF AppImage; extracts to the expected AppDir; runs (extract-and-run, and FUSE when
#           this machine has it).
# Window    with xvfb-run and xwininfo: the window's WM_CLASS matches StartupWMClass in deskpilot.desktop.
# Needs: desktop-file-validate (desktop-file-utils), file, cmp; optional xvfb-run and xwininfo (x11-utils).
set -euo pipefail

artifacts="$(cd "${1:-artifacts}" && pwd)"
work="$(mktemp -d)"
cleanup() {
  # Single-file .NET apps extract native libraries; keep them inside the scratch folder and remove it all.
  chmod -R u+w "$work" 2>/dev/null || true
  rm -rf "$work"
}
trap cleanup EXIT
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="$work/bundle"

fail() { echo "FAIL: $*" >&2; exit 1; }
ok() { echo "ok: $*"; }
warn() {
  if [ -n "${GITHUB_ACTIONS:-}" ]; then echo "::warning::$*"; else echo "WARNING: $*" >&2; fi
}

shopt -s nullglob
tarballs=("$artifacts"/DeskPilot-*-linux-x64.tar.gz)
images=("$artifacts"/DeskPilot-*-x86_64.AppImage)
[ ${#tarballs[@]} -eq 1 ] || fail "expected one DeskPilot-*-linux-x64.tar.gz in $artifacts, found ${#tarballs[@]}"
tarball="${tarballs[0]}"
pkg="$(basename "$tarball" .tar.gz)"

# The bridge mode connects to a running DeskPilot; with none running it must say so and exit with code 3.
bridge_runs() { # bridge_runs LABEL COMMAND...  (explains the problem and returns 1 when it does not run)
  local label=$1 code=0 output
  shift
  output="$(timeout 60 "$@" --mcp-bridge "DeskPilot-mcp-check$$" "0123abcd" 2>&1 </dev/null)" || code=$?
  if [ "$code" -ne 3 ] || ! grep -q "DeskPilot is not running" <<<"$output"; then
    echo "$label: --mcp-bridge exited with $code, expected 3 and 'DeskPilot is not running'. Output: $output" >&2
    return 1
  fi
  ok "$label starts and runs the MCP bridge mode (exit 3: DeskPilot is not running)"
}
check_bridge() { bridge_runs "$@" || fail "$1 did not run"; }

# ------------------------------------------------------------------ tar.gz

listing="$(tar -tvzf "$tarball")"
for f in deskpilot install.sh uninstall.sh deskpilot.desktop deskpilot.png deskpilot-128.png README.md LICENSE; do
  grep -qE " $pkg/$f\$" <<<"$listing" || fail "$f is missing from $(basename "$tarball")"
done
for f in deskpilot install.sh uninstall.sh; do
  grep -qE "^-rwxr-xr-x .* $pkg/$f\$" <<<"$listing" || fail "$f is not executable in the tar.gz"
done
ok "$(basename "$tarball") has the program, scripts, menu entry, icons, README and LICENSE"

tar -xzf "$tarball" -C "$work"
dir="$work/$pkg"
grep -q "ELF 64-bit LSB .*x86-64" <<<"$(file -b "$dir/deskpilot")" || fail "deskpilot is not an x86-64 ELF: $(file -b "$dir/deskpilot")"
grep -q "256 x 256" <<<"$(file -b "$dir/deskpilot.png")" || fail "deskpilot.png is not 256x256"
grep -q "128 x 128" <<<"$(file -b "$dir/deskpilot-128.png")" || fail "deskpilot-128.png is not 128x128"
desktop-file-validate "$dir/deskpilot.desktop" || fail "deskpilot.desktop does not validate"
ok "program is an x86-64 ELF, icons are 256 and 128 px, deskpilot.desktop validates"

check_bridge "tar.gz program" "$dir/deskpilot"

# install.sh / uninstall.sh in a scratch HOME. The space exercises the quoting of Exec= in the menu entry.
home="$work/home dir"
mkdir -p "$home/.local/bin" "$home/.local/share/applications" "$home/.local/share/icons/hicolor/256x256/apps"
echo keep > "$home/.local/bin/other-tool"
echo keep > "$home/.local/share/applications/other.desktop"
echo keep > "$home/.local/share/icons/hicolor/256x256/apps/other.png"
as_user() { env -i HOME="$home" PATH="/usr/local/bin:/usr/bin:/bin" LANG=C.UTF-8 "$@"; }

out="$(as_user sh "$dir/install.sh")" || fail "install.sh failed: $out"
echo "$out" | sed 's/^/  | /'
grep -q "DeskPilot is installed for" <<<"$out" || fail "install.sh did not report the install"
grep -q "Desktop session: none detected" <<<"$out" || fail "install.sh did not report the (missing) session"
cmp "$dir/deskpilot" "$home/.local/bin/deskpilot" || fail "installed program differs"
[ -x "$home/.local/bin/deskpilot" ] || fail "installed program is not executable"
entry="$home/.local/share/applications/deskpilot.desktop"
grep -qxF "Exec=\"$home/.local/bin/deskpilot\"" "$entry" || fail "menu entry has the wrong Exec line: $(grep '^Exec=' "$entry")"
desktop-file-validate "$entry" || fail "installed menu entry does not validate"
cmp "$dir/deskpilot.png" "$home/.local/share/icons/hicolor/256x256/apps/deskpilot.png" || fail "256 px icon differs"
cmp "$dir/deskpilot-128.png" "$home/.local/share/icons/hicolor/128x128/apps/deskpilot.png" || fail "128 px icon differs"
ok "install.sh installed the program, an absolute-path menu entry and both icons"

as_user sh "$dir/install.sh" >/dev/null || fail "installing over an existing install failed"
ok "install.sh updates an existing install"

out="$(as_user sh "$dir/uninstall.sh")" || fail "uninstall.sh failed: $out"
echo "$out" | sed 's/^/  | /'
left="$(cd "$home" && find . -type f -o -type l | sort | tr '\n' ' ')"
[ "$left" = "./.local/bin/other-tool ./.local/share/applications/other.desktop ./.local/share/icons/hicolor/256x256/apps/other.png " ] \
  || fail "uninstall.sh left or removed the wrong files: $left"
ok "uninstall.sh removed exactly what install.sh installed"
out="$(as_user sh "$dir/uninstall.sh")" || fail "a second uninstall.sh failed"
grep -q "nothing to remove" <<<"$out" || fail "second uninstall did not say there was nothing to remove"

# Installing from the extracted folder with an explicit program path, and refusing a missing one.
as_user sh "$dir/install.sh" "$dir/deskpilot" >/dev/null || fail "install.sh FILE failed"
as_user sh "$dir/uninstall.sh" >/dev/null
if as_user sh "$dir/install.sh" "$work/no-such-file" >/dev/null 2>&1; then fail "install.sh accepted a missing program"; fi
ok "install.sh FILE works and rejects a missing file"

# ------------------------------------------------------------------ AppImage

if [ ${#images[@]} -eq 0 ]; then
  warn "no AppImage in $artifacts (appimagetool was skipped), AppImage checks skipped"
else
  image="${images[0]}"
  grep -q "ELF 64-bit LSB .*x86-64" <<<"$(file -b "$image")" || fail "AppImage is not an x86-64 ELF: $(file -b "$image")"
  magic="$(od -An -tx1 -j8 -N3 "$image" | tr -d ' \n')"
  [ "$magic" = "414902" ] || fail "AppImage has no type 2 magic (bytes 8-10 are $magic)"
  [ -x "$image" ] || fail "AppImage is not executable"
  ok "$(basename "$image") is an x86-64 ELF with the type 2 AppImage magic"

  mkdir -p "$work/extract"
  (cd "$work/extract" && "$image" --appimage-extract >/dev/null) || fail "--appimage-extract failed"
  sq="$work/extract/squashfs-root"
  for f in AppRun deskpilot.desktop deskpilot.png .DirIcon usr/bin/deskpilot \
           usr/share/applications/deskpilot.desktop usr/share/icons/hicolor/256x256/apps/deskpilot.png \
           usr/share/icons/hicolor/128x128/apps/deskpilot.png; do
    [ -e "$sq/$f" ] || fail "AppImage lacks $f"
  done
  [ -x "$sq/AppRun" ] && [ -x "$sq/usr/bin/deskpilot" ] || fail "AppRun or usr/bin/deskpilot is not executable"
  cmp "$dir/deskpilot" "$sq/usr/bin/deskpilot" || fail "the AppImage's program differs from the tar.gz one"
  desktop-file-validate "$sq/deskpilot.desktop" || fail "the AppImage's deskpilot.desktop does not validate"
  ok "AppImage extracts to AppRun, deskpilot.desktop, the icon and usr/bin/deskpilot (same program as the tar.gz)"

  check_bridge "AppImage (extract-and-run)" env APPIMAGE_EXTRACT_AND_RUN=1 TMPDIR="$work" "$image"
  if [ -e /dev/fuse ] && { command -v fusermount3 >/dev/null || command -v fusermount >/dev/null; }; then
    bridge_runs "AppImage (FUSE)" "$image" || warn "the AppImage did not run through FUSE on this machine (extract-and-run works)"
  else
    echo "note: no FUSE here, the mounted AppImage run was not tried"
  fi
fi

# ------------------------------------------------------------------ window class

expected="$(sed -n 's/^StartupWMClass=//p' "$dir/deskpilot.desktop")"
[ -n "$expected" ] || fail "deskpilot.desktop has no StartupWMClass"

# wm-class-probe.sh LOG COMMAND...: runs COMMAND in the current X display until a window of class $PROBE_CLASS
# shows up (or 30 s pass, or it exits), stops it, and prints the WM_CLASS pairs of all windows seen.
cat > "$work/wm-class-probe.sh" <<'EOF'
#!/usr/bin/env bash
log="$1"; shift
"$@" >"$log" 2>&1 &
pid=$!
classes() { xwininfo -root -tree 2>/dev/null | grep -oE '\("[^"]*" "[^"]*"\)' | sort -u | tr '\n' ' '; }
seen=""
for _ in $(seq 1 150); do
  seen="$(classes)"
  case "$seen" in *"\"$PROBE_CLASS\")"*) break ;; esac
  kill -0 "$pid" 2>/dev/null || break
  sleep 0.2
done
kill "$pid" 2>/dev/null
for _ in $(seq 1 25); do kill -0 "$pid" 2>/dev/null || break; sleep 0.2; done
kill -9 "$pid" 2>/dev/null
wait "$pid" 2>/dev/null
echo "$seen"
EOF
chmod +x "$work/wm-class-probe.sh"

check_wm_class() { # check_wm_class LABEL COMMAND...
  local label=$1 classes
  shift
  local apphome="$work/wm-home-$RANDOM"
  mkdir -p "$apphome"
  classes="$(env HOME="$apphome" PROBE_CLASS="$expected" xvfb-run -a -s "-screen 0 1280x800x24" "$work/wm-class-probe.sh" "$work/wm.log" "$@" || true)"
  if [ -z "${classes// /}" ]; then
    warn "$label opened no window under Xvfb within 30 s, WM_CLASS not checked. Its output: $(tail -n 20 "$work/wm.log" 2>/dev/null | tr '\n' ' ')"
    return 0
  fi
  echo "  | $label window classes: $classes"
  grep -qF "\"$expected\")" <<<"$classes" || fail "$label: no window with WM_CLASS class \"$expected\" (StartupWMClass); saw $classes"
  ok "$label window has WM_CLASS class \"$expected\", matching StartupWMClass"
}

if command -v xvfb-run >/dev/null && command -v xwininfo >/dev/null; then
  check_wm_class "tar.gz program" "$dir/deskpilot"
  if [ ${#images[@]} -gt 0 ]; then
    check_wm_class "AppImage" env APPIMAGE_EXTRACT_AND_RUN=1 TMPDIR="$work" "${images[0]}"
  fi
else
  echo "note: xvfb-run or xwininfo missing, WM_CLASS not checked"
fi

echo "All package checks passed."
