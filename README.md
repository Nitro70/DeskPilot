# DeskPilot

DeskPilot lets an AI model use your Windows or Linux PC for you. Tell it what you want done; it looks at the
screen, then clicks, types, scrolls and opens apps the way you would, while you watch (and can stop it
at any moment).

By default it runs on **your own Claude subscription through the Claude Code CLI**: no API key, no
extra account, no third-party service. You can switch to almost any other model: the Anthropic API,
OpenAI, OpenRouter, Google Gemini, Groq, xAI, DeepSeek, Mistral, Together, Azure OpenAI, local models
in Ollama or LM Studio, any OpenAI-compatible server, or agents that speak the Agent Client Protocol
such as Gemini CLI.

It is a native Windows app (WPF, .NET 10) shipped as one self-contained `DeskPilot.exe`. The Linux
version (Avalonia, .NET 10, X11 and Wayland) ships as an AppImage and as a tar.gz with an install
script; see [Linux](#linux).

## Features

- **Computer control**: screenshots, click / double-click / right-click, drag, scroll, typing (any
  Unicode), key combinations, window switching, launching apps, files, folders and URLs, reading UI
  elements through UI Automation for precise clicks, zooming into small text, clipboard, and (opt-in)
  shell commands.
- **Your Claude subscription by default**: talks to the `claude` CLI you are already logged in to,
  with Claude Haiku as the default model (the `haiku` alias, the newest Haiku). Pick Sonnet, Opus,
  Fable or any model id from the model box.
- **Model choice**: per-profile provider, model, API key (stored encrypted with Windows DPAPI, or read
  from an environment variable), base URL, headers, extra request JSON. Model lists are fetched live.
- **Thinking controls**: Auto / On / Off plus an effort level, translated to what each provider
  understands (Claude Code effort and thinking tokens, Anthropic adaptive or budgeted thinking, OpenAI
  `reasoning_effort`, OpenRouter `reasoning`, Ollama `think`).
- **Local models**: Ollama and LM Studio are detected automatically. Vision models are marked; for
  text-only models DeskPilot describes the screen through UI Automation instead of screenshots. An
  optional coordinate grid and 0-1000 coordinates help smaller vision models click accurately.
- **Optional notes vault**: point DeskPilot at a folder of notes (an Obsidian vault works well). The
  model does not read it up front; it searches it only when a request needs something written there,
  and reads just the relevant lines.
- **Safety first**: no administrator actions unless you turn Administrator mode on, a global stop
  hotkey (Ctrl+Alt+X by default), a failsafe corner (slam the mouse into the top-left corner), optional
  confirmation before risky or all actions, a dry-run mode, blocked apps, and a per-request action
  limit. DeskPilot's own windows are hidden from its screenshots so the model never clicks them.
- **Advanced mode**: edit the full computer-use system prompt (with placeholders), extra CLI arguments
  and environment, raw request JSON.
- **Auto-detection**: finds the Claude Code CLI and its login state, Gemini CLI, Ollama, LM Studio,
  API keys in your environment, your Obsidian vaults and your monitors.

## Getting started

1. Install [Claude Code](https://claude.com/claude-code) and log in once (`claude auth login`), or
   plan to use another provider.
2. Download `DeskPilot.exe` from the Releases page and run it. No installer is needed.
3. The first-run window shows what was detected and lets you pick an optional notes vault.
4. Type a request, for example "open Notepad and write a shopping list for tacos", and press Enter.

Stop the agent at any time with the Stop button, the stop hotkey, the tray icon, or by moving the
mouse into the top-left corner of the main screen.

## Linux

The Linux version is the same app with an Avalonia UI: the same model providers, tools, safety rules,
vault support and settings. It runs on 64-bit x86 and ARM distributions with glibc (the usual desktop
ones). UI elements are read through AT-SPI (the Linux accessibility bus) instead of UI Automation, and
API keys are encrypted with AES-GCM and a per-user key file instead of DPAPI.

### Installing

Download one of these from the Releases page:

- **`DeskPilot-<version>-x86_64.AppImage`**: make it executable and run it. Nothing gets installed.

  ```
  chmod +x DeskPilot-*-x86_64.AppImage
  ./DeskPilot-*-x86_64.AppImage
  ```

  AppImages mount themselves with FUSE. If yours does not start, install your distribution's `fuse3`
  package, or run it with `--appimage-extract-and-run`.

- **`DeskPilot-<version>-linux-x64.tar.gz`** (or `linux-arm64`): extract it and run `install.sh`.

  ```
  tar -xzf DeskPilot-*-linux-x64.tar.gz
  cd DeskPilot-*-linux-x64
  ./install.sh
  ```

  It installs for your user only, without sudo: the program as `~/.local/bin/deskpilot`, a menu entry
  in `~/.local/share/applications` and the icon in `~/.local/share/icons`. Then it checks your desktop
  session and prints the helper packages that are still missing, with the command to install them.
  `./install.sh --check` prints only that report; `./uninstall.sh` removes exactly what `install.sh`
  installed and leaves your settings alone.

### Desktop sessions

| Session | Support | How DeskPilot sees the screen and acts |
|---|---|---|
| X11, any desktop | Recommended, fully supported | Directly through X11 (Xlib and XTest), no prompts, global stop hotkey |
| Wayland: sway, Hyprland and other wlroots compositors | Supported | `grim` for screenshots, `wtype` for the keyboard, `swaymsg` / `hyprctl` for windows and the mouse |
| Wayland: GNOME, KDE Plasma, COSMIC | Supported, with prompts | `xdg-desktop-portal`: the desktop asks your permission for screenshots, and once to allow remote control of the mouse and keyboard. `ydotool` or `dotool` also work for input |

Wayland deliberately makes it hard for one program to watch and control others, so that route needs
helpers and approvals, and it has no global hotkeys. If your desktop still offers an X11 session at the
login screen, that is the smoothest choice. On Wayland desktops DeskPilot's own window runs through
Xwayland, which GNOME, KDE Plasma and sway provide by default.

### Helper packages

`install.sh` tells you exactly which ones your session lacks. The usual sets:

```
# Debian, Ubuntu, Linux Mint, Pop!_OS
sudo apt install xclip at-spi2-core                                 # X11
sudo apt install grim wtype wl-clipboard                            # sway, Hyprland
sudo apt install xdg-desktop-portal-gnome wl-clipboard              # GNOME on Wayland (KDE: xdg-desktop-portal-kde)

# Fedora
sudo dnf install xclip at-spi2-core                                 # X11
sudo dnf install grim wtype wl-clipboard                            # sway, Hyprland
sudo dnf install xdg-desktop-portal-gnome wl-clipboard              # GNOME on Wayland (KDE: xdg-desktop-portal-kde)

# Arch, Manjaro, EndeavourOS
sudo pacman -S --needed xclip at-spi2-core                          # X11
sudo pacman -S --needed grim wtype wl-clipboard                     # sway, Hyprland
sudo pacman -S --needed xdg-desktop-portal-gnome wl-clipboard       # GNOME on Wayland (KDE: xdg-desktop-portal-kde)
```

`ydotool` is the input fallback for Wayland desktops without the remote-desktop portal (COSMIC, for
example); it needs its daemon `ydotoold` running. The X11 libraries themselves come with every X11
desktop.

### Claude Code on Linux

Install Claude Code as usual ([claude.com/claude-code](https://claude.com/claude-code), native installer
or npm) and run `claude` once to log in. DeskPilot finds it on your PATH and in the usual user folders
(`~/.local/bin`, `~/.claude/local`, npm, bun, nvm), or you set its path in Settings. It then works
exactly as on Windows: the CLI starts `deskpilot --mcp-bridge` as its MCP server, and that process talks
to the running DeskPilot window over a private Unix socket.

### Stopping the agent on Linux

- the **Stop** button in the main window and in the small overlay that shows the current step;
- the **tray icon**'s Stop (GNOME shows tray icons only with the AppIndicator extension);
- the **stop hotkey** (Ctrl+Alt+X by default) on X11 only, since Wayland does not let apps grab global keys;
- the **failsafe corner**: move the mouse into the top-left corner of the main screen (on Wayland only
  where DeskPilot can read the pointer position).

Linux has no way to keep a window out of screenshots, so the overlay hides itself for the instant a
screenshot is taken, and the main window minimizes while the agent works (when that setting is on).

### Files on Linux

Settings live in `~/.config/DeskPilot/settings.json` (`$XDG_CONFIG_HOME` is respected), next to
`.secret-key`, the key your API keys are encrypted with (readable only by you). Logs go to
`~/.local/share/DeskPilot/logs`. A `portable.txt` file next to the program (in the extracted tar.gz
folder, not inside an AppImage) keeps everything in a `DeskPilotData` folder beside it, as on Windows.

## Privacy

Screenshots and your requests are sent to the model provider you choose (Anthropic for the default
Claude setup, or your local machine for Ollama / LM Studio). DeskPilot itself sends nothing anywhere
else, stores no screenshots on disk, and never logs API keys. Settings live in
`%APPDATA%\DeskPilot\settings.json`; logs in `%LOCALAPPDATA%\DeskPilot\logs`. Put an empty file named
`portable.txt` next to the exe to keep everything in a `DeskPilotData` folder beside it instead.

## How it works

With Claude Code (and other CLI agents) DeskPilot starts the CLI in a locked-down mode: no built-in
tools, none of your Claude Code settings or CLAUDE.md files, only DeskPilot's own tools, which it
provides as an MCP server. The CLI runs the conversation; every tool call comes back into DeskPilot,
where the safety checks run before anything touches your desktop. With API and local providers,
DeskPilot runs the agent loop itself. See [docs/DESIGN.md](docs/DESIGN.md) for the details.

## Building from source

Requirements: Windows 10 2004 or later, the .NET 10 SDK.

```
dotnet build DeskPilot.sln
dotnet test tests/DeskPilot.Tests
dotnet publish src/DeskPilot/DeskPilot.csproj -c Release -r win-x64
```

The last command writes the single-file `DeskPilot.exe` to `bin/publish/`.

## License

MIT, see [LICENSE](LICENSE).
