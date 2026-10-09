# DeskPilot design

DeskPilot is a Windows desktop app (WPF, .NET 10, one self-contained exe) that lets an AI model operate the
user's computer: it sees the screen through screenshots and acts with the mouse and keyboard. The default
model provider is the user's own Claude subscription through the Claude Code CLI (no API key, no third
party). Many other providers are supported. Nothing about any particular user is hard-coded: every path,
key and model lives in `settings.json`. A Linux version (Avalonia UI, X11 and Wayland) runs on the same
core; see [Linux](#linux) at the end.

## Architecture

```
 ┌──────────────────────────── DeskPilot.exe (WPF UI) ─────────────────────────────┐
 │ MainWindow (chat log, input, model picker)  SettingsWindow  Overlay  Confirm     │
 │                         │ IAgentSession                                          │
 │                   AgentSession ──── PromptBuilder, EnvironmentDetector           │
 │        ┌────────────────┼─────────────────────────┐                              │
 │  IAgentBackend:  ClaudeCliBackend   AcpBackend   HttpAgentBackend                │
 │        │ (claude -p stream-json)  (gemini --acp)  (own loop: Anthropic API,      │
 │        │                │                          OpenAI-compatible, Ollama)    │
 │        └──── MCP ───────┘                                │                       │
 │   McpPipeServer (named pipe) ◄── "DeskPilot.exe --mcp-bridge PIPE TOKEN"         │
 │        │                          (stdio MCP server process the CLI starts)      │
 │        ▼                                                 ▼                       │
 │   ObservedToolHost (events, stop signal, step limit) ◄───┘                       │
 │        ▼                                                                         │
 │   CompositeToolHost = ComputerToolHost (+SafetyGuard) + VaultToolHost            │
 │        ▼                                                                         │
 │   DesktopServices: screen capture, SendInput, windows, UI Automation, launcher,  │
 │                    clipboard, shell                                              │
 └──────────────────────────────────────────────────────────────────────────────────┘
```

* **CLI/ACP backends** do not run the loop themselves; the external agent does, and reaches DeskPilot's
  tools as an MCP server. The CLI starts `DeskPilot.exe --mcp-bridge <pipe> <token>` as a stdio MCP
  server; that process relays stdin/stdout to a named pipe served by the running DeskPilot window, so
  tools execute inside the GUI process (same safety guard, stop button, log and overlay).
* **HTTP backends** run the agent loop in-process and call the `IToolHost` directly.
* Both paths go through `ObservedToolHost`, so the UI log, stop signal and step limit behave the same.

## Source layout and ownership

| Folder | Contents |
|---|---|
| `src/DeskPilot.Core/Abstractions` | Interfaces and records shared by everything (foundation, do not change signatures) |
| `src/DeskPilot.Core/Settings` | `AppSettings`, `SettingsStore`, `SecretProtector` (DPAPI; AES-GCM key file on Linux), `ProviderPresets`, `AppPaths` |
| `src/DeskPilot.Core/Runtime` | `AgentRunControl`, `ObservedToolHost`, `CompositeToolHost`, `JsonArgs`, `ExecutableLocator`, `CommandLine`, `Log` |
| `src/DeskPilot.Core/Prompts` | `DefaultPrompts` (built-in computer-use prompt), `PromptBuilder` (placeholders) |
| `src/DeskPilot.Desktop.Windows` | Win32 implementations of the desktop interfaces (the Linux ones: see [Linux](#linux)) |
| `src/DeskPilot.Core/Tools` | `ComputerToolHost` (the computer tools), `CoordinateMapper` |
| `src/DeskPilot.Core/Safety` | `SafetyGuard` |
| `src/DeskPilot.Core/Vault` | `VaultToolHost`, vault index/search, `ObsidianVaultDetector` |
| `src/DeskPilot.Core/Mcp` | `McpPipeServer`, `McpBridge`, JSON-RPC/MCP protocol handling |
| `src/DeskPilot.Core/Backends/Cli` | `ClaudeCliBackend`, `ClaudeCliProbe` |
| `src/DeskPilot.Core/Backends/Acp` | `AcpBackend` (Agent Client Protocol client) |
| `src/DeskPilot.Core/Backends/Http` | `HttpAgentBackend` (agent loop) + Anthropic / OpenAI-compatible / Ollama clients, `ModelCatalog` |
| `src/DeskPilot.Core/Agent` | `AgentSession`, `EnvironmentDetector`, `ProfileAutoConfig` |
| `src/DeskPilot` | WPF app: `Program` (handles `--mcp-bridge`), windows, view models, theme, tray, hotkey |
| `tests/DeskPilot.Tests` | xUnit tests, one file per module (`<Module>Tests.cs`) |

Rules for every module:

* Public signatures in the stub files are a contract other modules compile against. You may add members
  and new files in your own folder; do not rename or remove existing public members or change their
  parameters.
* No hard-coded user paths, names, keys or machine facts. Everything configurable lives in `AppSettings`.
* Never log or display API keys. Never write screenshots to disk.
* No new NuGet dependencies in `DeskPilot.Core` (BCL + Windows Desktop framework only). The app project
  may use `CommunityToolkit.Mvvm` if wanted, nothing else.
* Code style: file-scoped namespaces, nullable enabled, comments only where they explain *why*.
* Never use the long dash characters (em/en dash) anywhere: code, comments, UI strings, docs.

## Tools the model sees

Coordinates in every tool are in the model's coordinate space: pixels of the screenshot image it was shown
(default) or 0-1000 normalized (`ScreenSettings.Coordinates`). `CoordinateMapper` converts to physical
virtual-desktop pixels. The mapping depends only on settings (monitor selection + max image size), not on
the last screenshot, so it is stable.

Computer tools (`ComputerToolHost`):

| Tool | Arguments | Notes |
|---|---|---|
| `screenshot` | none | Image + a text line: image size, foreground window title, cursor position in model coords. Text-only models (`Profile.SupportsVision == false`, see below) get a text description instead. |
| `zoom` | `x, y, width, height` (model coords) | Higher-resolution crop of a region, for small text. The result text reminds that clicks still use full-screenshot coordinates. |
| `click` | `x, y, button? (left/right/middle), clicks? (1-3), modifiers? (["ctrl","shift","alt","win"])` | Moves then clicks. |
| `move_mouse` | `x, y` | |
| `drag` | `from_x, from_y, to_x, to_y, button?` | Smooth move with button held. |
| `scroll` | `direction (up/down/left/right), amount? (notches, default 3), x?, y?` | Moves to x,y first when given. |
| `type_text` | `text, press_enter? (bool)` | Unicode typing; `\n` = Enter. |
| `press_keys` | `keys (e.g. "ctrl+s"), repeat? (1-50)` | One combo per call (KeyCombo). |
| `wait` | `seconds (0.1-30)` | Returns a screenshot after waiting. |
| `list_windows` | none | Title, process, bounds (model coords), minimized/elevated flags. |
| `focus_window` | `title (substring, case-insensitive)` | |
| `launch` | `target (app name, path, URL, folder), arguments?` | Only when `Safety.AllowAppLaunch`. Never elevated unless admin mode. |
| `ui_elements` | `window_title? , filter? (substring), max? (default 80)` | UI Automation: name, type, center (model coords), enabled. Foreground window by default. |
| `get_clipboard` / `set_clipboard` | `text` for set | Only when `Safety.AllowClipboard`. |
| `run_command` | `command, shell? (powershell/cmd), timeout_seconds? (default 60)` | Only when `Safety.AllowShellCommands`. Non-elevated, hidden window, output truncated to ~8 KB. |

When `Screen.ScreenshotAfterAction` is on, every action tool (click, move, drag, scroll, type, keys,
focus, launch, wait) waits `Screen.ActionSettleDelayMs` and returns a fresh screenshot with its text.

Vault tools (`VaultToolHost`, only listed when a vault folder is configured, enabled and exists):

| Tool | Arguments | Notes |
|---|---|---|
| `vault_search` | `query, max_results? , folder?` | Ranked keyword search (BM25-style, title/heading/tag boosts, phrase bonus). Returns relative paths, titles, line numbers and short snippets, never whole files. |
| `vault_read` | `path (relative), start_line? (1-based), max_lines?` | Capped at `Vault.MaxReadLines`. Paths are confined to the vault (no `..`, no absolute paths, no symlink escapes). |
| `vault_list` | `folder?` | Immediate children (folders + note names), capped. |
| `vault_append` | `path, text` | Only when `Vault.AllowWrites`. Creates the note (`.md` only) if missing, appends otherwise. |

## Safety rules (SafetyGuard + ComputerToolHost)

Admin mode (`Safety.AllowAdmin`) is OFF by default. When off:

* Any pointer action whose target window (`IWindowManager.GetWindowAt`) is elevated or a UAC/credential
  prompt is denied; keyboard actions are denied when the foreground window is elevated.
* While a UAC prompt is active (`IsUacPromptActive`), every action except `screenshot`, `zoom`, `wait`,
  `list_windows` is denied with a message telling the model the user must answer it.
* `press_keys` with `ctrl+shift+enter` (runs elevated from Start/Run) is denied.
* Clicking a UI element whose name contains "run as administrator" / "run as admin" is denied
  (`IUiInspector.GetElementAtAsync`, best effort with a short timeout).
* Typed text and shell commands matching any `Safety.ElevationTextPatterns` regex are denied.
* `launch` targets matching `Safety.ElevatedLaunchTargets` are denied; launches never use the `runas` verb.
* `run_command` never elevates.

Always (regardless of admin mode):

* Windows of processes in `Safety.BlockedProcesses` cannot be clicked, typed into or focused.
* DeskPilot's own windows are never a valid target (the process id equals the current process): deny.
* `Safety.Confirm`: `Never`, `RiskyOnly` (confirm `ActionRisk.High`), `Always` (confirm Medium and High).
  Confirmation goes through `IUserConfirmation`; "allow all" sets `AgentRunControl.AllowAllThisTurn`.
* `Safety.DryRun`: input actions are described but not performed; screenshots stay real.
* Failsafe: before each input action, if `Safety.FailsafeCorner` is on and the physical cursor is within
  3 px of the primary monitor's top-left corner while DeskPilot did not put it there, call
  `AgentRunControl.RequestStop("Failsafe: mouse moved to the top-left corner")`.
* `Safety.StopOnUserMouseMove`: if the cursor is more than 12 px from where DeskPilot last put it, stop.

Risk levels: screenshot/zoom/list/ui_elements/get_clipboard/wait = None; move/scroll = Low;
click/type/focus/set_clipboard/drag = Medium; launch/run_command/vault_append and key presses that include
enter, delete, alt+f4, ctrl+w, ctrl+q, win+l, ctrl+alt+delete = High.

## Claude Code CLI backend (verified against Claude Code 2.1.293)

Process: `claude` (auto-detected via `ExecutableLocator.Find("claude", profile.CliPath)`), working
directory `AppPaths.AgentWorkDirectory`, arguments:

```
-p --input-format stream-json --output-format stream-json --verbose
--model <profile.Model or "haiku">
--system-prompt-file <temp file with the rendered prompt>
--tools ""                       (no built-in tools at all)
--strict-mcp-config --mcp-config <temp json file>
--allowedTools mcp__deskpilot    (server-level allow: every DeskPilot tool, nothing else)
--setting-sources ""             (do not load the user's CLAUDE.md / settings)
--disable-slash-commands
--no-session-persistence
[--effort <profile.Effort>]      (only when set)
[profile.ExtraCliArgs...]
```

MCP config file: `{"mcpServers":{"deskpilot":{"type":"stdio","command":"<exe>","args":["--mcp-bridge","<pipe>","<token>"]}}}`.

Environment: `MAX_THINKING_TOKENS=0` when `profile.Thinking == Off`; when `profile.ForceSubscriptionLogin`
remove `ANTHROPIC_API_KEY` and `ANTHROPIC_AUTH_TOKEN` so the subscription login is used; drop the variables
a parent Claude Code session sets for its children (`CLAUDECODE`, `CLAUDE_CODE_ENTRYPOINT`, session ids...)
so DeskPilot also works when started from inside Claude Code; add `profile.ExtraEnv` (an empty value removes
a variable). Effort `none`/`minimal` map to `low` (Claude Code accepts low, medium, high, xhigh, max).

Observed protocol (stream-json, one JSON object per line):

* stdin user turn: `{"type":"user","message":{"role":"user","content":[{"type":"text","text":"..."}]}}`
  (image blocks `{"type":"image","source":{"type":"base64","media_type":"image/png","data":"..."}}` allowed).
* stdin interrupt: `{"type":"control_request","request_id":"<id>","request":{"subtype":"interrupt"}}` ->
  stdout `{"type":"control_response","response":{"subtype":"success","request_id":"<id>",...}}`, then the turn
  ends with a `result` whose `subtype` is `error_during_execution` and `terminal_reason` is
  `aborted_streaming`. The process stays alive and accepts the next turn.
* stdout `{"type":"system","subtype":"init","model":"claude-haiku-5-5","tools":[...],"mcp_servers":[{"name":"deskpilot","status":"connected"}],...}`
  at the start of each turn. If `mcp_servers` shows deskpilot not connected, report an error.
* stdout `{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"..."},{"type":"text","text":"..."},{"type":"tool_use",...}],"usage":{...}}}`.
  Thinking text may be empty (only a signature); skip empty thinking.
* stdout `{"type":"user","message":{"content":[{"type":"tool_result",...}]}}` echoes tool results (ignore; tool
  events come from ObservedToolHost).
* stdout `{"type":"result","subtype":"success"|"error_during_execution"|...,"is_error":bool,"result":"final text","total_cost_usd":0.0004,"num_turns":1,"usage":{"input_tokens":..,"output_tokens":..,"cache_read_input_tokens":..},"terminal_reason":"completed"|"aborted_streaming"}`
  ends the turn.
* Other types (`rate_limit_event`, `system/thinking_tokens`, `system/post_turn_summary`, ...) are informational;
  a `rate_limit_event` whose `rate_limit_info.status` is not `allowed` becomes a warning StatusEvent.
* The `haiku` alias resolves to the newest Haiku (Claude Haiku 5.5 at the time of writing).

`claude auth status` prints JSON: `{"loggedIn":true,"authMethod":"claude.ai","subscriptionType":"max",...}`.
`claude --version` prints `2.1.293 (Claude Code)`.

## ACP backend (Agent Client Protocol, experimental)

JSON-RPC 2.0 over the agent's stdio, newline-delimited. Default command `gemini --acp`
(`profile.CliPath` or auto-detected `gemini`, args from `profile.ExtraCliArgs`, default `--acp`). For Gemini
CLI DeskPilot also passes `--allowed-mcp-server-names deskpilot`, `--skip-trust`, `--approval-mode default`,
`--policy <temp file>` (a policy that denies every tool except DeskPilot's) and `-m <model>` when a model is
set. For custom ACP agents `CliPath` is the agent command and `ExtraCliArgs` its arguments. Flow:
`initialize` {protocolVersion: 1, clientCapabilities: {fs: {readTextFile: false, writeTextFile: false}, terminal: false}}
-> `session/new` {cwd, mcpServers: [{name:"deskpilot", command, args, env: []}]} -> `session/prompt`
{sessionId, prompt: [{type:"text", text}]} with `session/update` notifications streaming
`agent_message_chunk`, `agent_thought_chunk`, `tool_call`, `tool_call_update`, `plan`; the prompt response
carries `stopReason`. `session/cancel` (notification) interrupts. The agent may send
`session/request_permission`: allow (pick an `allow_once`/`allow_always` option) only for tool calls of the
deskpilot MCP server, reject everything else (built-in shell/file tools). Unknown client methods get a
JSON-RPC method-not-found error. The system prompt is passed via the `GEMINI_SYSTEM_MD` environment
variable (a temp file) for Gemini; for other agents it is prepended to the first prompt. If the agent
replies to `session/new` with an auth error, surface "log in to <agent> first" as a readable error.

## HTTP backend

`HttpAgentBackend` runs: send conversation + tools -> show text/thinking -> execute tool calls one by one
through `context.Tools` -> append results -> repeat until the model stops calling tools, the stop signal,
or the step limit. Conversation is kept between turns until `ResetConversationAsync`.

* **Anthropic Messages API** (`{BaseUrl}/v1/messages`, headers `x-api-key`, `anthropic-version: 2023-06-01`).
  Tool results carry images inside `tool_result` content. Thinking: `Auto` = send nothing; `On` =
  `{"type":"adaptive"}` for current models, `{"type":"enabled","budget_tokens":N}` for legacy models
  (Haiku 4.5, Sonnet/Opus 4.5 and older); `Off` = omit for legacy models, `{"type":"disabled"}` otherwise,
  and on a 400 that mentions thinking retry once without the thinking parameter. Effort goes in
  `output_config: {effort}` when set. Append assistant content blocks unchanged (thinking blocks with
  signatures included). Prompt caching: top-level `cache_control: {type: "ephemeral"}` when enabled.
  Context editing beta (`anthropic-beta: context-management-2025-06-27`,
  `context_management: {edits: [{type: "clear_tool_uses_20250919"}]}`) when enabled; on a 400 that
  mentions it, retry without and remember. Never use forced `tool_choice` (`any`/`tool` 400 on new models).
  Old screenshots are pruned client-side only when the history has no thinking blocks.
* **OpenAI-compatible** (`{BaseUrl}/chat/completions`, `Authorization: Bearer`, `ExtraHeaders`).
  Tools as `{"type":"function","function":{name, description, parameters}}`. Tool results are `role: tool`
  text; images go in a following `role: user` message with `image_url` data URLs. Reasoning:
  `ReasoningStyle.ReasoningEffort` -> `reasoning_effort` (Off -> "none" for local servers, otherwise
  omitted; Effort used when set), `ReasoningStyle.OpenRouter` -> `reasoning: {effort}` / `{enabled:false}`.
  On a 400 mentioning a reasoning parameter, retry once without it and remember. Use `max_tokens`, and
  `max_completion_tokens` for api.openai.com. Merge `ExtraBodyJson`. Accept `reasoning` /
  `reasoning_content` fields as thinking. Some local models put tool calls in text; parse
  `<tool_call>{json}</tool_call>` / fenced JSON `{"name":..,"arguments":..}` as a fallback.
* **Ollama native** (`{BaseUrl}/api/chat`, `stream: false`). Images as base64 strings in a message's
  `images` array (tool results: `role: tool` text, then a `role: user` message with the image). Tools
  same shape as OpenAI; tool call arguments come back as objects. `think: true/false` from `Thinking`
  (Auto = omit); thinking arrives in `message.thinking`. `options.num_predict` = MaxOutputTokens,
  `options.temperature` when set.
* Text-only models (`SupportsVision == false`): images are stripped and replaced with a text note; the
  tool host's screenshot tool returns a UI-text description for such profiles.
* Images in history: keep the last `Screen.ScreenshotsToKeep`; older ones become `[older screenshot removed]`.
* Retries: 429 / 5xx / timeouts up to 3 times with backoff (respect `retry-after`). Errors become
  `TurnOutcome.Failed` with the provider's message (never the key).

## UI

* **Main window**: dark theme. Top bar: profile picker, model box (editable combo with a refresh button
  listing models from `IModelCatalog`), thinking toggle (Auto/On/Off) + effort picker, status pill
  (Idle / Working / Stopping), settings button. Center: conversation log (user bubbles, assistant
  markdown-ish text, collapsible thinking, tool steps with icon + summary + optional screenshot thumbnail
  that enlarges on click, status lines, per-turn stats: steps, tokens, cost, time). Bottom: multi-line input
  (Enter sends, Shift+Enter newline), Send / Stop button, New conversation button.
* **Settings window** (tabs): Model & provider (profiles list, add from preset, edit fields, API key box
  stored with DPAPI, test connection, detected environment with "add" buttons), Vault (optional folder
  picker + detected Obsidian vaults, enable, allow writes, test search), Safety (admin mode with a warning
  and "Restart DeskPilot as administrator", confirm mode, app launch, shell, clipboard, dry run, failsafe,
  stop on mouse move, max steps, blocked processes), Screen (monitor, max size, format/quality, cursor,
  grid, coordinate mode, screenshot after action, delays, screenshots kept), Interface (minimize while
  working, overlay + position, stop hotkey, close to tray, show thinking/screenshots, start with Windows),
  Advanced (only when Advanced mode is on: full system-prompt editor with placeholder list, reset to
  default and preview; extra CLI args/env; extra body JSON; headers). "Your instructions" text box is on
  the main settings page for everyone.
* **Overlay**: small always-on-top pill showing the current step and a Stop button; excluded from screen
  capture (`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`), does not take focus, hides itself while an
  input action targets its area (`IInputActionObserver`).
* **Main window** is also excluded from capture, and is minimized while working when that setting is on.
* **Global stop hotkey** (default Ctrl+Alt+X, configurable) via `RegisterHotKey`; tray icon with
  Show / Stop / Exit.
* **Confirmation dialog** for `IUserConfirmation`: shows the action summary and risk; Allow / Deny /
  Allow all for this request; topmost, excluded from capture.
* First run: detect environment, auto-add detected providers, show a short welcome explaining the Claude
  subscription default, the optional vault and the stop hotkey.

## Linux

The Linux version reuses `DeskPilot.Core` unchanged (it targets plain `net10.0`); only the desktop layer and
the UI are new. Core code that differs by OS checks `OperatingSystem.IsWindows()` / `IsLinux()` itself:
`PromptBuilder` (the `{{OS}}` line names the distribution, the X11 or Wayland session and the desktop;
Linux app-finding tips; a sudo/pkexec/polkit safety rule instead of UAC), `ComputerToolHost` (`run_command`
shells are `bash` and `sh`; zsh and fish map to bash), `ExecutableLocator` (PATH plus `~/.local/bin`,
`~/.claude/local`, npm, bun, nvm, snap and Flatpak folders; the file must be executable),
`ObsidianVaultDetector` (`~/.config/obsidian`, plus the Flatpak and Snap sandboxes) and `SecretProtector`
(below). The MCP server and bridge, the Claude CLI / ACP / HTTP backends and `AgentSession` run as they are.
`tests/DeskPilot.Linux.Tests/CoreOnLinuxTests.cs` checks these on real Linux.

### Project layout

| Folder | Contents |
|---|---|
| `src/DeskPilot.Desktop.Linux` | The Linux desktop layer (SkiaSharp, Tmds.DBus.Protocol). `LinuxSession` detects the session, `LinuxDesktopFactory` builds the `DesktopServices` for it and lists missing helper tools, `FrameEncoder` scales and encodes captures and draws the cursor and grid for both routes |
| `src/DeskPilot.Desktop.Linux/X11` | `X11ScreenCapture`, `X11InputSimulator`, `X11WindowManager`, `X11GlobalHotkey`: native Xlib and XTest through P/Invoke |
| `src/DeskPilot.Desktop.Linux/Wayland` | `WaylandScreenCapture`, `WaylandInputSimulator`, `WaylandWindowManager`: compositor tools on wlroots, xdg-desktop-portal elsewhere |
| `src/DeskPilot.Desktop.Linux/Services` | Session-independent services: `AtSpiInspector` (`ui_elements` over the AT-SPI accessibility bus), `LinuxAppLauncher`, `LinuxClipboard`, `LinuxShellRunner`, `LinuxProcessInfo` (process names, root detection and polkit prompt detection from `/proc`) |
| `src/DeskPilot.Linux` | The Avalonia 12 app, assembly and executable `deskpilot`. `Program` handles `--mcp-bridge` before any UI starts; main window, settings and welcome windows, overlay, tray |
| `tests/DeskPilot.Linux.Tests` | xUnit v3. `[LinuxFact]`, `[X11Fact]` and `[WaylandFact]` tests run only on Linux or inside the matching session; the rest run anywhere, so the project also builds and passes on Windows |
| `packaging/linux` | `deskpilot.desktop`, icons, `install.sh` / `uninstall.sh` (per user, no root), `build-package.sh` (tar.gz and AppImage), `check-package.sh` (what CI checks in the packages) |
| `ci/` | `run-linux-tests.sh` (starts the sessions), `sway-headless.conf`, and per-module hooks: `apt/*.txt`, `setup.d/*.sh`, `session.d/*.sh` |

The app uses Avalonia's X11 backend, so on Wayland desktops its windows run through Xwayland. That backend
sets `WM_CLASS` to (process name, entry assembly name), both `deskpilot`, which is the `StartupWMClass` of
the menu entry (CI checks the real window under Xvfb).

### Session detection

`LinuxSession.Detect()` reads the environment: `DESKPILOT_SESSION=x11|wayland` overrides everything (for
example to force the X11 route under Xwayland); otherwise `XDG_SESSION_TYPE` decides, and without it
`WAYLAND_DISPLAY` wins over `DISPLAY` (Xwayland also sets `DISPLAY`). `XDG_CURRENT_DESKTOP` names the
desktop (`IsGnome`, `IsKde`, `IsWlroots` for sway, Hyprland, river, wayfire, labwc and niri, `IsCosmic`).
`LinuxDesktopFactory.CreateFor(session)` picks the X11 or Wayland implementations plus the shared services;
`DescribeMissingTools(session)` lists the helper programs that session's route still needs, for the welcome
window. `packaging/linux/install.sh` repeats the same checks in shell so the installer can print the right
package names before DeskPilot first runs; keep the two in step.

### X11 route

Everything goes through Xlib on DeskPilot's own display connections, without helper processes: screen
capture of the monitors, mouse and keyboard through the XTest extension, the window list, focus and
process ids from the properties the window manager maintains, and the global stop hotkey through
`XGrabKey` on a separate connection and thread. The clipboard uses `xclip` or `xsel`. X11 lets any client
of the display do all of this without asking, which is why it is the recommended session. The sources
in `src/DeskPilot.Desktop.Linux/X11` have the details.

### Wayland route

Wayland gives ordinary clients no way to read the screen or inject input, so the route depends on the
compositor:

* **wlroots compositors** (sway, Hyprland, river, wayfire, labwc, niri): `grim` takes screenshots, `wtype`
  types and presses keys, and the compositor's IPC (`swaymsg`, `hyprctl`) lists and focuses windows and
  drives the pointer; other wlroots compositors use `wlrctl`, `ydotool` or `dotool` for the pointer.
* **GNOME, KDE Plasma, COSMIC**: xdg-desktop-portal over D-Bus. The Screenshot portal shows the desktop's
  permission prompt; the RemoteDesktop portal asks once for mouse and keyboard control. Where that portal
  is missing (COSMIC, for example), `ydotool` or `dotool` inject input through `/dev/uinput`.
* `wl-clipboard` (`wl-copy`, `wl-paste`) is the clipboard everywhere.

There is no global hotkey on Wayland; the Stop buttons, the tray icon and the failsafe corner remain (the
corner only where the pointer position can be read). The sources in `src/DeskPilot.Desktop.Linux/Wayland`
have the details.

### DeskPilot's own windows in screenshots (ICaptureObserver)

On Windows the overlay, the main window and dialogs are excluded from capture with
`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`. Neither X11 nor Wayland has anything like it: a
screenshot shows every window on screen. So the Linux UI implements `ICaptureObserver` on the same object
as `IInputActionObserver`: `ComputerToolHost` calls `BeforeCaptureAsync` right before every capture (the
overlay hides and waits until the screen has been redrawn without it) and `AfterCapture` right after (it
shows again). The main window minimizes while the agent works. The model therefore does not see
DeskPilot's own UI, and the rule that DeskPilot's windows are never a target still holds through the
process id the window managers report.

### API keys on Linux

There is no DPAPI. `SecretProtector` encrypts keys with AES-GCM (random 96-bit nonce, 128-bit tag, and the
`DeskPilot.ApiKey.v1` value DPAPI uses as entropy as associated data) under a random 256-bit key, stored
base64 in `~/.config/DeskPilot/.secret-key`. The key file is written under a temporary name, set to mode
0600 and then renamed into place. Stored values start with `aesgcm:`; a DPAPI value copied from Windows, a
tampered value, or a value encrypted under another key all read back as empty instead of failing. This is
the protection of desktop apps that do not use a keyring: keys stay out of the plain-text settings and
anything copied from them, but not out of reach of programs running as the same user.

### MCP bridge on Linux

.NET implements named pipes on Linux as Unix domain sockets at `$TMPDIR/CoreFxPipe_<pipe name>` (the path
must stay under the 108-byte `sun_path` limit; the default `/tmp` easily does). `PipeOptions.CurrentUserOnly`
makes both ends check that the peer runs as the same user, and the session token is still required, so
the protection matches the Windows pipe. The socket file is removed when the server is disposed. When
DeskPilot runs from the AppImage, its own path is inside the mounted image, which exists for exactly as
long as DeskPilot runs, and that is all the bridge needs.

### Packaging

`packaging/linux/build-package.sh <rid>` publishes the single-file, self-contained program and writes
`DeskPilot-<version>-<rid>.tar.gz` (program, `install.sh`, `uninstall.sh`, menu entry, icons, README,
LICENSE) and, for linux-x64, `DeskPilot-<version>-x86_64.AppImage` (an AppDir with `AppRun`, the menu
entry, the icon, `usr/bin/deskpilot` and the hicolor icons, packed by appimagetool with a pinned type 2
runtime). Without a working appimagetool the AppImage is skipped with a warning. `install.sh` installs for
one user only (`~/.local/bin`, `~/.local/share/applications` with an absolute `Exec`,
`~/.local/share/icons/hicolor`), refreshes menu and icon caches only where they already exist, and
`uninstall.sh` removes exactly those files, leaving settings and logs alone.

### CI

`.github/workflows/linux.yml` runs on ubuntu-24.04. The test job builds `tests/DeskPilot.Linux.Tests` and
runs it through `ci/run-linux-tests.sh`, once per session, each inside a real but invisible session with
its own D-Bus session bus:

* **x11**: Xvfb at 1920x1080 with the openbox window manager and the AT-SPI bus;
* **wayland**: headless sway (wlroots, pixman renderer, no input devices, no Xwayland) at 1920x1080.

`DESKPILOT_TEST_SESSION` tells the tests which one they are in. Each module adds its packages in
`ci/apt/<module>.txt`, root setup in `ci/setup.d/<module>.sh` and session variables in
`ci/session.d/<module>.sh`. The package job then builds the tar.gz for linux-x64 and linux-arm64 and the
x86-64 AppImage, lints the scripts with shellcheck and runs `check-package.sh`: archive contents and
modes, an install and uninstall round trip in a scratch home, both packages starting in bridge mode, and
the window's `WM_CLASS` under Xvfb. Tags `v*` attach the packages to the GitHub release.
