using System.Text;
using System.Text.RegularExpressions;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Backends.Acp;

/// <summary>Works out which program to run for an ACP profile, with which arguments and environment.</summary>
internal static class AcpLaunchBuilder
{
    public const string DefaultServerName = "deskpilot";
    public const string GeminiSystemPromptVariable = "GEMINI_SYSTEM_MD";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly string[] RunnableExtensions = { ".exe", ".cmd", ".bat", ".com" };

    /// <summary>
    /// Builds the launch spec and writes its temporary files (Gemini: system prompt and policy) into
    /// <paramref name="tempDirectory"/>. Throws AcpAgentException with a readable message on problems.
    /// </summary>
    public static AcpLaunchSpec Build(AgentBackendContext context, Func<string, string?> resolveExecutable, string tempDirectory)
    {
        var profile = context.Profile;
        var executable = ResolveExecutable(profile, resolveExecutable);
        var isGemini = IsGeminiAgent(profile, executable);
        var serverName = string.IsNullOrWhiteSpace(context.Mcp?.ServerName) ? DefaultServerName : context.Mcp!.ServerName;

        var args = CommandLine.Split(profile.ExtraCliArgs);
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var tempFiles = new List<string>();
        string? promptFile = null, policyFile = null;
        var restricted = false;

        if (isGemini)
        {
            try
            {
                Directory.CreateDirectory(tempDirectory);
                var id = Guid.NewGuid().ToString("N");
                promptFile = Path.Combine(tempDirectory, $"acp-system-{id}.md");
                File.WriteAllText(promptFile, BuildGeminiSystemPrompt(context.SystemPrompt, serverName), Utf8NoBom);
                tempFiles.Add(promptFile);
                policyFile = Path.Combine(tempDirectory, $"acp-policy-{id}.toml");
                File.WriteAllText(policyFile, BuildGeminiPolicy(serverName), Utf8NoBom);
                tempFiles.Add(policyFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                foreach (var f in tempFiles) TryDelete(f);
                throw new AcpAgentException("DeskPilot could not write the agent's temporary files: " + ex.Message, ex);
            }

            env[GeminiSystemPromptVariable] = promptFile;
            restricted = AddGeminiArguments(args, profile.Model, serverName, policyFile);

            // A key stored in the profile reaches Gemini CLI the way it expects it, unless one is already set.
            if (context.ApiKey.Length > 0 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GEMINI_API_KEY")))
                env["GEMINI_API_KEY"] = context.ApiKey;
        }

        if (!string.IsNullOrWhiteSpace(profile.ApiKeyEnvVar) && context.ApiKey.Length > 0)
            env[profile.ApiKeyEnvVar.Trim()] = context.ApiKey;

        // The user's own variables win; an empty value removes a variable from the agent's environment.
        foreach (var (name, value) in profile.ExtraEnv)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            env[name.Trim()] = string.IsNullOrEmpty(value) ? null : value;
        }

        var displayName = isGemini ? "Gemini CLI" : DefaultDisplayName(executable);
        var ext = Path.GetExtension(executable).ToLowerInvariant();
        string fileName;
        string? raw = null;
        IReadOnlyList<string> argumentList = args;
        if (ext is ".cmd" or ".bat")
        {
            // Batch shims (npm installs gemini.cmd) must go through cmd.exe; quote exactly once, here.
            fileName = CommandProcessor();
            raw = WindowsCommandLine.BuildCmdArguments(executable, args);
            argumentList = Array.Empty<string>();
        }
        else
        {
            fileName = executable;
        }

        return new AcpLaunchSpec
        {
            FileName = fileName,
            ArgumentList = argumentList,
            RawArguments = raw,
            WorkingDirectory = context.WorkingDirectory,
            Environment = env,
            ExecutablePath = executable,
            AgentArguments = args,
            DisplayName = displayName,
            IsGemini = isGemini,
            McpRestrictedToDeskPilot = restricted,
            ServerName = serverName,
            SystemPromptFile = promptFile,
            PolicyFile = policyFile,
            TempFiles = tempFiles,
        };
    }

    /// <summary>profile.CliPath (a path or a command name), else the preset's command.</summary>
    public static string ResolveExecutable(ProviderProfile profile, Func<string, string?> resolveExecutable)
    {
        var configured = Environment.ExpandEnvironmentVariables((profile.CliPath ?? "").Trim().Trim('"'));
        if (configured.Length > 0)
        {
            if (Path.IsPathRooted(configured) || configured.Contains('\\') || configured.Contains('/'))
            {
                if (File.Exists(configured)) return PreferRunnable(Path.GetFullPath(configured));
                throw new AcpAgentException($"The agent program \"{configured}\" does not exist. Fix the CLI path in the profile settings.");
            }
            var name = StripRunnableExtension(configured);
            return resolveExecutable(name)
                ?? throw new AcpAgentException($"Could not find the command \"{configured}\". Install it or set the full path of the agent program in the profile settings.");
        }

        var preset = ProviderPresets.Find(profile.PresetId);
        var command = preset?.CliCommand ?? "";
        if (command.Length == 0)
            throw new AcpAgentException("This ACP agent profile has no command to run. In the profile settings, set the CLI path to the agent program and put the arguments that start its ACP mode in the extra CLI arguments.");

        var found = resolveExecutable(command);
        if (found != null) return found;
        if (preset!.Id == ProviderPresets.GeminiCliId)
            throw new AcpAgentException("Gemini CLI was not found. Install it (npm install -g @google/gemini-cli), run 'gemini' once in a terminal to sign in, or set its path in the profile settings.");
        throw new AcpAgentException($"Could not find \"{command}\". Install it or set its path in the profile settings.");
    }

    public static bool IsGeminiAgent(ProviderProfile profile, string executable) =>
        profile.PresetId == ProviderPresets.GeminiCliId ||
        string.Equals(Path.GetFileNameWithoutExtension(executable), "gemini", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Adds what DeskPilot needs from Gemini CLI unless the user already passed it. Returns true when the
    /// MCP server allow-list restricts Gemini to DeskPilot's server.
    /// </summary>
    public static bool AddGeminiArguments(List<string> args, string? model, string serverName, string? policyFile)
    {
        bool Has(params string[] names) => args.Any(a => names.Any(n =>
            a.Equals(n, StringComparison.OrdinalIgnoreCase) || a.StartsWith(n + "=", StringComparison.OrdinalIgnoreCase)));

        if (!Has("--acp", "--experimental-acp")) args.Insert(0, "--acp");
        if (!string.IsNullOrWhiteSpace(model) && !Has("-m", "--model"))
        {
            args.Add("-m");
            args.Add(model.Trim());
        }

        var restricted = false;
        if (!Has("--allowed-mcp-server-names"))
        {
            args.Add("--allowed-mcp-server-names");
            args.Add(serverName);
            restricted = true;
        }

        // The working directory is DeskPilot's own empty folder; headless runs refuse untrusted folders.
        if (!Has("--skip-trust")) args.Add("--skip-trust");
        // A "yolo" default in the user's Gemini settings must not auto-approve shell or file tools here.
        if (!Has("--approval-mode", "--yolo", "-y"))
        {
            args.Add("--approval-mode");
            args.Add("default");
        }
        if (policyFile != null)
        {
            args.Add("--policy");
            args.Add(policyFile);
        }
        return restricted;
    }

    public static string BuildGeminiSystemPrompt(string systemPrompt, string serverName) =>
        systemPrompt.TrimEnd() + "\n\n" + ToolNamingNote(serverName) + "\n";

    public static string ToolNamingNote(string serverName) =>
        $"# Tool names\nDeskPilot's tools reach you through an MCP server named \"{serverName}\", so their names may carry a prefix " +
        $"(for example mcp_{serverName}_screenshot or mcp__{serverName}__screenshot instead of screenshot). They are the tools described above; use only them to work on the computer.";

    /// <summary>
    /// A Gemini CLI policy (user tier, replaces the user's own policy folder for this run): DeskPilot's MCP
    /// tools are allowed, every other tool is denied and hidden from the model, like --tools "" for Claude.
    /// </summary>
    public static string BuildGeminiPolicy(string serverName)
    {
        var server = TomlString(serverName);
        return $"""
            # Written by DeskPilot for one Gemini CLI session and deleted afterwards.
            # Only DeskPilot's own tools may run; Gemini CLI's built-in tools are switched off.

            [[rule]]
            toolName = "*"
            mcpName = {server}
            decision = "allow"
            priority = 999

            [[rule]]
            toolName = "*"
            decision = "deny"
            priority = 990
            denyMessage = "Only DeskPilot's tools are available in this session."

            """;
    }

    private static string TomlString(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string DefaultDisplayName(string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable);
        return string.IsNullOrWhiteSpace(name) ? "The ACP agent" : name;
    }

    private static string CommandProcessor()
    {
        var comspec = Environment.GetEnvironmentVariable("ComSpec");
        return !string.IsNullOrWhiteSpace(comspec) && File.Exists(comspec) ? comspec : Path.Combine(Environment.SystemDirectory, "cmd.exe");
    }

    private static string StripRunnableExtension(string name)
    {
        var ext = Path.GetExtension(name);
        return RunnableExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) ? name[..^ext.Length] : name;
    }

    /// <summary>npm puts an extensionless shell script next to the .cmd shim; Windows can only run the shim.</summary>
    private static string PreferRunnable(string path)
    {
        if (Path.GetExtension(path).Length > 0) return path;
        foreach (var ext in RunnableExtensions)
        {
            var candidate = path + ext;
            if (File.Exists(candidate)) return candidate;
        }
        return path;
    }

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Quoting for starting batch files through cmd.exe (the cross-spawn algorithm).</summary>
internal static class WindowsCommandLine
{
    private static readonly Regex MetaChars = new(@"([()\][%!^""`<>&|;, *?])", RegexOptions.Compiled);

    /// <summary>"/d /s /c "...": runs <paramref name="scriptPath"/> with each argument arriving unchanged.</summary>
    public static string BuildCmdArguments(string scriptPath, IEnumerable<string> args)
    {
        var line = new StringBuilder(EscapeCommand(scriptPath));
        // Batch shims re-parse %* once more, so metacharacters are escaped twice.
        foreach (var a in args) line.Append(' ').Append(EscapeArgument(a, doubleEscape: true));
        return "/d /s /c \"" + line + "\"";
    }

    public static string EscapeCommand(string command) => MetaChars.Replace(command, "^$1");

    public static string EscapeArgument(string arg, bool doubleEscape)
    {
        // Backslashes before a quote are doubled and the quote escaped; trailing backslashes are doubled
        // because a closing quote follows (Microsoft C runtime rules, which node.exe uses).
        var s = Regex.Replace(arg, "(\\\\*)\"", m => m.Groups[1].Value + m.Groups[1].Value + "\\\"");
        s = Regex.Replace(s, "(\\\\*)\\z", m => m.Groups[1].Value + m.Groups[1].Value);
        s = "\"" + s + "\"";
        s = MetaChars.Replace(s, "^$1");
        if (doubleEscape) s = MetaChars.Replace(s, "^$1");
        return s;
    }
}
