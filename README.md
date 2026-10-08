# DeskPilot

DeskPilot lets an AI model use your Windows PC for you. Tell it what you want done; it looks at the
screen, then clicks, types, scrolls and opens apps the way you would, while you watch (and can stop it
at any moment).

By default it runs on **your own Claude subscription through the Claude Code CLI**: no API key, no
extra account, no third-party service. You can switch to almost any other model: the Anthropic API,
OpenAI, OpenRouter, Google Gemini, Groq, xAI, DeepSeek, Mistral, Together, Azure OpenAI, local models
in Ollama or LM Studio, any OpenAI-compatible server, or agents that speak the Agent Client Protocol
such as Gemini CLI.

It is a native Windows app (WPF, .NET 10) shipped as one self-contained `DeskPilot.exe`.

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
