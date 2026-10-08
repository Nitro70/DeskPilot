using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Backends.Cli;

/// <summary>
/// Pure builders for starting the Claude Code CLI: executable resolution, arguments, environment,
/// the MCP config file and the stream-json lines DeskPilot writes to stdin.
/// </summary>
internal static partial class ClaudeCliCommand
{
    public const string CommandName = "claude";
    public const string DefaultModel = "haiku";

    public const string NotFoundMessage =
        "Claude Code CLI not found. Install Claude Code, run 'claude' once to log in, or set its path in Settings.";

    public const string NotLoggedInMessage =
        "Claude Code is not logged in. Open a terminal and run: claude auth login";

    // Variables a parent Claude Code session sets for its own child processes. DeskPilot can be started
    // from inside such a session (a Claude Code terminal, the desktop app); its agent must still be an
    // independent headless session, so these must not leak into it (CLAUDECODE alone makes the CLI refuse
    // to start as a "nested session", and the SDK variables change how MCP tools are named).
    internal static readonly IReadOnlyList<string> ParentSessionVariables = new[]
    {
        "CLAUDECODE",
        "CLAUDE_CODE_ENTRYPOINT",
        "CLAUDE_CODE_SESSION_ID",
        "CLAUDE_CODE_CHILD_SESSION",
        "CLAUDE_CODE_HOST_SESSION_ID",
        "CLAUDE_CODE_SESSION_ATTENDED",
        "CLAUDE_CODE_MESSAGING_SOCKET",
        "CLAUDE_CODE_MESSAGING_TOKEN",
        "CLAUDE_CODE_EXECPATH",
        "CLAUDE_CODE_SDK_HAS_HOST_AUTH_REFRESH",
        "CLAUDE_AGENT_SDK_VERSION",
        "CLAUDE_AGENT_SDK_MCP_NO_PREFIX",
        "CLAUDE_PID",
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // ------------------------------------------------------------------ executable

    /// <summary>Finds the claude executable. Prefers a native claude.exe over an npm .cmd shim when both exist.</summary>
    public static string? ResolveExecutable(string? configuredPath)
    {
        var found = ExecutableLocator.Find(CommandName, configuredPath);
        if (found == null) return null;
        var configured = !string.IsNullOrWhiteSpace(configuredPath) &&
                         string.Equals(Path.GetFullPath(found), SafeFullPath(configuredPath), StringComparison.OrdinalIgnoreCase);
        return PreferNative(found, configured ? Array.Empty<string>() : NativeSearchDirectories());
    }

    /// <summary>
    /// When <paramref name="path"/> is a .cmd/.bat shim, returns a native claude.exe from the same folder or,
    /// failing that, from <paramref name="searchDirectories"/>; otherwise returns <paramref name="path"/>.
    /// </summary>
    public static string PreferNative(string path, IEnumerable<string> searchDirectories)
    {
        if (!IsShellScript(path)) return path;
        var sibling = Path.Combine(Path.GetDirectoryName(path) ?? "", CommandName + ".exe");
        if (File.Exists(sibling)) return sibling;
        foreach (var dir in searchDirectories)
        {
            try
            {
                var candidate = Path.Combine(dir, CommandName + ".exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return path;
    }

    public static bool IsShellScript(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    private static string? SafeFullPath(string? p)
    {
        try { return p == null ? null : Path.GetFullPath(Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'))); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static IEnumerable<string> NativeSearchDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0) yield return Path.Combine(home, ".local", "bin");
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            var path = Environment.GetEnvironmentVariable("PATH", target);
            if (string.IsNullOrEmpty(path)) continue;
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return Environment.ExpandEnvironmentVariables(dir.Trim('"'));
        }
    }

    // ------------------------------------------------------------------ arguments

    /// <summary>
    /// The CLI arguments in a stable order (see DESIGN.md). Without an MCP config (tests) --mcp-config and
    /// --allowedTools are omitted but --strict-mcp-config is kept so no user MCP servers load.
    /// </summary>
    public static List<string> BuildArguments(ProviderProfile profile, string systemPromptFile, string? mcpConfigFile, string? mcpServerName)
    {
        var args = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--model", ModelOrDefault(profile.Model),
            "--system-prompt-file", systemPromptFile,
            "--tools", "",
            "--strict-mcp-config",
        };
        if (!string.IsNullOrEmpty(mcpConfigFile))
        {
            args.Add("--mcp-config");
            args.Add(mcpConfigFile);
            args.Add("--allowedTools");
            args.Add(AllowedToolsFor(string.IsNullOrWhiteSpace(mcpServerName) ? "deskpilot" : mcpServerName));
        }
        args.Add("--setting-sources");
        args.Add("");
        args.Add("--disable-slash-commands");
        args.Add("--no-session-persistence");

        var effort = NormalizeEffort(profile.Effort);
        if (effort != null)
        {
            args.Add("--effort");
            args.Add(effort);
        }
        args.AddRange(CommandLine.Split(profile.ExtraCliArgs));
        return args;
    }

    public static string ModelOrDefault(string? model) => string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();

    /// <summary>Server-level permission: every tool of that MCP server and nothing else.</summary>
    public static string AllowedToolsFor(string serverName) => "mcp__" + serverName;

    /// <summary>
    /// Claude Code accepts low, medium, high, xhigh and max. The profile's generic levels "none" and
    /// "minimal" (meant for other providers) map to the lowest Claude level instead of failing the start.
    /// </summary>
    public static string? NormalizeEffort(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort)) return null;
        var e = effort.Trim().ToLowerInvariant();
        return e is "none" or "minimal" ? "low" : e;
    }

    // ------------------------------------------------------------------ environment

    /// <summary>Applies the profile's environment rules to a child environment (case-insensitive keys expected).</summary>
    public static void ApplyEnvironment(IDictionary<string, string?> env, ProviderProfile profile)
    {
        foreach (var name in ParentSessionVariables) RemoveKey(env, name);

        if (profile.Thinking == ThinkingMode.Off) SetKey(env, "MAX_THINKING_TOKENS", "0");

        if (profile.ForceSubscriptionLogin)
        {
            RemoveKey(env, "ANTHROPIC_API_KEY");
            RemoveKey(env, "ANTHROPIC_AUTH_TOKEN");
        }

        if (profile.ExtraEnv != null)
        {
            foreach (var (name, value) in profile.ExtraEnv)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                // An empty value removes the variable (Windows has no real empty variables anyway).
                if (string.IsNullOrEmpty(value)) RemoveKey(env, name.Trim());
                else SetKey(env, name.Trim(), value);
            }
        }
    }

    private static void RemoveKey(IDictionary<string, string?> env, string name)
    {
        foreach (var key in env.Keys.Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList())
            env.Remove(key);
    }

    private static void SetKey(IDictionary<string, string?> env, string name, string value)
    {
        RemoveKey(env, name);
        env[name] = value;
    }

    // ------------------------------------------------------------------ process start

    /// <summary>
    /// Builds the ProcessStartInfo: stdio redirected, UTF-8 without BOM, no window. A .cmd/.bat shim (npm
    /// install) cannot be started directly with redirected stdio, so it runs through cmd.exe.
    /// </summary>
    public static ProcessStartInfo BuildStartInfo(string exePath, IReadOnlyList<string> args, string workingDirectory, ProviderProfile profile)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        if (IsShellScript(exePath))
        {
            psi.FileName = CmdExePath();
            psi.Arguments = BuildCmdArguments(exePath, args);
        }
        else
        {
            psi.FileName = exePath;
            foreach (var a in args) psi.ArgumentList.Add(a);
        }

        ApplyEnvironment(psi.Environment, profile);
        return psi;
    }

    public static string CmdExePath()
    {
        var comspec = Environment.GetEnvironmentVariable("ComSpec");
        if (!string.IsNullOrWhiteSpace(comspec) && File.Exists(comspec)) return comspec;
        return Path.Combine(Environment.SystemDirectory, "cmd.exe");
    }

    /// <summary>
    /// The cmd.exe argument string that runs a batch shim with the given arguments. Same scheme as the
    /// widely used cross-spawn package: each argument is quoted for the C runtime, then every cmd
    /// metacharacter (quotes included) is caret-escaped twice, because cmd parses the line once for /c and
    /// again when the batch file expands %*. An empty argument survives as "".
    /// </summary>
    public static string BuildCmdArguments(string scriptPath, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        sb.Append(EscapeCmdCommand(scriptPath));
        foreach (var a in args)
        {
            sb.Append(' ');
            sb.Append(EscapeCmdArgument(a, doubleEscape: true));
        }
        return "/d /s /c \"" + sb + "\"";
    }

    internal static string EscapeCmdCommand(string command) => CmdMetaChars().Replace(command, "^$1");

    internal static string EscapeCmdArgument(string arg, bool doubleEscape)
    {
        // Backslashes before a quote are doubled and the quote is escaped; trailing backslashes are doubled
        // because a closing quote follows. Other backslashes are literal for the C runtime.
        var s = BackslashesBeforeQuote().Replace(arg, m => m.Groups[1].Value + m.Groups[1].Value + "\\\"");
        s = TrailingBackslashes().Replace(s, m => m.Groups[1].Value + m.Groups[1].Value);
        s = "\"" + s + "\"";
        s = CmdMetaChars().Replace(s, "^$1");
        if (doubleEscape) s = CmdMetaChars().Replace(s, "^$1");
        return s;
    }

    [GeneratedRegex(@"([()\][%!^""`<>&|;, *?])")]
    private static partial Regex CmdMetaChars();

    [GeneratedRegex(@"(\\*)""")]
    private static partial Regex BackslashesBeforeQuote();

    [GeneratedRegex(@"(\\*)$")]
    private static partial Regex TrailingBackslashes();

    // ------------------------------------------------------------------ files and stdin lines

    public static UTF8Encoding FileEncoding => Utf8NoBom;

    /// <summary>{"mcpServers":{"deskpilot":{"type":"stdio","command":"...","args":[...]}}}</summary>
    public static string BuildMcpConfigJson(McpEndpointInfo mcp)
    {
        return WriteJson(w =>
        {
            w.WriteStartObject();
            w.WriteStartObject("mcpServers");
            w.WriteStartObject(mcp.ServerName);
            w.WriteString("type", "stdio");
            w.WriteString("command", mcp.Command);
            w.WriteStartArray("args");
            foreach (var a in mcp.Args) w.WriteStringValue(a);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }, indented: true);
    }

    /// <summary>One stream-json user message. Images go before the text, as Anthropic recommends for vision.</summary>
    public static string BuildUserMessage(UserTurn turn)
    {
        var images = turn.Images ?? Array.Empty<ToolImage>();
        var text = turn.Text ?? "";
        return WriteJson(w =>
        {
            w.WriteStartObject();
            w.WriteString("type", "user");
            w.WriteStartObject("message");
            w.WriteString("role", "user");
            w.WriteStartArray("content");
            foreach (var img in images)
            {
                w.WriteStartObject();
                w.WriteString("type", "image");
                w.WriteStartObject("source");
                w.WriteString("type", "base64");
                w.WriteString("media_type", string.IsNullOrWhiteSpace(img.MediaType) ? "image/png" : img.MediaType);
                w.WriteString("data", img.Base64Data);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            // The API rejects empty text blocks; only an image-only turn may omit the text.
            if (!string.IsNullOrWhiteSpace(text) || images.Count == 0)
            {
                w.WriteStartObject();
                w.WriteString("type", "text");
                w.WriteString("text", string.IsNullOrWhiteSpace(text) ? "(empty message)" : text);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        });
    }

    /// <summary>{"type":"control_request","request_id":"...","request":{"subtype":"interrupt"}}</summary>
    public static string BuildInterruptRequest(string requestId) => WriteJson(w =>
    {
        w.WriteStartObject();
        w.WriteString("type", "control_request");
        w.WriteString("request_id", requestId);
        w.WriteStartObject("request");
        w.WriteString("subtype", "interrupt");
        w.WriteEndObject();
        w.WriteEndObject();
    });

    /// <summary>A success control_response carrying a JSON payload (answers a control_request from the CLI).</summary>
    public static string BuildControlSuccess(string requestId, Action<Utf8JsonWriter> writePayload) => WriteJson(w =>
    {
        w.WriteStartObject();
        w.WriteString("type", "control_response");
        w.WriteStartObject("response");
        w.WriteString("subtype", "success");
        w.WriteString("request_id", requestId);
        w.WritePropertyName("response");
        writePayload(w);
        w.WriteEndObject();
        w.WriteEndObject();
    });

    public static string BuildControlError(string requestId, string error) => WriteJson(w =>
    {
        w.WriteStartObject();
        w.WriteString("type", "control_response");
        w.WriteStartObject("response");
        w.WriteString("subtype", "error");
        w.WriteString("request_id", requestId);
        w.WriteString("error", error);
        w.WriteEndObject();
        w.WriteEndObject();
    });

    private static string WriteJson(Action<Utf8JsonWriter> write, bool indented = false)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = indented }))
            write(w);
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
