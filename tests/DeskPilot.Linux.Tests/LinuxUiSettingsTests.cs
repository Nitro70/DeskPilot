using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Prompts;
using DeskPilot.Core.Settings;
using DeskPilot.Desktop.Linux;
using DeskPilot.Linux.Services;
using DeskPilot.Linux.ViewModels;
using DeskPilot.Linux.Views;

namespace DeskPilot.Linux.Tests;

/// <summary>
/// The Linux settings and welcome windows: view-model rules (ported from the Windows app), the XDG autostart
/// entry, and the real Avalonia windows built and driven on the headless platform (nothing is ever shown).
/// </summary>
public class LinuxUiSettingsTests
{
    private static readonly LinuxSessionInfo X11Session = new(LinuxSessionKind.X11, ":0", null, "GNOME", "/run/user/1000");
    private static readonly LinuxSessionInfo WaylandSession = new(LinuxSessionKind.Wayland, null, "wayland-0", "sway", "/run/user/1000");

    // ------------------------------------------------------------------ parsing

    [Theory]
    [InlineData("80", 1, 1000, true, 80)]
    [InlineData(" 1000 ", 1, 1000, true, 1000)]
    [InlineData("0", 1, 1000, false, 0)]
    [InlineData("1001", 1, 1000, false, 0)]
    [InlineData("abc", 1, 1000, false, 0)]
    [InlineData("", 1, 1000, false, 0)]
    [InlineData("1,280", 320, 3840, true, 1280)]
    [InlineData("1_280", 320, 3840, true, 1280)]
    public void ParseInt_checks_range(string text, int min, int max, bool ok, int expected)
    {
        Assert.Equal(ok, SettingsParsing.TryParseInt(text, min, max, out var value, out var error));
        if (ok) Assert.Equal(expected, value);
        else Assert.NotEmpty(error);
    }

    [Fact]
    public void ParseOptionalDouble_allows_empty()
    {
        Assert.True(SettingsParsing.TryParseOptionalDouble("", 0, 2, out var none, out _));
        Assert.Null(none);
        Assert.True(SettingsParsing.TryParseOptionalDouble("0.7", 0, 2, out var v, out _));
        Assert.Equal(0.7, v);
        Assert.False(SettingsParsing.TryParseOptionalDouble("2.5", 0, 2, out _, out var e1));
        Assert.NotEmpty(e1);
        Assert.False(SettingsParsing.TryParseOptionalDouble("warm", 0, 2, out _, out var e2));
        Assert.NotEmpty(e2);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", true)]
    [InlineData("http://127.0.0.1:11434", true)]
    [InlineData("", false)]
    [InlineData("api.openai.com/v1", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("file:///home/me/x", false)]
    [InlineData("not a url", false)]
    public void HttpUrl_validation(string url, bool ok)
    {
        Assert.Equal(ok, SettingsParsing.TryValidateHttpUrl(url, out var error));
        Assert.Equal(ok, error.Length == 0);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("{\"top_p\": 0.9}", true)]
    [InlineData("{\"a\": {\"b\": [1, 2]},}", true)]
    [InlineData("[1, 2]", false)]
    [InlineData("42", false)]
    [InlineData("{bad", false)]
    public void ExtraBodyJson_must_be_object_or_empty(string json, bool ok)
    {
        Assert.Equal(ok, SettingsParsing.TryValidateJsonObject(json, out var error));
        Assert.Equal(ok, error.Length == 0);
    }

    [Fact]
    public void Env_lines_parse_and_format()
    {
        Assert.True(SettingsParsing.TryParseEnvLines("A=1\r\n# comment\n\n  B = two words \nC=x=y\nexport D=4\nGONE=", out var env, out var error), error);
        Assert.Equal(5, env.Count);
        Assert.Equal("1", env["A"]);
        Assert.Equal("two words", env["B"]);
        Assert.Equal("x=y", env["C"]);
        Assert.Equal("4", env["D"]);
        Assert.Equal("", env["GONE"]); // an empty value removes the variable for the agent

        // Linux variable names are case-sensitive: both survive.
        Assert.True(SettingsParsing.TryParseEnvLines("path_extra=1\nPATH_EXTRA=2", out var cased, out _));
        Assert.Equal(2, cased.Count);

        Assert.False(SettingsParsing.TryParseEnvLines("A=1\nnovalue", out _, out var e1));
        Assert.Contains("Line 2", e1);
        Assert.False(SettingsParsing.TryParseEnvLines("=x", out _, out _));
        Assert.False(SettingsParsing.TryParseEnvLines("MY VAR=x", out _, out _));

        var text = SettingsParsing.FormatEnvLines(new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" });
        Assert.Equal("A=1\nB=2", text);
        Assert.True(SettingsParsing.TryParseEnvLines(text, out var back, out _));
        Assert.Equal(new[] { "A", "B" }, back.Keys.OrderBy(k => k));
    }

    [Fact]
    public void Header_lines_parse_and_reject_bad_names()
    {
        Assert.True(SettingsParsing.TryParseHeaderLines("api-key: abc\nHTTP-Referer: https://example.com/x\n", out var headers, out var error), error);
        Assert.Equal("abc", headers["api-key"]);
        Assert.Equal("https://example.com/x", headers["HTTP-Referer"]);

        Assert.False(SettingsParsing.TryParseHeaderLines("Bad Name: x", out _, out var e1));
        Assert.Contains("not a valid header name", e1);
        Assert.False(SettingsParsing.TryParseHeaderLines("no colon here", out _, out _));
        Assert.Equal("X: 1", SettingsParsing.FormatHeaderLines(new Dictionary<string, string> { ["X"] = "1" }));
    }

    [Fact]
    public void Lists_extensions_and_process_names_are_normalized()
    {
        Assert.Equal(new[] { "KeePass", "1Password" }, SettingsParsing.ParseProcessNames("KeePass.exe, 1Password ;keepass\n"));
        Assert.Equal(new[] { "keepassxc", "bitwarden" }, SettingsParsing.ParseProcessNames("/usr/bin/keepassxc, 'bitwarden'"));
        Assert.Equal(new[] { ".obsidian", "Archive" }, SettingsParsing.ParseFolderNames(".obsidian, /Archive/ , .OBSIDIAN"));

        Assert.True(SettingsParsing.TryParseExtensions("md, .TXT, *.org .md", out var ext, out var error), error);
        Assert.Equal(new[] { ".md", ".txt", ".org" }, ext);
        Assert.False(SettingsParsing.TryParseExtensions(" , ", out _, out var e1));
        Assert.Contains("at least one", e1);
        Assert.False(SettingsParsing.TryParseExtensions(".md, a/b", out _, out _));
        Assert.Equal("a\nb", SettingsParsing.FormatLines(new[] { "a", "b" }));
        Assert.Equal(new[] { "a", "b" }, SettingsParsing.ParseLines("a\r\nb\n\na"));
    }

    [Fact]
    public void Regex_lines_must_compile()
    {
        Assert.True(SettingsParsing.TryParseRegexLines("\\bsudo\\b\n\n\\bpkexec\\b", out var patterns, out _));
        Assert.Equal(2, patterns.Count);
        Assert.False(SettingsParsing.TryParseRegexLines("ok\n([unclosed", out _, out var error));
        Assert.Contains("([unclosed", error);
    }

    [Fact]
    public void InsertAt_replaces_selection_and_returns_caret()
    {
        Assert.Equal("Hello {{DATE}}world", SettingsParsing.InsertAt("Hello world", "{{DATE}}", 6, 0, out var caret));
        Assert.Equal(14, caret);
        Assert.Equal("Hi {{OS}}!", SettingsParsing.InsertAt("Hi there!", "{{OS}}", 3, 5, out caret));
        Assert.Equal(9, caret);
        Assert.Equal("x{{TIME}}", SettingsParsing.InsertAt("x", "{{TIME}}", 99, 5, out caret));
        Assert.Equal(9, caret);
    }

    [Fact]
    public void Redact_hides_secrets_in_messages()
    {
        const string key = "sk-secret-1234567890";
        var redacted = SettingsParsing.Redact($"401: invalid key {key}", key);
        Assert.DoesNotContain(key, redacted);
        Assert.Contains("sk-s", redacted);
    }

    [Theory]
    [InlineData("~", "/home/me")]
    [InlineData("~/Notes", "/home/me/Notes")]
    [InlineData(" \"~/My Notes\" ", "/home/me/My Notes")]
    [InlineData("~other/x", "~other/x")]
    [InlineData("/srv/notes", "/srv/notes")]
    [InlineData("", "")]
    public void ExpandHome_handles_the_tilde(string input, string expected) =>
        Assert.Equal(expected, SettingsParsing.ExpandHome(input, "/home/me/"));

    // ------------------------------------------------------------------ hotkey

    [Theory]
    [InlineData(KeyModifiers.Control | KeyModifiers.Alt, Key.X, "Ctrl+Alt+X")]
    [InlineData(KeyModifiers.Control | KeyModifiers.Shift, Key.D1, "Ctrl+Shift+1")]
    [InlineData(KeyModifiers.Alt | KeyModifiers.Meta, Key.F12, "Alt+Super+F12")]
    [InlineData(KeyModifiers.None, Key.Pause, "Pause")]
    [InlineData(KeyModifiers.Control, Key.PageDown, "Ctrl+PageDown")]
    [InlineData(KeyModifiers.Control, Key.NumPad5, "Ctrl+NumPad5")]
    [InlineData(KeyModifiers.Control | KeyModifiers.Alt, Key.Enter, "Ctrl+Alt+Enter")]
    public void Hotkey_format(KeyModifiers modifiers, Key key, string expected)
    {
        Assert.Equal(expected, SettingsHotkey.Format(modifiers, key));
        Assert.True(KeyCombo.TryParse(expected, out _, out var error), error);
    }

    [Fact]
    public void Hotkey_super_is_the_win_modifier_for_KeyCombo()
    {
        var combo = KeyCombo.Parse(SettingsHotkey.Format(KeyModifiers.Meta, Key.S)!);
        Assert.True(combo.Has("win"));
        Assert.Equal("s", combo.Key);
    }

    [Fact]
    public void Hotkey_modifier_only_and_layout_keys_are_not_combinations()
    {
        Assert.Null(SettingsHotkey.Format(KeyModifiers.Control, Key.LeftCtrl));
        Assert.Null(SettingsHotkey.Format(KeyModifiers.Alt, Key.RightAlt));
        Assert.Null(SettingsHotkey.Format(KeyModifiers.Control, Key.OemPlus)); // the symbol depends on the keyboard layout
        Assert.Equal("Ctrl+Alt+", SettingsHotkey.FormatModifiers(KeyModifiers.Control | KeyModifiers.Alt));
        Assert.Equal("Super+", SettingsHotkey.FormatModifiers(KeyModifiers.Meta));
    }

    [Theory]
    [InlineData("Ctrl+Alt+X", true)]
    [InlineData("ctrl+shift+f5", true)]
    [InlineData("F9", true)]
    [InlineData("Pause", true)]
    [InlineData("Super+Esc", true)]
    [InlineData("Win+Esc", true)]
    [InlineData("X", false)]
    [InlineData("Shift+A", false)]
    [InlineData("Ctrl+Alt", false)]
    [InlineData("", false)]
    [InlineData("Ctrl+A+B", false)]
    public void Hotkey_validation(string text, bool ok)
    {
        Assert.Equal(ok, SettingsHotkey.TryValidate(text, out var error));
        Assert.Equal(ok, error.Length == 0);
    }

    [Fact]
    public void Hotkey_capture_updates_setting()
    {
        using var vm = new SettingsViewModel(SettingsStore.CreateDefault());
        Assert.False(vm.CaptureHotkey(KeyModifiers.Control | KeyModifiers.Alt, Key.LeftAlt));
        Assert.Contains("Ctrl+Alt+", vm.HotkeyHint);
        Assert.Equal("Ctrl+Alt+X", vm.StopHotkey);

        Assert.True(vm.CaptureHotkey(KeyModifiers.Control | KeyModifiers.Shift, Key.Q));
        Assert.Equal("Ctrl+Shift+Q", vm.StopHotkey);
        Assert.Equal("Ctrl+Shift+Q", vm.Settings.Ui.StopHotkey);
        Assert.Equal("", vm.HotkeyHint);

        Assert.False(vm.CaptureHotkey(KeyModifiers.None, Key.Q));
        Assert.Equal("Ctrl+Shift+Q", vm.StopHotkey);
        Assert.Contains("Ctrl, Alt or Super", vm.HotkeyHint);

        Assert.False(vm.CaptureHotkey(KeyModifiers.Control, Key.OemComma));
        Assert.Contains("cannot be used", vm.HotkeyHint);

        Assert.True(vm.CaptureHotkey(KeyModifiers.Meta | KeyModifiers.Shift, Key.F2));
        Assert.Equal("Shift+Super+F2", vm.StopHotkey);

        Assert.True(vm.CaptureHotkey(KeyModifiers.None, Key.Back));
        Assert.Equal(SettingsHotkey.DefaultHotkey, vm.StopHotkey);
        Assert.Contains("default", vm.HotkeyHint);
    }

    // ------------------------------------------------------------------ profile view model

    [Fact]
    public void Profile_text_fields_map_to_profile()
    {
        var profile = ProviderPresets.CreateProfile(ProviderPresets.OpenAiId);
        var vm = new ProfileViewModel(profile);
        Assert.Equal("8192", vm.MaxOutputTokensText);
        Assert.Equal("", vm.TemperatureText);
        Assert.True(vm.IsHttp);
        Assert.False(vm.IsCliOrAcp);

        vm.MaxOutputTokensText = "4096";
        vm.TemperatureText = "0.25";
        vm.RequestTimeoutText = "120";
        vm.ThinkingBudgetText = "2048";
        vm.ExtraEnvText = "A=1";
        vm.ExtraHeadersText = "X-Test: yes";
        vm.ExtraBodyJson = "{\"top_p\":0.9}";
        vm.Name = "  My OpenAI  ";
        vm.Model = " gpt-5 ";
        vm.Thinking = ThinkingMode.Off;
        vm.SelectedEffortOption = vm.EffortOptions.Single(o => o.Value == "low");
        vm.ExtraCliArgs = "--x";
        vm.SelectedReasoningStyleOption = ProfileViewModel.ReasoningStyles.Single(o => o.Value == ReasoningStyle.OpenRouter);
        vm.SupportsVision = false;
        vm.UsePromptCaching = false;
        vm.UseContextEditing = false;
        vm.ForceSubscriptionLogin = false;
        vm.ApiKeyEnvVar = " MY_KEY ";

        Assert.True(vm.Commit(), string.Join("; ", vm.Errors.Keys.Select(k => vm.Errors[k])));
        Assert.Equal(4096, profile.MaxOutputTokens);
        Assert.Equal(0.25, profile.Temperature);
        Assert.Equal(120, profile.RequestTimeoutSeconds);
        Assert.Equal(2048, profile.ThinkingBudgetTokens);
        Assert.Equal("1", profile.ExtraEnv["A"]);
        Assert.Equal("yes", profile.ExtraHeaders["X-Test"]);
        Assert.Equal("{\"top_p\":0.9}", profile.ExtraBodyJson);
        Assert.Equal("My OpenAI", profile.Name);
        Assert.Equal("gpt-5", profile.Model);
        Assert.Equal(ThinkingMode.Off, profile.Thinking);
        Assert.Equal("low", profile.Effort);
        Assert.Equal("--x", profile.ExtraCliArgs);
        Assert.Equal(ReasoningStyle.OpenRouter, profile.ReasoningStyle);
        Assert.False(profile.SupportsVision);
        Assert.False(profile.UsePromptCaching);
        Assert.False(profile.UseContextEditing);
        Assert.False(profile.ForceSubscriptionLogin);
        Assert.Equal("MY_KEY", profile.ApiKeyEnvVar);

        vm.TemperatureText = "";
        Assert.True(vm.Commit());
        Assert.Null(profile.Temperature);
    }

    [Fact]
    public void Profile_cli_path_expands_home()
    {
        var profile = ProviderPresets.CreateProfile(ProviderPresets.ClaudeSubscriptionId);
        var vm = new ProfileViewModel(profile) { CliPath = "~/.local/bin/claude" };
        Assert.True(vm.Commit());
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(home.TrimEnd('/') + "/.local/bin/claude", profile.CliPath);
        Assert.Contains("Using this file", vm.CliHint);
    }

    [Fact]
    public void Profile_validation_reports_each_bad_field()
    {
        var profile = ProviderPresets.CreateProfile(ProviderPresets.OpenRouterId);
        var vm = new ProfileViewModel(profile);
        vm.Name = " ";
        vm.BaseUrl = "openrouter.ai";
        vm.MaxOutputTokensText = "lots";
        vm.TemperatureText = "9";
        vm.RequestTimeoutText = "1";
        vm.ExtraBodyJson = "[1]";
        vm.ExtraHeadersText = "Bad Header: 1";
        vm.ExtraEnvText = "oops";

        Assert.False(vm.Commit());
        foreach (var field in new[]
                 {
                     nameof(ProfileViewModel.Name), nameof(ProfileViewModel.BaseUrl), nameof(ProfileViewModel.MaxOutputTokensText),
                     nameof(ProfileViewModel.TemperatureText), nameof(ProfileViewModel.RequestTimeoutText), nameof(ProfileViewModel.ExtraBodyJson),
                     nameof(ProfileViewModel.ExtraHeadersText), nameof(ProfileViewModel.ExtraEnvText),
                 })
            Assert.NotEqual("", vm.Errors[field]);
        Assert.Equal(8192, profile.MaxOutputTokens);
        Assert.Equal("", profile.ExtraBodyJson);

        vm.Name = "OpenRouter";
        vm.BaseUrl = "https://openrouter.ai/api/v1";
        vm.MaxOutputTokensText = "1000";
        vm.TemperatureText = "1";
        vm.RequestTimeoutText = "60";
        vm.ExtraBodyJson = "";
        vm.ExtraHeadersText = "";
        vm.ExtraEnvText = "";
        Assert.True(vm.Commit());
        Assert.False(vm.Errors.HasErrors);
    }

    [Fact]
    public void Profile_base_url_is_only_required_for_http_kinds()
    {
        var cli = new ProfileViewModel(ProviderPresets.CreateProfile(ProviderPresets.ClaudeSubscriptionId));
        Assert.Equal("", cli.BaseUrl);
        Assert.True(cli.Commit());
        Assert.True(cli.IsCliOrAcp);
        Assert.True(cli.IsClaudeCli);
        Assert.Contains("'claude'", cli.CliHint);

        var ollama = new ProfileViewModel(ProviderPresets.CreateProfile(ProviderPresets.OllamaId));
        ollama.BaseUrl = "";
        Assert.False(ollama.Commit());
        Assert.NotEqual("", ollama.Errors[nameof(ProfileViewModel.BaseUrl)]);
    }

    [Fact]
    public void Profile_untouched_invalid_values_do_not_block_saving()
    {
        var profile = ProviderPresets.CreateProfile(ProviderPresets.OpenAiId);
        profile.ExtraBodyJson = "[\"hand edited\"]";
        profile.MaxOutputTokens = 0;
        var vm = new ProfileViewModel(profile);
        Assert.True(vm.Commit());
        Assert.Equal("[\"hand edited\"]", profile.ExtraBodyJson);
        Assert.Equal(0, profile.MaxOutputTokens);
    }

    [Fact]
    public void Profile_key_is_masked_never_shown_and_protected_on_save()
    {
        const string oldKey = "sk-abcdefgh12345678wxyz";
        var profile = ProviderPresets.CreateProfile(ProviderPresets.OpenAiId);
        profile.ApiKeyProtected = SecretProtector.Protect(oldKey);
        if (!OperatingSystem.IsWindows()) Assert.StartsWith("aesgcm:", profile.ApiKeyProtected);
        var vm = new ProfileViewModel(profile);

        Assert.True(vm.HasStoredKey);
        Assert.False(vm.ShowKeyEntry);
        Assert.Equal("Saved key: " + SecretProtector.Mask(oldKey), vm.StoredKeyText);
        Assert.Equal("", vm.NewApiKey); // the password box never receives the stored key
        Assert.DoesNotContain(oldKey, vm.StoredKeyText);
        Assert.DoesNotContain(oldKey, vm.KeyStateText);

        // Replace, then cancel: nothing changes.
        vm.ReplaceKey();
        Assert.True(vm.ShowKeyEntry);
        vm.NewApiKey = "sk-typed-but-cancelled";
        vm.CancelKeyChange();
        Assert.Equal("", vm.NewApiKey);
        Assert.True(vm.Commit());
        Assert.Equal(oldKey, SecretProtector.Unprotect(profile.ApiKeyProtected));

        // Replace with a new key: stored encrypted, trimmed.
        vm.ReplaceKey();
        vm.NewApiKey = "  sk-new-key-1234567890  ";
        Assert.Equal("sk-new-key-1234567890", vm.ResolveKeyForRequests());
        Assert.Contains("replaces the saved one", vm.KeyStateText);
        Assert.True(vm.Commit());
        Assert.DoesNotContain("sk-new-key-1234567890", profile.ApiKeyProtected);
        Assert.Equal("sk-new-key-1234567890", SecretProtector.Unprotect(profile.ApiKeyProtected));

        // Clear removes it.
        var cleared = new ProfileViewModel(profile);
        cleared.ClearKey();
        Assert.False(cleared.HasStoredKey);
        Assert.True(cleared.ShowKeyEntry);
        Assert.Contains("removed", cleared.KeyStateText);
        Assert.True(cleared.Commit());
        Assert.Equal("", profile.ApiKeyProtected);
    }

    [Fact]
    public void Profile_unreadable_key_asks_to_enter_it_again()
    {
        var profile = ProviderPresets.CreateProfile(ProviderPresets.OpenAiId);
        profile.ApiKeyProtected = Convert.ToBase64String(Encoding.UTF8.GetBytes("not a protected key"));
        var vm = new ProfileViewModel(profile);
        Assert.True(vm.HasStoredKey);
        Assert.Contains("Enter it again", vm.StoredKeyText);
    }

    [Fact]
    public void Profile_env_var_status_shows_only_the_name()
    {
        var profile = ProviderPresets.CreateProfile(ProviderPresets.OpenAiId);
        var vm = new ProfileViewModel(profile, getEnvironmentVariable: name => name == "OPENAI_API_KEY" ? "sk-env-secret-value" : null);
        Assert.Contains("OPENAI_API_KEY is set", vm.ApiKeyEnvStatus);
        Assert.DoesNotContain("sk-env-secret-value", vm.ApiKeyEnvStatus);
        vm.ApiKeyEnvVar = "OTHER_KEY";
        Assert.Contains("not set", vm.ApiKeyEnvStatus);
        Assert.Contains("~/.profile", vm.ApiKeyEnvStatus);
    }

    [Fact]
    public void Profile_effort_options_keep_unknown_values_and_share_the_standard_list()
    {
        var profile = ProviderPresets.CreateProfile(ProviderPresets.AnthropicApiId);
        profile.Effort = "turbo";
        var vm = new ProfileViewModel(profile);
        Assert.Contains(vm.EffortOptions, o => o.Value == "");
        Assert.Contains(vm.EffortOptions, o => o.Value == "high");
        Assert.Contains(vm.EffortOptions, o => o.Value == "turbo");
        Assert.Equal("turbo", vm.SelectedEffortOption!.Value);

        var standard = new ProfileViewModel(ProviderPresets.CreateProfile(ProviderPresets.AnthropicApiId));
        Assert.Same(ProfileViewModel.StandardEffortOptions, standard.EffortOptions);
        Assert.Equal("", standard.SelectedEffortOption!.Value);
        standard.SelectedEffortOption = null; // the picker swapping lists must not wipe the choice
        Assert.Equal("", standard.Effort);
        standard.Effort = "max";
        Assert.Equal("Max", standard.SelectedEffortOption!.Label);
    }

    [Fact]
    public void Profile_kind_flags()
    {
        ProfileViewModel Make(string id) => new(ProviderPresets.CreateProfile(id));
        Assert.True(Make(ProviderPresets.AnthropicApiId).IsAnthropicApi);
        Assert.True(Make(ProviderPresets.OpenAiId).IsOpenAiCompatible);
        Assert.True(Make(ProviderPresets.OllamaId).IsOllama);
        Assert.False(Make(ProviderPresets.OllamaId).ShowEffort);
        Assert.True(Make(ProviderPresets.GeminiCliId).IsAcp);
        Assert.False(Make(ProviderPresets.GeminiCliId).ShowThinking);
        Assert.Equal("Claude Code", Make(ProviderPresets.ClaudeSubscriptionId).KindBadge);
        Assert.Equal("ACP agent", Make(ProviderPresets.CustomAcpId).KindBadge);
    }

    [Fact]
    public async Task Test_connection_reports_model_count_and_uses_new_key()
    {
        var catalog = new FakeCatalog { Result = new ModelListResult(Enumerable.Range(1, 23).Select(i => new ModelInfo($"m{i}", $"M{i}", true, true, null)).ToList(), null) };
        var profile = ProviderPresets.CreateProfile(ProviderPresets.OpenAiId);
        var vm = new ProfileViewModel(profile, catalog);
        vm.NewApiKey = "sk-fresh-1234567890";
        vm.BaseUrl = "https://example.test/v1";

        await vm.TestConnectionAsync(CancellationToken.None);

        Assert.Equal("OK: 23 models", vm.ConnectionStatus);
        Assert.True(vm.ConnectionOk);
        Assert.True(vm.ConnectionSucceeded);
        Assert.False(vm.ConnectionFailed);
        Assert.False(vm.IsTesting);
        Assert.Equal("sk-fresh-1234567890", catalog.LastKey);
        Assert.Equal("https://example.test/v1", catalog.LastProfile!.BaseUrl);
        Assert.NotSame(profile, catalog.LastProfile);
        Assert.Equal(23, vm.ModelOptions.Count);
    }

    [Fact]
    public async Task Test_connection_failure_shows_error_without_the_key()
    {
        var catalog = new FakeCatalog { Result = new ModelListResult(Array.Empty<ModelInfo>(), "401 Unauthorized: bad key sk-fresh-1234567890") };
        var vm = new ProfileViewModel(ProviderPresets.CreateProfile(ProviderPresets.OpenAiId), catalog) { NewApiKey = "sk-fresh-1234567890" };
        await vm.TestConnectionAsync(CancellationToken.None);
        Assert.False(vm.ConnectionOk);
        Assert.True(vm.ConnectionFailed);
        Assert.StartsWith("Failed: 401 Unauthorized", vm.ConnectionStatus);
        Assert.DoesNotContain("sk-fresh-1234567890", vm.ConnectionStatus);

        catalog.Throw = new HttpRequestException("connection refused");
        await vm.TestConnectionAsync(CancellationToken.None);
        Assert.False(vm.ConnectionOk);
        Assert.Contains("connection refused", vm.ConnectionStatus);
    }

    [Fact]
    public async Task Refresh_models_fills_picker_with_capability_hints()
    {
        var catalog = new FakeCatalog
        {
            Result = new ModelListResult(new[]
            {
                new ModelInfo("qwen2.5vl:7b", "Qwen VL", true, true, null),
                new ModelInfo("deepseek-r1:8b", "R1", false, true, true),
            }, null),
        };
        var vm = new ProfileViewModel(ProviderPresets.CreateProfile(ProviderPresets.OllamaId), catalog);
        Assert.Contains(vm.ModelOptions, o => o.Id == "qwen2.5vl:7b"); // preset suggestions before any query

        await vm.RefreshModelsAsync(CancellationToken.None);
        Assert.Equal(new[] { "qwen2.5vl:7b", "deepseek-r1:8b" }, vm.ModelOptions.Select(o => o.Id));
        Assert.Equal("vision", vm.ModelOptions[0].Hint);
        Assert.Equal("text only, thinking", vm.ModelOptions[1].Hint);
        Assert.Equal("2 models available.", vm.ModelStatus);
        Assert.True(vm.ModelsLoadedOnce);

        vm.PickModel(vm.ModelOptions[1]);
        Assert.Equal("deepseek-r1:8b", vm.Model);
        Assert.Contains("turn off", vm.ModelHint);
        vm.SupportsVision = false;
        Assert.Equal("text only, thinking", vm.ModelHint);
        vm.PickModel(null);
        Assert.Equal("deepseek-r1:8b", vm.Model);
    }

    // ------------------------------------------------------------------ settings view model

    [Fact]
    public void Settings_vm_loads_text_fields_and_commits_them()
    {
        using var vm = new SettingsViewModel(SettingsStore.CreateDefault());
        Assert.Equal("80", vm.MaxStepsText);
        Assert.Equal("1280", vm.MaxImageWidthText);
        Assert.Contains(".md", vm.VaultExtensionsText);
        Assert.Contains("\\bsudo\\b", vm.ElevationPatternsText);
        Assert.Contains("gparted", vm.ElevatedTargetsText);

        vm.MaxStepsText = "42";
        vm.MaxImageWidthText = "1600";
        vm.MaxImageHeightText = "900";
        vm.GridSpacingText = "100";
        vm.SettleDelayText = "300";
        vm.ScreenshotsToKeepText = "5";
        vm.TypingDelayText = "0";
        vm.VaultMaxResultsText = "12";
        vm.VaultMaxReadLinesText = "250";
        vm.VaultExtensionsText = "md, txt";
        vm.VaultExcludeText = ".git, Private";
        vm.BlockedProcessesText = "keepassxc, /opt/1Password/1password";
        vm.ElevationPatternsText = "\\bsudo\\b";
        vm.ElevatedTargetsText = "gparted\r\nsynaptic";

        Assert.True(vm.TryCommit(), vm.ErrorSummary);
        var s = vm.Settings;
        Assert.Equal(42, s.Safety.MaxStepsPerTurn);
        Assert.Equal(1600, s.Screen.MaxImageWidth);
        Assert.Equal(900, s.Screen.MaxImageHeight);
        Assert.Equal(100, s.Screen.GridSpacing);
        Assert.Equal(300, s.Screen.ActionSettleDelayMs);
        Assert.Equal(5, s.Screen.ScreenshotsToKeep);
        Assert.Equal(0, s.Screen.TypingDelayMs);
        Assert.Equal(12, s.Vault.MaxSearchResults);
        Assert.Equal(250, s.Vault.MaxReadLines);
        Assert.Equal(new[] { ".md", ".txt" }, s.Vault.IncludeExtensions);
        Assert.Equal(new[] { ".git", "Private" }, s.Vault.ExcludeFolders);
        Assert.Equal(new[] { "keepassxc", "1password" }, s.Safety.BlockedProcesses);
        Assert.Equal(new[] { "\\bsudo\\b" }, s.Safety.ElevationTextPatterns);
        Assert.Equal(new[] { "gparted", "synaptic" }, s.Safety.ElevatedLaunchTargets);
        Assert.Equal("", vm.ErrorSummary);
    }

    [Fact]
    public void Settings_vm_write_through_values()
    {
        using var vm = new SettingsViewModel(SettingsStore.CreateDefault());
        vm.UserInstructions = "Use Firefox.";
        vm.AllowAdmin = true;
        vm.Confirm = ConfirmMode.RiskyOnly;
        vm.AllowAppLaunch = false;
        vm.AllowShellCommands = true;
        vm.AllowClipboard = false;
        vm.DryRun = true;
        vm.FailsafeCorner = false;
        vm.StopOnUserMouseMove = true;
        vm.MonitorSelection = MonitorSelection.Specific;
        vm.MonitorIndex = 1;
        vm.ImageFormat = "PNG";
        vm.JpegQuality = 500;
        vm.DrawCursor = false;
        vm.Coordinates = CoordinateMode.Normalized1000;
        vm.ScreenshotAfterAction = false;
        vm.MinimizeWhileWorking = false;
        vm.ShowOverlay = false;
        vm.SelectedOverlayPosition = SettingsViewModel.OverlayPositions.Single(o => o.Value == OverlayCorner.TopCenter);
        vm.CloseToTray = true;
        vm.ShowThinking = false;
        vm.ShowScreenshots = false;
        vm.StartAtLogin = true;
        vm.VaultEnabled = false;
        vm.VaultAllowWrites = true;
        vm.ShowWelcomeAgain = false;
        vm.AdvancedMode = true;
        vm.CustomSystemPrompt = "Custom {{OS_TIPS}}";

        var s = vm.Settings;
        Assert.Equal("Use Firefox.", s.Prompt.UserInstructions);
        Assert.True(s.Safety.AllowAdmin);
        Assert.Equal(ConfirmMode.RiskyOnly, s.Safety.Confirm);
        Assert.False(s.Safety.AllowAppLaunch);
        Assert.True(s.Safety.AllowShellCommands);
        Assert.False(s.Safety.AllowClipboard);
        Assert.True(s.Safety.DryRun);
        Assert.False(s.Safety.FailsafeCorner);
        Assert.True(s.Safety.StopOnUserMouseMove);
        Assert.Equal(MonitorSelection.Specific, s.Screen.Monitor);
        Assert.True(vm.IsSpecificMonitor);
        Assert.Equal(1, s.Screen.MonitorIndex);
        Assert.Equal("png", s.Screen.Format);
        Assert.False(vm.IsJpeg);
        Assert.Equal(100, s.Screen.JpegQuality);
        vm.JpegQualitySlider = 61.6;
        Assert.Equal(62, s.Screen.JpegQuality);
        Assert.False(s.Screen.DrawCursor);
        Assert.Equal(CoordinateMode.Normalized1000, s.Screen.Coordinates);
        Assert.False(s.Screen.ScreenshotAfterAction);
        Assert.False(s.Ui.MinimizeWhileWorking);
        Assert.False(s.Ui.ShowOverlay);
        Assert.Equal(OverlayCorner.TopCenter, s.Ui.OverlayPosition);
        Assert.Equal("Top center", vm.SelectedOverlayPosition!.Label);
        Assert.True(s.Ui.CloseToTray);
        Assert.False(s.Ui.ShowThinking);
        Assert.False(s.Ui.ShowScreenshots);
        Assert.True(s.Ui.StartWithWindows);
        Assert.False(s.Vault.Enabled);
        Assert.True(s.Vault.AllowWrites);
        Assert.True(s.FirstRunCompleted);
        Assert.True(s.Ui.AdvancedMode);
        Assert.Equal("Custom {{OS_TIPS}}", s.Prompt.CustomSystemPrompt);
        vm.SelectedOverlayPosition = null; // ignored
        Assert.Equal(OverlayCorner.TopCenter, s.Ui.OverlayPosition);
    }

    [Fact]
    public void Every_setting_is_reachable_from_the_view_model()
    {
        // Each AppSettings value maps to a view-model member that edits it (WindowWidth/Height/Left/Top are the
        // main window's own geometry, SchemaVersion and the profile list's ids are not user settings).
        var notEditedHere = new HashSet<string> { "SchemaVersion", "WindowWidth", "WindowHeight", "WindowLeft", "WindowTop", "Id", "PresetId", "Kind" };
        var mapped = new Dictionary<string, string>
        {
            ["FirstRunCompleted"] = nameof(SettingsViewModel.ShowWelcomeAgain),
            ["ActiveProfileId"] = nameof(SettingsViewModel.SetActiveCommand),
            ["Profiles"] = nameof(SettingsViewModel.Profiles),
            ["Path"] = nameof(SettingsViewModel.VaultPath),
            ["Enabled"] = nameof(SettingsViewModel.VaultEnabled),
            ["AllowWrites"] = nameof(SettingsViewModel.VaultAllowWrites),
            ["MaxSearchResults"] = nameof(SettingsViewModel.VaultMaxResultsText),
            ["MaxReadLines"] = nameof(SettingsViewModel.VaultMaxReadLinesText),
            ["IncludeExtensions"] = nameof(SettingsViewModel.VaultExtensionsText),
            ["ExcludeFolders"] = nameof(SettingsViewModel.VaultExcludeText),
            ["MaxStepsPerTurn"] = nameof(SettingsViewModel.MaxStepsText),
            ["BlockedProcesses"] = nameof(SettingsViewModel.BlockedProcessesText),
            ["ElevationTextPatterns"] = nameof(SettingsViewModel.ElevationPatternsText),
            ["ElevatedLaunchTargets"] = nameof(SettingsViewModel.ElevatedTargetsText),
            ["Monitor"] = nameof(SettingsViewModel.MonitorSelection),
            ["MaxImageWidth"] = nameof(SettingsViewModel.MaxImageWidthText),
            ["MaxImageHeight"] = nameof(SettingsViewModel.MaxImageHeightText),
            ["Format"] = nameof(SettingsViewModel.ImageFormat),
            ["GridSpacing"] = nameof(SettingsViewModel.GridSpacingText),
            ["ActionSettleDelayMs"] = nameof(SettingsViewModel.SettleDelayText),
            ["ScreenshotsToKeep"] = nameof(SettingsViewModel.ScreenshotsToKeepText),
            ["TypingDelayMs"] = nameof(SettingsViewModel.TypingDelayText),
            ["StartWithWindows"] = nameof(SettingsViewModel.StartAtLogin),
            ["OverlayPosition"] = nameof(SettingsViewModel.OverlayPosition),
            ["MaxOutputTokens"] = nameof(ProfileViewModel.MaxOutputTokensText),
            ["Temperature"] = nameof(ProfileViewModel.TemperatureText),
            ["RequestTimeoutSeconds"] = nameof(ProfileViewModel.RequestTimeoutText),
            ["ThinkingBudgetTokens"] = nameof(ProfileViewModel.ThinkingBudgetText),
            ["ExtraEnv"] = nameof(ProfileViewModel.ExtraEnvText),
            ["ExtraHeaders"] = nameof(ProfileViewModel.ExtraHeadersText),
            ["ApiKeyProtected"] = nameof(ProfileViewModel.NewApiKey),
        };
        var vmMembers = typeof(SettingsViewModel).GetProperties().Select(p => p.Name)
            .Concat(typeof(ProfileViewModel).GetProperties().Select(p => p.Name)).ToHashSet();
        var settingTypes = new[] { typeof(AppSettings), typeof(ProviderProfile), typeof(VaultSettings), typeof(SafetySettings), typeof(ScreenSettings), typeof(UiSettings), typeof(PromptSettings) };
        var missing = new List<string>();
        foreach (var type in settingTypes)
            foreach (var prop in type.GetProperties().Where(p => p.CanWrite))
            {
                if (prop.PropertyType.Namespace == typeof(AppSettings).Namespace && prop.PropertyType.IsClass) continue; // the section objects themselves
                if (notEditedHere.Contains(prop.Name)) continue;
                var member = mapped.GetValueOrDefault(prop.Name, prop.Name);
                if (!vmMembers.Contains(member)) missing.Add($"{type.Name}.{prop.Name}");
            }
        Assert.True(missing.Count == 0, "Not editable: " + string.Join(", ", missing));
    }

    [Fact]
    public void Settings_vm_validation_navigates_to_the_first_error()
    {
        using var vm = new SettingsViewModel(SettingsStore.CreateDefault());
        vm.SelectSection(SettingsViewModel.SectionVault);
        vm.MaxStepsText = "0";
        vm.GridSpacingText = "5";
        vm.ScreenshotsToKeepText = "x";

        Assert.False(vm.TryCommit());
        Assert.NotEqual("", vm.Errors[nameof(SettingsViewModel.MaxStepsText)]);
        Assert.NotEqual("", vm.Errors[nameof(SettingsViewModel.GridSpacingText)]);
        Assert.NotEqual("", vm.Errors[nameof(SettingsViewModel.ScreenshotsToKeepText)]);
        Assert.Equal(SettingsViewModel.SectionSafety, vm.SelectedSectionKey);
        Assert.StartsWith("Safety: max actions per request", vm.ErrorSummary);
        Assert.Contains("(2 more)", vm.ErrorSummary);
        Assert.Equal(80, vm.Settings.Safety.MaxStepsPerTurn);

        vm.MaxStepsText = "10";
        vm.GridSpacingText = "0";
        vm.ScreenshotsToKeepText = "2";
        Assert.True(vm.TryCommit());
        Assert.False(vm.Errors.HasErrors);
        Assert.Equal("", vm.ErrorSummary);
        Assert.Equal(10, vm.Settings.Safety.MaxStepsPerTurn);
    }

    [Fact]
    public void Settings_vm_profile_error_selects_the_profile()
    {
        using var vm = new SettingsViewModel(SettingsStore.CreateDefault());
        var openai = vm.AddPreset(ProviderPresets.OpenAiId);
        openai.BaseUrl = "nope";
        vm.SelectedProfile = vm.Profiles[0];
        vm.SelectSection(SettingsViewModel.SectionScreen);

        Assert.False(vm.TryCommit());
        Assert.Same(openai, vm.SelectedProfile);
        Assert.Equal(SettingsViewModel.SectionModels, vm.SelectedSectionKey);
        Assert.Contains("Base URL", vm.ErrorSummary);
    }

    [Fact]
    public void Settings_vm_advanced_profile_error_points_to_advanced_mode()
    {
        using var vm = new SettingsViewModel(SettingsStore.CreateDefault());
        var openai = vm.AddPreset(ProviderPresets.OpenAiId);
        openai.ExtraHeadersText = "bad header line";
        Assert.False(vm.TryCommit());
        Assert.Contains("Turn on Advanced mode", vm.ErrorSummary);
        Assert.Equal(SettingsViewModel.SectionModels, vm.SelectedSectionKey);

        vm.AdvancedMode = true;
        Assert.False(vm.TryCommit());
        Assert.Equal(SettingsViewModel.SectionAdvanced, vm.SelectedSectionKey);
    }

    [Fact]
    public void Settings_vm_rejects_bad_hotkey_and_patterns()
    {
        var settings = SettingsStore.CreateDefault();
        settings.Ui.StopHotkey = "Q";
        using var vm = new SettingsViewModel(settings);
        vm.AdvancedMode = true;
        vm.ElevationPatternsText = "(";
        Assert.False(vm.TryCommit());
        Assert.NotEqual("", vm.Errors[nameof(SettingsViewModel.StopHotkey)]);
        Assert.NotEqual("", vm.Errors[nameof(SettingsViewModel.ElevationPatternsText)]);
    }

    [Fact]
    public void Settings_vm_specific_monitor_must_exist_once_detected()
    {
        using var vm = new SettingsViewModel(SettingsStore.CreateDefault());
        vm.ApplyReport(MakeReport());
        vm.MonitorSelection = MonitorSelection.Specific;
        vm.MonitorIndex = 3;
        Assert.Contains(vm.MonitorOptions, o => o.Index == 3 && o.Label.Contains("not connected"));
        Assert.False(vm.TryCommit());
        Assert.NotEqual("", vm.Errors[nameof(SettingsViewModel.MonitorIndex)]);
        vm.SelectedMonitorOption = vm.MonitorOptions.Single(o => o.Index == 1);
        Assert.Equal(1, vm.MonitorIndex);
        Assert.True(vm.TryCommit());
        vm.SelectedMonitorOption = null; // ignored
        Assert.Equal(1, vm.MonitorIndex);
    }

    [Fact]
    public void Settings_vm_reverting_a_committed_value_applies_it()
    {
        using var vm = new SettingsViewModel(SettingsStore.CreateDefault());
        vm.MaxStepsText = "42";
        vm.BlockedProcessesText = "keepassxc";
        Assert.True(vm.TryCommit());
        Assert.Equal(42, vm.Settings.Safety.MaxStepsPerTurn);

        vm.MaxStepsText = "80";
        vm.BlockedProcessesText = "";
        Assert.True(vm.TryCommit());
        Assert.Equal(80, vm.Settings.Safety.MaxStepsPerTurn);
        Assert.Empty(vm.Settings.Safety.BlockedProcesses);
    }

    [Fact]
    public void Settings_vm_untouched_hand_edited_values_still_save()
    {
        var settings = SettingsStore.CreateDefault();
        settings.Vault.IncludeExtensions = new List<string>();
        settings.Screen.GridSpacing = 3;
        using var vm = new SettingsViewModel(settings);
        Assert.True(vm.TryCommit(), vm.ErrorSummary);
        Assert.Empty(vm.Settings.Vault.IncludeExtensions);
    }

    [Fact]
    public void Settings_vm_edits_a_copy_and_round_trips_through_the_store()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var store = new SettingsStore(path);
            using var vm = new SettingsViewModel(store.CloneCurrent());
            var ollama = vm.AddPreset(ProviderPresets.OllamaId, model: "gemma3:12b");
            vm.SetSelectedActive();
            ollama.MaxOutputTokensText = "2048";
            ollama.NewApiKey = "local-key-12345";
            vm.MaxStepsText = "33";
            vm.StopHotkey = "Ctrl+Shift+F12";
            vm.VaultPath = "~/Notes";

            Assert.Single(store.Current.Profiles); // not saved yet
            Assert.True(vm.TryCommit(), vm.ErrorSummary);
            store.Save(SettingsStore.Clone(vm.Settings));

            var reloaded = new SettingsStore(path).Current;
            Assert.Equal(2, reloaded.Profiles.Count);
            Assert.Equal(ollama.Id, reloaded.ActiveProfileId);
            var p = reloaded.ActiveProfile!;
            Assert.Equal(ProviderKind.Ollama, p.Kind);
            Assert.Equal("gemma3:12b", p.Model);
            Assert.Equal(2048, p.MaxOutputTokens);
            Assert.Equal("local-key-12345", SecretProtector.Unprotect(p.ApiKeyProtected));
            Assert.DoesNotContain("local-key-12345", File.ReadAllText(path));
            Assert.Equal(33, reloaded.Safety.MaxStepsPerTurn);
            Assert.Equal("Ctrl+Shift+F12", reloaded.Ui.StopHotkey);
            Assert.False(reloaded.Vault.Path.StartsWith('~'));
            Assert.EndsWith("Notes", reloaded.Vault.Path);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public void Profiles_add_duplicate_delete_and_activate()
    {
        using var vm = new SettingsViewModel(SettingsStore.CreateDefault());
        var claude = vm.Profiles[0];
        Assert.True(claude.IsActive);
        Assert.Same(claude, vm.SelectedProfile);
        Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.False(vm.DeleteSelected());
        Assert.False(vm.SetActiveCommand.CanExecute(null));

        var groq = vm.AddPreset(ProviderPresets.GroqId);
        Assert.Equal(2, vm.Profiles.Count);
        Assert.Equal(2, vm.Settings.Profiles.Count);
        Assert.Same(groq, vm.SelectedProfile);
        Assert.False(groq.IsActive);
        Assert.True(vm.DeleteCommand.CanExecute(null));
        Assert.True(vm.SetActiveCommand.CanExecute(null));

        var second = vm.AddPreset(ProviderPresets.GroqId);
        Assert.Equal(groq.Name + " 2", second.Name);

        vm.SelectedProfile = groq;
        groq.NewApiKey = "gsk-pending-0987654321";
        groq.MaxOutputTokensText = "1234";
        var copy = vm.DuplicateSelected()!;
        Assert.NotEqual(groq.Id, copy.Id);
        Assert.Equal(groq.Name + " (copy)", copy.Name);
        Assert.Equal(groq.Model, copy.Model);
        Assert.Equal(1234, copy.Profile.MaxOutputTokens);
        Assert.Equal("gsk-pending-0987654321", SecretProtector.Unprotect(copy.Profile.ApiKeyProtected));
        Assert.Equal(8192, groq.Profile.MaxOutputTokens); // the source keeps its pending edit uncommitted

        vm.SelectedProfile = copy;
        vm.SetSelectedActive();
        Assert.Equal(copy.Id, vm.Settings.ActiveProfileId);
        Assert.True(copy.IsActive);
        Assert.False(claude.IsActive);
        Assert.Same(copy, vm.ActiveProfile);

        Assert.True(vm.DeleteSelected());
        Assert.DoesNotContain(copy, vm.Profiles);
        Assert.DoesNotContain(vm.Settings.Profiles, p => p.Id == copy.Id);
        Assert.Equal(vm.Profiles[0].Id, vm.Settings.ActiveProfileId);
        Assert.True(vm.Profiles[0].IsActive);
        Assert.NotNull(vm.SelectedProfile);
        Assert.Contains(vm.SelectedProfile, vm.Profiles);
    }

    [Fact]
    public void Add_command_rejects_unknown_presets()
    {
        using var vm = new SettingsViewModel(SettingsStore.CreateDefault());
        Assert.False(vm.AddPresetCommand.CanExecute("no-such-preset"));
        Assert.True(vm.AddPresetCommand.CanExecute(ProviderPresets.MistralId));
        vm.AddPresetCommand.Execute(ProviderPresets.MistralId);
        Assert.Equal(ProviderKind.OpenAiCompatible, vm.SelectedProfile!.Kind);
        Assert.Equal(ProviderPresets.MistralId, vm.SelectedProfile.Profile.PresetId);
    }

    [Fact]
    public void Preset_groups_cover_every_preset_once()
    {
        var ids = SettingsViewModel.PresetGroups.SelectMany(g => g.Presets).Select(p => p.Id).ToList();
        Assert.Equal(ProviderPresets.All.Select(p => p.Id).OrderBy(x => x), ids.OrderBy(x => x));
        var groups = SettingsViewModel.PresetGroups.ToDictionary(g => g.Name, g => g.Presets.Select(p => p.Id).ToList());
        Assert.Equal(new[] { "Cloud", "Local", "Agents" }, groups.Keys);
        Assert.Contains(ProviderPresets.OpenAiId, groups["Cloud"]);
        Assert.Contains(ProviderPresets.AnthropicApiId, groups["Cloud"]);
        Assert.Contains(ProviderPresets.OllamaId, groups["Local"]);
        Assert.Contains(ProviderPresets.LmStudioId, groups["Local"]);
        Assert.Contains(ProviderPresets.ClaudeSubscriptionId, groups["Agents"]);
        Assert.Contains(ProviderPresets.GeminiCliId, groups["Agents"]);
    }

    [Fact]
    public void Advanced_section_follows_advanced_mode_live()
    {
        using var vm = new SettingsViewModel(SettingsStore.CreateDefault());
        Assert.DoesNotContain(vm.VisibleSections, s => s.Key == SettingsViewModel.SectionAdvanced);
        Assert.Equal(6, vm.VisibleSections.Count);

        vm.AdvancedMode = true;
        Assert.Contains(vm.VisibleSections, s => s.Key == SettingsViewModel.SectionAdvanced);
        vm.SelectSection(SettingsViewModel.SectionAdvanced);
        Assert.Equal(SettingsViewModel.SectionAdvanced, vm.SelectedSectionKey);

        vm.AdvancedMode = false;
        Assert.DoesNotContain(vm.VisibleSections, s => s.Key == SettingsViewModel.SectionAdvanced);
        Assert.Equal(SettingsViewModel.SectionInterface, vm.SelectedSectionKey);
        Assert.Same(vm.SelectedSection, vm.VisibleSections.Single(s => s.Key == SettingsViewModel.SectionInterface));

        vm.SelectSection(SettingsViewModel.SectionVault);
        vm.AdvancedMode = true;
        Assert.Equal(SettingsViewModel.SectionVault, vm.SelectedSectionKey);
    }

    [Fact]
    public void Prompt_editor_commands_and_preview()
    {
        using var vm = new SettingsViewModel(SettingsStore.CreateDefault());
        Assert.Contains("built-in", vm.PromptStatus);
        vm.LoadBuiltInPromptCommand.Execute(null);
        Assert.Equal(DefaultPrompts.ComputerUse, vm.CustomSystemPrompt);
        Assert.Equal(DefaultPrompts.ComputerUse, vm.Settings.Prompt.CustomSystemPrompt);
        vm.ResetPromptCommand.Execute(null);
        Assert.Equal("", vm.CustomSystemPrompt);

        Assert.Equal(PromptBuilder.Placeholders.Count, vm.Placeholders.Count);
        Assert.Contains(vm.Placeholders, p => p.Name == "{{USER_INSTRUCTIONS}}" && p.Description.Length > 0);
        Assert.Contains(vm.Placeholders, p => p.Name == "{{OS_TIPS}}" && p.Description.Length > 0);

        vm.UserInstructions = "Always use Firefox.";
        vm.MaxStepsText = "42"; // not committed: the preview still uses it
        var preview = vm.BuildPreviewPrompt(new DateTime(2026, 10, 7, 9, 30, 0));
        Assert.DoesNotContain("{{", preview);
        Assert.Contains("Always use Firefox.", preview);
        Assert.Contains("at most 42 actions", preview);
        Assert.Contains("Wednesday, October 7, 2026", preview);
        Assert.Equal(80, vm.Settings.Safety.MaxStepsPerTurn);

        vm.CustomSystemPrompt = "Custom: ";
        var caret = vm.InsertIntoPrompt("{{MAX_STEPS}}", 8, 0);
        Assert.Equal("Custom: {{MAX_STEPS}}", vm.CustomSystemPrompt);
        Assert.Equal(21, caret);
        Assert.Equal("Custom: 42", vm.BuildPreviewPrompt(DateTime.Now).Trim());
        Assert.Contains("Custom prompt", vm.PromptStatus);
    }

    [Fact]
    public void Sample_screen_description_fits_the_image_box()
    {
        var s = SettingsStore.CreateDefault();
        var monitors = MakeReport().Monitors;
        Assert.Equal("screenshots are 1280x720 and show the primary monitor (2560x1440 physical pixels)", SettingsViewModel.DescribeSampleScreen(s, monitors));

        s.Screen.Monitor = MonitorSelection.Specific;
        s.Screen.MonitorIndex = 1;
        Assert.Equal("screenshots are 1067x800 and show monitor 2 (1440x1080 physical pixels)", SettingsViewModel.DescribeSampleScreen(s, monitors));

        s.Screen.Monitor = MonitorSelection.AllMonitors;
        Assert.Contains("all monitors (4000x1440", SettingsViewModel.DescribeSampleScreen(s, monitors));

        s.Screen.Monitor = MonitorSelection.Primary;
        Assert.Contains("(1920x1080 physical pixels)", SettingsViewModel.DescribeSampleScreen(s, null));
    }

    [Fact]
    public async Task Vault_test_search_uses_the_edited_settings()
    {
        var dir = NewTempDir();
        try
        {
            var host = new FakeToolHost { Result = ToolResult.Ok("notes/setup.md:3 Firefox is my browser") };
            AppSettings? seen = null;
            using var vm = new SettingsViewModel(SettingsStore.CreateDefault())
            {
                VaultHostFactory = getSettings => { seen = getSettings(); return host; },
            };

            await vm.RunVaultTestAsync(CancellationToken.None);
            Assert.Contains("Type a few words", vm.VaultTestResult);

            vm.VaultTestQuery = "browser";
            await vm.RunVaultTestAsync(CancellationToken.None);
            Assert.Contains("Choose a vault folder", vm.VaultTestResult);

            vm.VaultPath = Path.Combine(dir, "missing");
            await vm.RunVaultTestAsync(CancellationToken.None);
            Assert.Contains("does not exist", vm.VaultTestResult);
            Assert.Null(host.LastName);

            vm.VaultPath = dir;
            vm.VaultEnabled = false;
            vm.VaultMaxResultsText = "5";
            await vm.RunVaultTestAsync(CancellationToken.None);

            Assert.Equal("vault_search", host.LastName);
            Assert.Equal("browser", host.LastArgs.GetProperty("query").GetString());
            Assert.Equal(5, host.LastArgs.GetProperty("max_results").GetInt32());
            Assert.NotNull(seen);
            Assert.Equal(dir, seen!.Vault.Path);
            Assert.True(seen.Vault.Enabled);
            Assert.Equal("notes/setup.md:3 Firefox is my browser", vm.VaultTestResult);
            Assert.False(vm.IsVaultTesting);
            Assert.False(vm.Settings.Vault.Enabled); // the edited copy keeps the user's choice

            host.Result = ToolResult.Error("index failed");
            await vm.RunVaultTestAsync(CancellationToken.None);
            Assert.Equal("Error: index failed", vm.VaultTestResult);

            host.Throw = new NotImplementedException("not yet");
            await vm.RunVaultTestAsync(CancellationToken.None);
            Assert.Contains("Search failed", vm.VaultTestResult);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task Vault_test_search_runs_the_real_vault_search()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "browsers.md"), "# Browsers\nFirefox is the browser I use for everything.\n");
            using var vm = new SettingsViewModel(SettingsStore.CreateDefault()) { VaultPath = dir, VaultTestQuery = "firefox browser" };
            await vm.RunVaultTestAsync(CancellationToken.None);
            Assert.Contains("browsers.md", vm.VaultTestResult);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public void Vault_detected_vault_fills_path_and_clear_empties_it()
    {
        using var vm = new SettingsViewModel(SettingsStore.CreateDefault());
        Assert.Contains("No vault", vm.VaultPathStatus);
        vm.ApplyReport(MakeReport(vaults: new[] { "V1", "V2" }.Select(v => Path.Combine(Path.GetTempPath(), v)).ToArray()));
        Assert.True(vm.HasDetectedVaults);
        vm.SelectedDetectedVault = vm.DetectedVaults[1];
        Assert.Equal(vm.DetectedVaults[1], vm.VaultPath);
        Assert.Equal(vm.DetectedVaults[1], vm.Settings.Vault.Path);
        vm.ClearVaultCommand.Execute(null);
        Assert.Equal("", vm.Settings.Vault.Path);
        vm.VaultPath = "relative/folder";
        Assert.Contains("full folder path", vm.VaultPathStatus);
        vm.VaultPath = "~";
        Assert.Equal("", vm.VaultPathStatus); // the home folder exists
        vm.VaultPath = Path.Combine(Path.GetTempPath(), "no-such-vault-" + Guid.NewGuid().ToString("N"));
        Assert.Contains("does not exist", vm.VaultPathStatus);
    }

    [Fact]
    public void Root_and_session_notes()
    {
        using var vm = new SettingsViewModel(SettingsStore.CreateDefault()) { IsRunningAsRoot = false };
        vm.ApplyReport(MakeReport(elevated: true));
        Assert.True(vm.IsRunningAsRoot);
        vm.ApplyReport(MakeReport(elevated: false));
        Assert.False(vm.IsRunningAsRoot);

        vm.Session = X11Session;
        Assert.False(vm.IsWayland);
        Assert.Equal("", vm.HotkeySessionNote);
        Assert.Equal("X11 session (GNOME)", vm.SessionText);

        vm.Session = WaylandSession;
        Assert.True(vm.IsWayland);
        Assert.Contains("Wayland", vm.HotkeySessionNote);
        Assert.Contains("Stop button", vm.HotkeySessionNote);
        Assert.Contains("top-left corner", vm.HotkeySessionNote);
        Assert.Equal("Wayland session (sway)", vm.SessionText);

        Assert.Equal("Wayland session (ubuntu, GNOME)", SettingsViewModel.DescribeSession(WaylandSession with { Desktop = "ubuntu:GNOME" }));
        Assert.Equal("No graphical session detected", SettingsViewModel.DescribeSession(new LinuxSessionInfo(LinuxSessionKind.None, null, null, null, null)));
    }

    // ------------------------------------------------------------------ detection

    [Fact]
    public void Detected_items_describe_the_environment()
    {
        var report = MakeReport(
            claude: new CliToolStatus("claude", "/home/me/.local/bin/claude", "2.1.293", false, null),
            ollama: new LocalServerStatus("Ollama", "http://127.0.0.1:11434", true, new[]
            {
                new ModelInfo("llama3.1:8b", "Llama", false, true, null),
                new ModelInfo("qwen2.5vl:7b", "Qwen VL", true, true, null),
            }),
            lmStudio: new LocalServerStatus("LM Studio", "http://127.0.0.1:1234", true, new[] { new ModelInfo("some-model", "x", null, null, null) }),
            envVars: new[] { "OPENAI_API_KEY", "SOMETHING_ELSE_KEY" });

        var items = SettingsViewModel.BuildDetectedItems(report);
        var claude = items.Single(i => i.Title == "Claude Code");
        Assert.True(claude.IsFound);
        Assert.Equal(ProviderPresets.ClaudeSubscriptionId, claude.PresetId);
        Assert.Contains("2.1.293", claude.Detail);
        Assert.Contains("claude auth login", claude.Detail);

        Assert.False(items.Single(i => i.Title == "Gemini CLI").CanAdd);

        var ollama = items.Single(i => i.Title == "Ollama");
        Assert.Equal(ProviderPresets.OllamaId, ollama.PresetId);
        Assert.Equal("qwen2.5vl:7b", ollama.Model);
        Assert.Equal("http://127.0.0.1:11434", ollama.BaseUrl);
        Assert.Contains("2 models (1 with vision)", ollama.Detail);

        var lm = items.Single(i => i.Title == "LM Studio");
        Assert.Equal("http://127.0.0.1:1234/v1", lm.BaseUrl);
        Assert.Equal("some-model", lm.Model);

        Assert.Equal(ProviderPresets.OpenAiId, items.Single(i => i.Title == "OPENAI_API_KEY").PresetId);
        Assert.False(items.Single(i => i.Title == "SOMETHING_ELSE_KEY").CanAdd);
        Assert.All(items, i => Assert.DoesNotContain("sk-", i.Detail));
    }

    [Fact]
    public void Detected_items_when_nothing_is_installed()
    {
        var items = SettingsViewModel.BuildDetectedItems(MakeReport(claude: new CliToolStatus("claude", null, null, null, null)));
        var claude = items.Single(i => i.Title == "Claude Code");
        Assert.False(claude.IsFound);
        Assert.False(claude.CanAdd);
        Assert.Contains("Install Claude Code", claude.Detail);
        Assert.False(items.Single(i => i.Title == "Ollama").CanAdd);
    }

    [Fact]
    public void Partial_reports_never_break_the_windows()
    {
        var partial = new EnvironmentReport(null!, null!, null!, null!,
            new LocalServerStatus("LM Studio", null!, true, null!), null!, null!, null!, false);
        var items = SettingsViewModel.BuildDetectedItems(partial);
        Assert.Contains(items, i => i.Title == "Claude Code" && !i.IsFound);
        Assert.Contains(items, i => i.Title == "LM Studio" && i.CanAdd);

        using var vm = new SettingsViewModel(SettingsStore.CreateDefault());
        vm.ApplyReport(partial);
        var added = vm.AddPreset(ProviderPresets.ClaudeSubscriptionId);
        Assert.Null(added.DetectedCliPath);
        Assert.Single(vm.MonitorOptions);

        var welcome = new SettingsWelcomeViewModel(SettingsStore.CreateDefault(), partial, null, null);
        Assert.Equal(WelcomeClaudeState.NotFound, welcome.ClaudeState);
        Assert.Contains(welcome.OtherProviders, l => l.Contains("LM Studio"));
        Assert.Empty(welcome.SetupNotes);
    }

    [Fact]
    public void PickModel_prefers_vision()
    {
        var models = new[] { new ModelInfo("text", "t", false, true, null), new ModelInfo("qwen2.5vl:7b", "v", true, true, null) };
        Assert.Equal("qwen2.5vl:7b", SettingsViewModel.PickModel(models, "default"));
        Assert.Equal("b", SettingsViewModel.PickModel(new[] { new ModelInfo("a", "a", null, null, null), new ModelInfo("b", "b", null, null, null) }, "b"));
        Assert.Equal("a", SettingsViewModel.PickModel(new[] { new ModelInfo("a", "a", null, null, null) }, "zzz"));
        Assert.Equal("default", SettingsViewModel.PickModel(Array.Empty<ModelInfo>(), "default"));
        Assert.Null(SettingsViewModel.PickModel(Array.Empty<ModelInfo>(), ""));
    }

    [Fact]
    public async Task Detection_populates_panels_and_add_buttons_create_profiles()
    {
        var detector = new FakeDetector
        {
            Report = MakeReport(
                claude: new CliToolStatus("claude", "/home/me/.local/bin/claude", "2.1.293", true, "claude.ai, max"),
                ollama: new LocalServerStatus("Ollama", "http://127.0.0.1:11434", true, new[] { new ModelInfo("gemma3:12b", "g", true, true, null) }),
                vaults: new[] { "/home/me/Vault A" }),
        };
        using var vm = new SettingsViewModel(SettingsStore.CreateDefault(), detector: detector);
        await vm.DetectEnvironmentAsync();

        Assert.Equal(1, detector.Calls);
        Assert.False(vm.IsDetecting);
        Assert.Equal("", vm.DetectionStatus);
        Assert.NotNull(vm.Report);
        Assert.Single(vm.DetectedVaults);
        Assert.Equal(2, vm.MonitorOptions.Count);
        Assert.Equal("Monitor 1 (eDP-1): 2560 x 1440, 150% scale, primary", vm.MonitorOptions[0].Label);
        Assert.Contains("/home/me/.local/bin/claude", vm.Profiles[0].CliHint);
        Assert.Contains("Logged in (claude.ai, max)", vm.DetectedItems.Single(i => i.Title == "Claude Code").Detail);

        var ollamaRow = vm.DetectedItems.Single(i => i.Title == "Ollama");
        Assert.True(ollamaRow.AddCommand!.CanExecute(null));
        ollamaRow.AddCommand.Execute(null);
        var added = vm.SelectedProfile!;
        Assert.Equal(ProviderKind.Ollama, added.Kind);
        Assert.Equal("gemma3:12b", added.Model);
        Assert.Equal(2, vm.Settings.Profiles.Count);
    }

    [Fact]
    public async Task Detection_failure_is_reported_not_thrown()
    {
        var detector = new FakeDetector { Throw = new InvalidOperationException("boom") };
        using var vm = new SettingsViewModel(SettingsStore.CreateDefault(), detector: detector);
        await vm.DetectEnvironmentAsync();
        Assert.False(vm.IsDetecting);
        Assert.Equal("Detection failed: boom", vm.DetectionStatus);
        Assert.Empty(vm.DetectedItems);
    }

    [Fact]
    public async Task Start_loads_models_once_per_profile()
    {
        var catalog = new FakeCatalog { Result = new ModelListResult(new[] { new ModelInfo("haiku", "Haiku", true, true, true) }, null) };
        var detector = new FakeDetector { Report = MakeReport() };
        using var vm = new SettingsViewModel(SettingsStore.CreateDefault(), catalog, detector);
        var second = vm.AddPreset(ProviderPresets.AnthropicApiId);
        vm.SelectedProfile = vm.Profiles[0];
        Assert.Equal(0, catalog.Calls); // nothing is fetched before the window opens

        await vm.StartAsync();
        Assert.Equal(1, catalog.Calls);
        Assert.Equal(1, detector.Calls);

        vm.SelectedProfile = second;
        await WaitUntil(() => catalog.Calls == 2 && !second.IsLoadingModels);
        vm.SelectedProfile = vm.Profiles[0];
        vm.SelectedProfile = second;
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(2, catalog.Calls);
    }

    // ------------------------------------------------------------------ welcome

    [Fact]
    public void Welcome_describes_claude_states()
    {
        var current = SettingsStore.CreateDefault();
        var ok = new SettingsWelcomeViewModel(current, MakeReport(claude: new CliToolStatus("claude", "c", "2.1.293", true, "max")));
        Assert.Equal(WelcomeClaudeState.LoggedIn, ok.ClaudeState);
        Assert.True(ok.ClaudeReady);
        Assert.Equal("", ok.ClaudeCommand);
        Assert.False(ok.HasClaudeCommand);
        Assert.Contains("logged in", ok.ClaudeTitle);

        var notLoggedIn = new SettingsWelcomeViewModel(current, MakeReport(claude: new CliToolStatus("claude", "c", "2.1.293", false, null)));
        Assert.Equal(WelcomeClaudeState.NotLoggedIn, notLoggedIn.ClaudeState);
        Assert.False(notLoggedIn.ClaudeReady);
        Assert.Equal("claude auth login", notLoggedIn.ClaudeCommand);

        var missing = new SettingsWelcomeViewModel(current, MakeReport(claude: new CliToolStatus("claude", null, null, null, null)));
        Assert.Equal(WelcomeClaudeState.NotFound, missing.ClaudeState);
        Assert.Contains("Install Claude Code", missing.ClaudeDetail);

        var unknown = new SettingsWelcomeViewModel(current, MakeReport(claude: new CliToolStatus("claude", "c", null, null, null)));
        Assert.Equal(WelcomeClaudeState.LoginUnknown, unknown.ClaudeState);
    }

    [Fact]
    public void Welcome_summarizes_other_providers_and_safety()
    {
        var current = SettingsStore.CreateDefault();
        current.Ui.StopHotkey = "Ctrl+Shift+F9";
        var vm = new SettingsWelcomeViewModel(current, MakeReport(
            ollama: new LocalServerStatus("Ollama", "http://127.0.0.1:11434", true, new[] { new ModelInfo("a", "a", true, true, null), new ModelInfo("b", "b", false, true, null) }),
            gemini: new CliToolStatus("gemini", "g", "1.0", null, null),
            envVars: new[] { "GROQ_API_KEY" }), null, X11Session);
        Assert.True(vm.HasOtherProviders);
        Assert.Contains(vm.OtherProviders, l => l.Contains("Ollama is running with 2 models, 1 with vision"));
        Assert.Contains(vm.OtherProviders, l => l.Contains("Gemini CLI"));
        Assert.Contains(vm.OtherProviders, l => l.Contains("GROQ_API_KEY"));
        Assert.Contains("Ctrl+Shift+F9", vm.StopText);
        Assert.Contains("top-left corner", vm.StopText);
        Assert.Contains("off", vm.AdminText);
        Assert.Contains("sudo", vm.AdminText);
        Assert.Equal("", vm.RootWarning);

        current.Safety.AllowAdmin = true;
        current.Safety.FailsafeCorner = false;
        var admin = new SettingsWelcomeViewModel(current, MakeReport(elevated: true), null, X11Session);
        Assert.Contains("ON", admin.AdminText);
        Assert.Contains("password yourself", admin.AdminText);
        Assert.DoesNotContain("top-left", admin.StopText);
        Assert.Contains("not supported", admin.RootWarning);
    }

    [Fact]
    public void Welcome_shows_the_session_and_missing_tools()
    {
        var current = SettingsStore.CreateDefault();
        var notes = new[] { "  Screenshots need grim: sudo apt install grim  ", "", "Typing needs wtype: sudo apt install wtype" };
        var wayland = new SettingsWelcomeViewModel(current, MakeReport(), notes, WaylandSession);
        Assert.True(wayland.IsWayland);
        Assert.Equal("Wayland session (sway)", wayland.SessionTitle);
        Assert.True(wayland.HasSetupNotes);
        Assert.False(wayland.SetupComplete);
        Assert.Equal(new[] { "Screenshots need grim: sudo apt install grim", "Typing needs wtype: sudo apt install wtype" }, wayland.SetupNotes);
        Assert.Contains("no stop hotkey", wayland.StopText);
        Assert.Contains("Stop button", wayland.StopText);
        Assert.Contains("top-left corner", wayland.StopText);
        Assert.DoesNotContain(current.Ui.StopHotkey, wayland.StopText);

        var x11 = new SettingsWelcomeViewModel(current, MakeReport(), Array.Empty<string>(), X11Session);
        Assert.False(x11.IsWayland);
        Assert.True(x11.SetupComplete);
        Assert.Equal("X11 session (GNOME)", x11.SessionTitle);
        Assert.Contains("Ctrl+Alt+X", x11.StopText);

        var none = new SettingsWelcomeViewModel(current, MakeReport(), null, new LinuxSessionInfo(LinuxSessionKind.None, null, null, null, null));
        Assert.False(none.SetupComplete);
        Assert.Contains("DISPLAY", none.SessionDetail);
    }

    [Fact]
    public void Welcome_vault_choice_is_saved_with_first_run_flag()
    {
        var vaultA = Path.Combine(Path.GetTempPath(), "Vault A");
        var current = SettingsStore.CreateDefault();
        var vm = new SettingsWelcomeViewModel(current, MakeReport(vaults: new[] { vaultA }));
        Assert.Equal(2, vm.VaultOptions.Count);
        Assert.Same(vm.SkipOption, vm.SelectedVault);
        Assert.Equal("Vault A", vm.VaultOptions[1].Label);

        var skipped = SettingsStore.CreateDefault();
        vm.Apply(skipped);
        Assert.True(skipped.FirstRunCompleted);
        Assert.Equal("", skipped.Vault.Path);

        vm.SelectedVault = vm.VaultOptions[1];
        var chosen = SettingsStore.CreateDefault();
        chosen.Vault.Enabled = false;
        vm.Apply(chosen);
        Assert.Equal(vaultA, chosen.Vault.Path);
        Assert.True(chosen.Vault.Enabled);
        Assert.True(chosen.FirstRunCompleted);

        var other = Path.Combine(Path.GetTempPath(), "Other Notes");
        var custom = vm.AddCustomVault(other);
        Assert.Same(custom, vm.SelectedVault);
        Assert.Same(custom, vm.AddCustomVault(other + "  "));
        Assert.Equal(3, vm.VaultOptions.Count);
        vm.SelectedVault = null; // ignored
        Assert.Same(custom, vm.SelectedVault);
    }

    [Fact]
    public void Welcome_keeps_an_already_configured_vault()
    {
        var existing = Path.Combine(Path.GetTempPath(), "Existing Vault");
        var current = SettingsStore.CreateDefault();
        current.Vault.Path = existing;
        var vm = new SettingsWelcomeViewModel(current, MakeReport());
        Assert.Equal(existing, vm.SelectedVault!.Path);
    }

    [Fact]
    public void Welcome_get_started_through_store_update()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var store = new SettingsStore(path);
            var vault = Path.Combine(dir, "notes");
            var vm = new SettingsWelcomeViewModel(store.Current, MakeReport(vaults: new[] { vault }));
            vm.SelectedVault = vm.VaultOptions.Single(o => o.Path == vault);
            store.Update(vm.Apply);
            var reloaded = new SettingsStore(path).Current;
            Assert.True(reloaded.FirstRunCompleted);
            Assert.Equal(vault, reloaded.Vault.Path);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    // ------------------------------------------------------------------ autostart

    [Fact]
    public void Autostart_writes_a_desktop_entry_only_in_the_given_folder()
    {
        var dir = NewTempDir();
        try
        {
            var autostartDir = Path.Combine(dir, "autostart");
            var exe = "/opt/DeskPilot/deskpilot";
            var autostart = new LinuxAutostart(autostartDir, () => exe);
            Assert.Equal(Path.Combine(autostartDir, "deskpilot.desktop"), autostart.FilePath);
            Assert.False(autostart.IsEnabled());
            Assert.False(autostart.Sync(false));
            Assert.False(Directory.Exists(autostartDir));

            Assert.True(autostart.Sync(true));
            Assert.True(autostart.IsEnabled());
            Assert.True(autostart.IsCurrent());
            var text = File.ReadAllText(autostart.FilePath);
            Assert.StartsWith("[Desktop Entry]\n", text);
            Assert.Contains("\nType=Application\n", text);
            Assert.Contains("\nName=DeskPilot\n", text);
            Assert.Contains("\nExec=\"/opt/DeskPilot/deskpilot\" --minimized\n", text);
            Assert.Contains("\nTerminal=false\n", text);
            Assert.DoesNotContain("\r", text);
            Assert.Equal("\"/opt/DeskPilot/deskpilot\" --minimized", autostart.GetRegisteredExec());
            Assert.False(autostart.Sync(true)); // already current

            // The program moved: Sync rewrites the entry.
            var moved = new LinuxAutostart(autostartDir, () => "/home/me/Apps/DeskPilot/deskpilot");
            Assert.True(moved.IsEnabled());
            Assert.False(moved.IsCurrent());
            Assert.True(moved.Sync(true));
            Assert.Equal("\"/home/me/Apps/DeskPilot/deskpilot\" --minimized", moved.GetRegisteredExec());
            Assert.Single(Directory.GetFiles(autostartDir)); // no temp file left behind

            Assert.True(autostart.Sync(false));
            Assert.False(autostart.IsEnabled());
            autostart.Disable(); // removing twice is fine
            Assert.Null(autostart.GetRegisteredExec());
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Theory]
    [InlineData("/opt/Desk Pilot/deskpilot", "\"/opt/Desk Pilot/deskpilot\"")]
    [InlineData("/opt/a$b/x", "\"/opt/a\\\\$b/x\"")]
    [InlineData("/opt/a\"b/x", "\"/opt/a\\\\\"b/x\"")]
    [InlineData("/opt/a`b/x", "\"/opt/a\\\\`b/x\"")]
    [InlineData("/opt/a\\b/x", "\"/opt/a\\\\\\\\b/x\"")]
    [InlineData("/opt/100%/x", "\"/opt/100%%/x\"")]
    public void Autostart_quotes_the_exec_path_as_the_desktop_entry_spec_says(string path, string expected) =>
        Assert.Equal(expected, LinuxAutostart.QuoteExecArgument(path));

    [Fact]
    public void Autostart_refuses_the_dotnet_host_and_missing_paths()
    {
        var dir = NewTempDir();
        try
        {
            var viaDotnet = new LinuxAutostart(dir, () => "/usr/share/dotnet/dotnet");
            Assert.Null(viaDotnet.ExpectedExec);
            Assert.Throws<InvalidOperationException>(() => viaDotnet.Enable());
            Assert.Throws<InvalidOperationException>(() => viaDotnet.Sync(true));
            Assert.False(viaDotnet.Sync(false));
            Assert.Null(new LinuxAutostart(dir, () => null).ExpectedExec);
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public void Autostart_folder_follows_xdg_config_home()
    {
        string? Env(string name, string? value) => name == "XDG_CONFIG_HOME" ? value : null;
        var fallback = Path.Combine(Path.Combine("/home/me", ".config"), "autostart");
        Assert.Equal(fallback, LinuxAutostart.DefaultDirectory(n => Env(n, null), "/home/me"));
        Assert.Equal(fallback, LinuxAutostart.DefaultDirectory(n => Env(n, "  "), "/home/me"));
        Assert.Equal(Path.Combine("/srv/cfg", "autostart"), LinuxAutostart.DefaultDirectory(n => Env(n, "/srv/cfg"), "/home/me"));
        // A relative XDG_CONFIG_HOME is invalid by the spec and ignored.
        Assert.Equal(fallback, LinuxAutostart.DefaultDirectory(n => Env(n, "cfg"), "/home/me"));
    }

    [LinuxFact]
    public void Autostart_default_folder_is_the_users_xdg_autostart_folder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var expected = !string.IsNullOrWhiteSpace(xdg) && xdg.StartsWith('/') ? Path.Combine(xdg, "autostart") : Path.Combine(home, ".config", "autostart");
        Assert.Equal(expected, LinuxAutostart.Default.Directory);
        Assert.Equal(Path.Combine(expected, "deskpilot.desktop"), LinuxAutostart.Default.FilePath);
    }

    [LinuxFact]
    public void Autostart_entry_is_a_valid_desktop_file_on_this_system()
    {
        // desktop-file-validate (desktop-file-utils) checks the entry the way desktops read it, when installed.
        var dir = NewTempDir();
        try
        {
            var autostart = new LinuxAutostart(dir, () => "/opt/Desk Pilot/deskpilot");
            autostart.Enable();
            var validator = new[] { "/usr/bin/desktop-file-validate", "/bin/desktop-file-validate" }.FirstOrDefault(File.Exists);
            if (validator == null) return;
            var (exit, output) = CiEnvironmentTests.Run(validator, autostart.FilePath);
            Assert.True(exit == 0, output);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    // ------------------------------------------------------------------ converters

    [Fact]
    public void Converters_map_values()
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var radio = SettingsValueToBoolConverter.Instance;
        Assert.Equal(true, radio.Convert(ThinkingMode.On, typeof(bool?), "On", culture));
        Assert.Equal(false, radio.Convert(ThinkingMode.Off, typeof(bool?), "On", culture));
        Assert.Equal(ThinkingMode.Off, radio.ConvertBack(true, typeof(ThinkingMode), "Off", culture));
        Assert.Equal(Avalonia.Data.BindingOperations.DoNothing, radio.ConvertBack(false, typeof(ThinkingMode), "Off", culture));
        Assert.Equal("png", radio.ConvertBack(true, typeof(string), "png", culture));
        Assert.Equal(ConfirmMode.RiskyOnly, radio.ConvertBack(true, typeof(ConfirmMode?), "riskyonly", culture));

        var eq = SettingsEqualsConverter.Instance;
        Assert.Equal(true, eq.Convert("vault", typeof(bool), "models|vault", culture));
        Assert.Equal(false, eq.Convert("screen", typeof(bool), "models|vault", culture));
    }

    [Fact]
    public void Error_bag_raises_changes_for_every_binding()
    {
        var bag = new SettingsErrorBag();
        var raised = new List<string?>();
        bag.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        bag.Set("A", "bad");
        Assert.Equal("bad", bag["A"]);
        Assert.Equal("", bag["B"]);
        Assert.Contains("", raised);
        Assert.True(bag.HasErrors);
        Assert.Equal(new[] { "A" }, bag.Keys);
        bag.Remove("A");
        Assert.False(bag.HasErrors);
    }

    // ------------------------------------------------------------------ windows (headless Avalonia, nothing is shown)

    [AvaloniaFact]
    public void Binding_error_check_sees_a_broken_binding()
    {
        // Proves the "no binding errors" checks below can fail: a binding to a missing property is reported.
        using var sink = new BindingErrorSink();
        var block = new TextBlock { DataContext = new SettingsSectionItem("k", "t", "d") };
        block.Bind(TextBlock.TextProperty, new Avalonia.Data.ReflectionBinding("NoSuchProperty"));
        var window = new Window { Content = block };
        window.Show();
        Pump();
        window.Close();
        Assert.NotEmpty(sink.Errors);
    }

    /// <summary>
    /// Renders every page with Skia on the headless platform. In CI (CI_LOGS set) the frames are saved next to the
    /// logs, so how the windows look on Linux can be checked by eye. Stand-in colors are used only for theme keys
    /// the app does not define yet.
    /// </summary>
    [AvaloniaFact]
    public void Windows_render_every_page()
    {
        var dir = NewTempDir();
        try
        {
            var outDir = Environment.GetEnvironmentVariable("CI_LOGS") is { Length: > 0 } logs ? Path.Combine(logs, "settings-ui") : null;
            if (outDir != null) Directory.CreateDirectory(outDir);
            var app = Application.Current!;
            foreach (var (key, color) in new[]
                     {
                         ("BgBrush", "#0E1016"), ("PanelBrush", "#14171F"), ("Panel2Brush", "#1C202A"), ("CardBrush", "#181B24"), ("BorderBrush", "#2A2F3B"),
                         ("TextBrush", "#E8EAF0"), ("SubtleTextBrush", "#A7ADBC"), ("MutedTextBrush", "#6E7588"), ("AccentBrush", "#6E7BFF"),
                         ("AccentTextBrush", "#FFFFFF"), ("DangerBrush", "#EF5B60"), ("WarningBrush", "#F2B14C"), ("SuccessBrush", "#3CCF89"),
                     })
                if (!app.TryGetResource(key, null, out _)) app.Resources[key] = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(color));

            void Save(TopLevel top, string name)
            {
                var frame = top.CaptureRenderedFrame();
                Assert.NotNull(frame);
                Assert.True(frame!.PixelSize.Width > 100 && frame.PixelSize.Height > 100, name);
                if (outDir != null) frame.Save(Path.Combine(outDir, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }

            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            var catalog = new FakeCatalog { Result = new ModelListResult(new[] { new ModelInfo("haiku", "Haiku", true, true, true), new ModelInfo("sonnet", "Sonnet", true, true, true) }, null) };
            var report = MakeReport(vaults: new[] { "/home/me/Notes" }, claude: new CliToolStatus("claude", "/usr/bin/claude", "2.1.293", true, "claude.ai, max"),
                ollama: new LocalServerStatus("Ollama", "http://127.0.0.1:11434", true, new[] { new ModelInfo("gemma3:12b", "g", true, true, null) }));
            var window = NewSettingsWindow(store, new FakeSession(), catalog, new FakeDetector { Report = report }, dir, WaylandSession);
            window.Width = 1000;
            window.Height = 1300;
            window.Show();
            var vm = window.ViewModel;
            PumpUntil(() => !vm.IsDetecting && !vm.Profiles[0].IsLoadingModels);
            vm.AdvancedMode = true;
            vm.AddPreset(ProviderPresets.OpenAiId);
            vm.SelectedProfile = vm.Profiles[0];
            foreach (var section in vm.VisibleSections.ToList())
            {
                vm.SelectedSection = section;
                Pump();
                Save(window, "settings-" + section.Key);
            }
            vm.SelectedProfile = vm.Profiles[1];
            vm.SelectSection(SettingsViewModel.SectionModels);
            Pump();
            Save(window, "settings-models-http");
            window.BuildAddMenu().ShowAt(window.FindControl<Button>("AddProfileButton")!);
            Pump();
            Save(window, "settings-add-menu");
            window.Close();

            var welcome = new WelcomeWindow(store, report with { ClaudeCli = new CliToolStatus("claude", "/usr/bin/claude", "2.1.293", false, null) },
                new[] { "Screenshots need grim: sudo apt install grim", "Typing needs wtype: sudo apt install wtype" }, WaylandSession) { Height = 1250 };
            welcome.Show();
            Pump();
            Save(welcome, "welcome-wayland");
            welcome.Close();

            var preview = new PromptPreviewWindow(vm.BuildPreviewPrompt(new DateTime(2026, 10, 8, 9, 30, 0)));
            preview.Show();
            Pump();
            Save(preview, "prompt-preview");
            preview.Close();
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [AvaloniaFact]
    public void Windows_parse_from_xaml_with_their_designer_constructors()
    {
        var settings = new SettingsWindow();
        var welcome = new WelcomeWindow();
        var preview = new PromptPreviewWindow();
        Assert.Null(settings.DataContext);
        Assert.Null(welcome.ViewModel);
        Assert.Equal("", preview.Prompt);
        Assert.Equal("DeskPilot settings", settings.Title);
        Assert.Equal("Welcome to DeskPilot", welcome.Title);
    }

    [AvaloniaFact]
    public void Settings_window_binds_every_page_and_profile_kind()
    {
        var dir = NewTempDir();
        using var bindingErrors = new BindingErrorSink();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            var session = new FakeSession();
            var catalog = new FakeCatalog { Result = new ModelListResult(new[] { new ModelInfo("haiku", "Haiku", true, true, true) }, null) };
            var detector = new FakeDetector { Report = MakeReport(vaults: new[] { Path.Combine(dir, "vault") }) };
            var window = NewSettingsWindow(store, session, catalog, detector, dir, WaylandSession);
            var vm = window.ViewModel;
            Assert.Same(vm, window.DataContext);
            Assert.Equal(store.FilePath, vm.SettingsFilePath);
            Assert.NotSame(store.Current, vm.Settings);
            Assert.Equal(0, detector.Calls); // detection starts when the window opens

            window.Show();
            Pump();
            Assert.Equal(1, detector.Calls);
            Assert.Equal(1, catalog.Calls);
            Assert.True(vm.HasDetectedVaults);
            Assert.Equal("Wayland session (sway)", vm.SessionText);

            vm.AdvancedMode = true;
            vm.AllowShellCommands = true;
            vm.MonitorSelection = MonitorSelection.Specific;
            foreach (var preset in new[] { ProviderPresets.OpenAiId, ProviderPresets.AnthropicApiId, ProviderPresets.OllamaId, ProviderPresets.GeminiCliId, ProviderPresets.CustomAcpId })
                vm.AddPreset(preset);
            vm.Profiles[1].NewApiKey = "sk-typed-1234567890";
            vm.TryCommit(); // fills nothing in, but exercises the error lines
            foreach (var section in vm.VisibleSections.ToList())
            {
                vm.SelectedSection = section;
                foreach (var profile in vm.Profiles.ToList())
                {
                    vm.SelectedProfile = profile;
                    Pump();
                }
                // The selected page is the only visible one.
                var pages = All<StackPanel>(window)
                    .Where(p => p.Parent is Panel { Parent: ScrollViewer }).ToList();
                Assert.Equal(7, pages.Count);
                Assert.Single(pages, p => p.IsVisible);
            }

            // Error lines show the bag's messages.
            vm.SelectSection(SettingsViewModel.SectionSafety);
            vm.MaxStepsText = "0";
            Assert.False(vm.TryCommit());
            Pump();
            var errorLine = All<TextBlock>(window).Single(t => t.Classes.Contains("error") && t.Text == vm.Errors[nameof(SettingsViewModel.MaxStepsText)]);
            Assert.True(errorLine.IsVisible);
            vm.MaxStepsText = "80";
            Assert.True(vm.TryCommit());
            Pump();
            Assert.False(errorLine.IsVisible);

            // The Add menu offers every preset, grouped.
            var menu = window.BuildAddMenu();
            var presetItems = menu.Items.OfType<MenuItem>().Where(m => m.CommandParameter is string).ToList();
            Assert.Equal(ProviderPresets.All.Count, presetItems.Count);
            Assert.All(presetItems, m => Assert.False(string.IsNullOrEmpty(ToolTip.GetTip(m) as string)));
            Assert.Equal(new[] { "Cloud", "Local", "Agents" }, menu.Items.OfType<MenuItem>().Where(m => !m.IsEnabled).Select(m => (string)m.Header!));
            presetItems.First(m => (string?)m.CommandParameter == ProviderPresets.XaiId).Command!.Execute(ProviderPresets.XaiId);
            Assert.Equal(ProviderPresets.XaiId, vm.SelectedProfile!.Profile.PresetId);

            window.Close();
            Pump();
            Assert.False(File.Exists(store.FilePath)); // nothing was saved
            Assert.Equal(0, session.ReloadCount);
            Assert.True(bindingErrors.Errors.Count == 0, "Binding errors:\n" + string.Join("\n", bindingErrors.Errors));
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [AvaloniaFact]
    public void Stored_api_key_never_reaches_the_window()
    {
        var dir = NewTempDir();
        try
        {
            const string secret = "sk-stored-secret-key-0123456789";
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            store.Update(s =>
            {
                var p = ProviderPresets.CreateProfile(ProviderPresets.OpenAiId);
                p.ApiKeyProtected = SecretProtector.Protect(secret);
                s.Profiles.Add(p);
            });
            var window = NewSettingsWindow(store, new FakeSession(), new FakeCatalog(), new FakeDetector { Report = MakeReport() }, dir, X11Session);
            window.Show();
            var vm = window.ViewModel;
            vm.SelectedProfile = vm.Profiles[1];
            Pump();

            var keyBox = FindByAutomationName<TextBox>(window, "API key");
            Assert.Equal('•', keyBox.PasswordChar);
            Assert.False(keyBox.IsEffectivelyVisible); // a saved key shows only its mask until Replace
            var texts = All<TextBlock>(window).Select(t => t.Text ?? "")
                .Concat(All<TextBox>(window).Select(t => t.Text ?? "")).ToList();
            Assert.DoesNotContain(texts, t => t.Contains(secret));
            Assert.Contains(texts, t => t == "Saved key: " + SecretProtector.Mask(secret));

            vm.SelectedProfile!.ReplaceKey();
            Pump();
            Assert.True(keyBox.IsEffectivelyVisible);
            Assert.Equal("", keyBox.Text ?? "");
            keyBox.Text = "sk-new-typed-key-123456";
            Assert.Equal("sk-new-typed-key-123456", vm.SelectedProfile.NewApiKey);
            window.Close();
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [AvaloniaFact]
    public void Model_picker_keeps_the_model_when_the_list_or_profile_changes()
    {
        var dir = NewTempDir();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            var window = NewSettingsWindow(store, new FakeSession(), new FakeCatalog(), new FakeDetector { Report = MakeReport() }, dir, X11Session);
            window.Show();
            var vm = window.ViewModel;
            var claude = vm.Profiles[0];
            var openai = vm.AddPreset(ProviderPresets.OpenAiId);
            var modelBox = FindByAutomationName<TextBox>(window, "Model");
            var pickButton = window.FindControl<Button>("ModelPickButton")!;
            var list = window.FindControl<ListBox>("ModelList")!;

            vm.SelectedProfile = claude;
            Pump();
            Assert.Equal("haiku", modelBox.Text);

            // Alt+Down in the model box opens the list, like a combo box.
            Assert.True(modelBox.Focus());
            window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.ArrowDown, null);
            Pump();
            Assert.True(pickButton.Flyout!.IsOpen);
            Assert.Same(claude, list.DataContext);
            Assert.Null(list.SelectedItem);

            // The user clicks "sonnet" in the open list.
            var popup = TopLevel.GetTopLevel(list)!;
            var index = claude.ModelOptions.ToList().FindIndex(o => o.Id == "sonnet");
            list.ScrollIntoView(index);
            Pump();
            var item = list.ContainerFromIndex(index)!;
            var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), popup)!.Value;
            popup.MouseDown(point, Avalonia.Input.MouseButton.Left);
            popup.MouseUp(point, Avalonia.Input.MouseButton.Left);
            Pump();
            Assert.Equal("sonnet", claude.Model);
            Assert.Equal("sonnet", modelBox.Text);
            Assert.Null(list.SelectedItem);
            Assert.False(pickButton.Flyout.IsOpen);

            // Keyboard: arrows only move the highlight, Enter picks.
            pickButton.Flyout.ShowAt(pickButton);
            Pump();
            list.SelectedIndex = 0;
            Assert.Equal("sonnet", claude.Model);
            list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Pump();
            Assert.Equal(claude.ModelOptions[0].Id, claude.Model);
            Assert.False(pickButton.Flyout.IsOpen);
            claude.Model = "sonnet";

            // A refresh replaces the list the picked item came from.
            claude.SetModels(new[] { new ModelInfo("opus", "Opus", true, true, true), new ModelInfo("haiku", "Haiku", true, true, true) });
            Pump();
            Assert.Equal("sonnet", claude.Model);

            // Typing still works, and switching profiles never carries one profile's model into the other.
            modelBox.Text = "claude-sonnet-5-5";
            Assert.Equal("claude-sonnet-5-5", claude.Model);
            vm.SelectedProfile = openai;
            Pump();
            Assert.Equal("gpt-5-mini", openai.Model);
            Assert.Equal("gpt-5-mini", modelBox.Text);
            Assert.Equal("claude-sonnet-5-5", claude.Model);
            vm.SelectedProfile = claude;
            Pump();
            Assert.Equal("claude-sonnet-5-5", modelBox.Text);
            Assert.Equal("gpt-5-mini", openai.Model);
            window.Close();
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [AvaloniaFact]
    public void Switching_profiles_and_redetecting_keep_every_choice()
    {
        var dir = NewTempDir();
        using var bindingErrors = new BindingErrorSink();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            store.Update(s =>
            {
                var hand = ProviderPresets.CreateProfile(ProviderPresets.AnthropicApiId);
                hand.Effort = "turbo-custom"; // an effort value the picker does not know, from a hand-edited file
                s.Profiles.Add(hand);
            });
            var window = NewSettingsWindow(store, new FakeSession(), new FakeCatalog(), new FakeDetector { Report = MakeReport() }, dir, X11Session);
            window.Show();
            var vm = window.ViewModel;
            var claude = vm.Profiles[0];
            claude.Effort = "high";
            claude.Thinking = ThinkingMode.On;
            var custom = vm.Profiles[1];
            var router = vm.AddPreset(ProviderPresets.OpenRouterId);
            router.Effort = "low";
            router.Thinking = ThinkingMode.Off;
            router.ReasoningStyle = ReasoningStyle.ReasoningEffort;
            vm.AdvancedMode = true;
            vm.MonitorSelection = MonitorSelection.Specific;
            vm.MonitorIndex = 1;
            vm.SelectedOverlayPosition = SettingsViewModel.OverlayPositions[0];

            foreach (var section in new[] { SettingsViewModel.SectionModels, SettingsViewModel.SectionAdvanced, SettingsViewModel.SectionScreen, SettingsViewModel.SectionInterface })
            {
                vm.SelectSection(section);
                foreach (var p in new[] { claude, router, custom, claude, custom, router })
                {
                    vm.SelectedProfile = p;
                    Pump();
                    if (section == SettingsViewModel.SectionModels)
                    {
                        Assert.Equal(p.Effort, (FindByAutomationName<ComboBox>(window, "Effort").SelectedItem as SettingsOption<string>)?.Value);
                        var thinkingOn = FindByAutomationName<RadioButton>(window, "Thinking on");
                        Assert.Equal(p.Thinking == ThinkingMode.On, thinkingOn.IsChecked);
                    }
                    if (section == SettingsViewModel.SectionAdvanced && p == router)
                        Assert.Equal(ReasoningStyle.ReasoningEffort, (FindByAutomationName<ComboBox>(window, "Reasoning style").SelectedItem as SettingsOption<ReasoningStyle>)?.Value);
                }
            }
            vm.SelectSection(SettingsViewModel.SectionScreen);
            Pump();
            var monitorBox = FindByAutomationName<ComboBox>(window, "Monitor");
            Assert.Equal(1, (monitorBox.SelectedItem as SettingsMonitorOption)?.Index);
            vm.ApplyReport(MakeReport()); // rebuilds the monitor list while the page is shown
            Pump();
            vm.ApplyReport(MakeReport());
            Pump();
            Assert.Equal(1, (monitorBox.SelectedItem as SettingsMonitorOption)?.Index);

            Assert.Equal("high", claude.Effort);
            Assert.Equal(ThinkingMode.On, claude.Thinking);
            Assert.Equal("low", router.Effort);
            Assert.Equal(ThinkingMode.Off, router.Thinking);
            Assert.Equal(ReasoningStyle.ReasoningEffort, router.ReasoningStyle);
            Assert.Equal("turbo-custom", custom.Effort);
            Assert.Equal(1, vm.MonitorIndex);
            Assert.Equal(OverlayCorner.TopLeft, vm.OverlayPosition);
            Assert.True(bindingErrors.Errors.Count == 0, "Binding errors:\n" + string.Join("\n", bindingErrors.Errors));
            window.Close();
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [AvaloniaFact]
    public void Radio_buttons_and_check_boxes_write_the_settings()
    {
        var dir = NewTempDir();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            var window = NewSettingsWindow(store, new FakeSession(), new FakeCatalog(), new FakeDetector { Report = MakeReport() }, dir, X11Session);
            window.Show();
            var vm = window.ViewModel;
            Pump();
            FindByAutomationName<RadioButton>(window, "Thinking off").IsChecked = true;
            Pump();
            Assert.Equal(ThinkingMode.Off, vm.SelectedProfile!.Thinking);
            Assert.False(FindByAutomationName<RadioButton>(window, "Thinking auto").IsChecked);

            vm.SelectSection(SettingsViewModel.SectionInterface);
            Pump();
            FindByAutomationName<CheckBox>(window, "Start when I log in").IsChecked = true;
            Assert.True(vm.StartAtLogin);
            window.Close();
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [AvaloniaFact]
    public void Hotkey_box_captures_real_key_presses()
    {
        var dir = NewTempDir();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            var window = NewSettingsWindow(store, new FakeSession(), new FakeCatalog(), new FakeDetector { Report = MakeReport() }, dir, WaylandSession);
            window.Show();
            var vm = window.ViewModel;
            vm.SelectSection(SettingsViewModel.SectionInterface);
            Pump();
            var box = FindByAutomationName<TextBox>(window, "Stop hotkey");
            Assert.True(box.Focus());
            Pump();

            window.KeyPress(Key.Q, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Q, "q");
            window.KeyRelease(Key.Q, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Q, "q");
            Pump();
            Assert.Equal("Ctrl+Shift+Q", vm.StopHotkey);
            Assert.Equal("Ctrl+Shift+Q", box.Text);

            window.KeyPress(Key.A, RawInputModifiers.None, PhysicalKey.A, "a");
            Pump();
            Assert.Equal("Ctrl+Shift+Q", vm.StopHotkey); // plain typing is refused
            Assert.Contains("Ctrl, Alt or Super", vm.HotkeyHint);

            window.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
            Pump();
            Assert.Equal(SettingsHotkey.DefaultHotkey, vm.StopHotkey);

            // The Wayland note is on the page.
            Assert.Contains(All<TextBlock>(window), t => t.IsEffectivelyVisible && (t.Text ?? "").Contains("Wayland does not let apps register global hotkeys"));
            window.Close();
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [AvaloniaFact]
    public void Placeholder_buttons_insert_at_the_caret()
    {
        var dir = NewTempDir();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            var window = NewSettingsWindow(store, new FakeSession(), new FakeCatalog(), new FakeDetector { Report = MakeReport() }, dir, X11Session);
            window.Show();
            var vm = window.ViewModel;
            vm.AdvancedMode = true;
            vm.SelectSection(SettingsViewModel.SectionAdvanced);
            vm.CustomSystemPrompt = "Date: . End";
            Pump();
            var editor = FindByAutomationName<TextBox>(window, "System prompt");
            editor.CaretIndex = 6;
            var button = FindByAutomationName<Button>(window, "{{DATE}}");
            Assert.Equal("{{DATE}}", ((TextBlock)button.Content!).Text); // no access-key mangling of the underscores elsewhere
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Assert.Equal("Date: {{DATE}}. End", vm.CustomSystemPrompt);
            Assert.Equal(14, editor.CaretIndex);
            Assert.Contains(All<Button>(window), b => (b.Tag as string) == "{{MAX_STEPS}}");
            Assert.Contains(All<Button>(window), b => (b.Tag as string) == "{{OS_TIPS}}");
            window.Close();
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [AvaloniaFact]
    public async Task Save_writes_the_store_reloads_the_session_sets_autostart_and_closes_with_true()
    {
        var dir = NewTempDir();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            var session = new FakeSession();
            var autostart = new LinuxAutostart(Path.Combine(dir, "autostart"), () => "/opt/DeskPilot/deskpilot");
            var owner = new Window();
            owner.Show();
            var window = new SettingsWindow(store, session, new FakeCatalog(), new FakeDetector { Report = MakeReport() }, autostart, X11Session);
            var result = window.ShowDialog<bool>(owner);
            Pump();
            var vm = window.ViewModel;
            vm.MaxStepsText = "33";
            vm.StartAtLogin = true;
            vm.UserInstructions = "Prefer Firefox.";

            Assert.True(await window.SaveAsync(closeOnSuccess: true));
            Pump();
            Assert.True(result.IsCompleted);
            Assert.True(await result);
            Assert.Equal(1, session.ReloadCount);
            Assert.Equal(33, store.Current.Safety.MaxStepsPerTurn);
            Assert.Equal("Prefer Firefox.", new SettingsStore(store.FilePath).Current.Prompt.UserInstructions);
            Assert.True(autostart.IsCurrent());
            Assert.NotSame(vm.Settings, store.Current); // later edits can never leak into the live settings

            // Turning it off again removes the entry.
            var again = new SettingsWindow(store, session, new FakeCatalog(), new FakeDetector { Report = MakeReport() }, autostart, X11Session);
            var second = again.ShowDialog<bool>(owner);
            Pump();
            again.ViewModel.StartAtLogin = false;
            Assert.True(await again.SaveAsync(closeOnSuccess: true));
            Assert.True(await second);
            Assert.False(autostart.IsEnabled());
            owner.Close();
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [AvaloniaFact]
    public async Task Save_with_invalid_fields_stays_open_and_points_at_the_problem()
    {
        var dir = NewTempDir();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            var session = new FakeSession();
            var window = NewSettingsWindow(store, session, new FakeCatalog(), new FakeDetector { Report = MakeReport() }, dir, X11Session);
            window.Show();
            var vm = window.ViewModel;
            vm.GridSpacingText = "7";
            Assert.False(await window.SaveAsync(closeOnSuccess: true));
            Pump();
            Assert.True(window.IsVisible);
            Assert.Equal(SettingsViewModel.SectionScreen, vm.SelectedSectionKey);
            Assert.StartsWith("Screen: grid spacing", vm.ErrorSummary);
            Assert.False(File.Exists(store.FilePath));
            Assert.Equal(0, session.ReloadCount);
            window.Close();
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [AvaloniaFact]
    public async Task Save_that_cannot_set_autostart_shows_why_and_still_reports_saved()
    {
        var dir = NewTempDir();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            var session = new FakeSession { ReloadError = new InvalidOperationException("backend busy") };
            var autostart = new LinuxAutostart(Path.Combine(dir, "autostart"), () => "/usr/lib/dotnet/dotnet");
            var owner = new Window();
            owner.Show();
            var window = new SettingsWindow(store, session, new FakeCatalog(), new FakeDetector { Report = MakeReport() }, autostart, X11Session);
            var result = window.ShowDialog<bool>(owner);
            Pump();
            window.ViewModel.StartAtLogin = true;

            Assert.True(await window.SaveAsync(closeOnSuccess: true));
            Pump();
            Assert.False(result.IsCompleted); // stays open so the warning can be read
            Assert.Contains("Start when I log in", window.SaveNoticeText);
            Assert.Contains("backend busy", window.SaveNoticeText);
            Assert.True(new SettingsStore(store.FilePath).Current.Ui.StartWithWindows);

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Pump();
            Assert.True(await result);
            owner.Close();
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [AvaloniaFact]
    public async Task Escape_cancels_without_saving()
    {
        var dir = NewTempDir();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            var owner = new Window();
            owner.Show();
            var window = NewSettingsWindow(store, new FakeSession(), new FakeCatalog(), new FakeDetector { Report = MakeReport() }, dir, X11Session);
            var result = window.ShowDialog<bool>(owner);
            Pump();
            window.ViewModel.MaxStepsText = "12";
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Pump();
            Assert.False(await result);
            Assert.False(File.Exists(store.FilePath));
            owner.Close();
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [AvaloniaFact]
    public void Open_folder_buttons_use_xdg_open()
    {
        var dir = NewTempDir();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "cfg", "settings.json"));
            var window = NewSettingsWindow(store, new FakeSession(), new FakeCatalog(), new FakeDetector { Report = MakeReport() }, dir, X11Session);
            var started = new List<(string File, string[] Args)>();
            window.StartProcess = (file, args) => { started.Add((file, args.ToArray())); return true; };
            window.Show();
            window.ViewModel.SelectSection(SettingsViewModel.SectionInterface);
            Pump();

            var buttons = All<Button>(window).Where(b => (b.Content as string) == "Open folder").ToList();
            Assert.Equal(2, buttons.Count);
            buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(started);
            Assert.Equal("xdg-open", started[0].File);
            Assert.Equal(new[] { Path.Combine(dir, "cfg") }, started[0].Args);
            Assert.True(Directory.Exists(Path.Combine(dir, "cfg")));

            window.StartProcess = (_, _) => throw new System.ComponentModel.Win32Exception(2, "No such file or directory");
            buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("xdg-open is not available", window.ViewModel.ErrorSummary);
            window.Close();
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [AvaloniaFact]
    public void Welcome_window_shows_session_and_setup_notes_and_saves_on_get_started()
    {
        var dir = NewTempDir();
        using var bindingErrors = new BindingErrorSink();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            var vault = Path.Combine(dir, "vault");
            var notes = new[] { "Screenshots need grim: sudo apt install grim" };
            var window = new WelcomeWindow(store, MakeReport(vaults: new[] { vault }, claude: new CliToolStatus("claude", "/usr/bin/claude", "2.1.293", false, null)), notes, WaylandSession);
            var vm = window.ViewModel!;
            Assert.Same(vm, window.DataContext);
            Assert.Equal(2, vm.VaultOptions.Count);
            window.Show();
            Pump();

            var texts = All<TextBlock>(window).Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
            Assert.Contains("Wayland session (sway)", texts);
            Assert.Contains("Install these first", texts);
            Assert.Contains(texts, t => t.Contains("no stop hotkey"));
            Assert.Contains(All<SelectableTextBlock>(window), t => t.Text == notes[0] && t.IsEffectivelyVisible);
            Assert.Contains(All<TextBox>(window), t => t.Text == "claude auth login" && t.IsEffectivelyVisible);
            var setupCard = All<Border>(window).Single(b => b.Classes.Contains("welcome-card") && b.Classes.Contains("attention"));
            Assert.NotNull(setupCard);

            vm.SelectedVault = vm.VaultOptions[1];
            Assert.True(window.GetStarted());
            var saved = new SettingsStore(store.FilePath).Current;
            Assert.True(saved.FirstRunCompleted);
            Assert.Equal(vault, saved.Vault.Path);
            Assert.True(bindingErrors.Errors.Count == 0, "Binding errors:\n" + string.Join("\n", bindingErrors.Errors));
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [AvaloniaFact]
    public async Task Welcome_window_closes_with_true_through_ShowDialog()
    {
        var dir = NewTempDir();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            var owner = new Window();
            owner.Show();
            var window = new WelcomeWindow(store, MakeReport(), Array.Empty<string>(), X11Session);
            var result = window.ShowDialog<bool>(owner);
            Pump();
            Assert.Contains(All<TextBlock>(window), t => t.IsEffectivelyVisible && t.Text == "Everything DeskPilot needs for this session is installed.");
            var getStarted = All<Button>(window).Single(b => (b.Content as string) == "Get started");
            getStarted.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Assert.True(await result);
            Assert.True(store.Current.FirstRunCompleted);
            owner.Close();
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [AvaloniaFact]
    public void Prompt_preview_window_shows_the_prompt_and_its_size()
    {
        var preview = new PromptPreviewWindow("Hello prompt");
        preview.Show();
        Pump();
        Assert.Equal("Hello prompt", preview.Prompt);
        Assert.StartsWith("12 characters, about 3 tokens", PromptPreviewWindow.Describe("Hello prompt"));
        Assert.Contains(All<TextBox>(preview), t => t.Text == "Hello prompt" && t.IsReadOnly);
        preview.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump();
        Assert.False(preview.IsVisible);
    }

    // ------------------------------------------------------------------ helpers

    private static SettingsWindow NewSettingsWindow(SettingsStore store, IAgentSession session, IModelCatalog catalog, IEnvironmentDetector detector, string tempDir, LinuxSessionInfo linuxSession) =>
        new(store, session, catalog, detector, new LinuxAutostart(Path.Combine(tempDir, "autostart"), () => "/opt/DeskPilot/deskpilot"), linuxSession);

    /// <summary>Runs queued dispatcher work, layout and a render tick, the way a shown window would between frames.</summary>
    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Pumps until background work (detection, model lists) has posted its results back.</summary>
    private static void PumpUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            Pump();
            if (condition()) return;
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("condition not met");
            Thread.Sleep(5);
        }
    }

    /// <summary>Logical and visual descendants (item templates and inner parts are not always both).</summary>
    private static IReadOnlyList<T> All<T>(Control root) where T : Control
    {
        root.UpdateLayout();
        return root.GetLogicalDescendants().OfType<T>().Concat(root.GetVisualDescendants().OfType<T>()).Distinct().ToList();
    }

    private static T FindByAutomationName<T>(Control root, string name) where T : Control
    {
        var match = All<T>(root).FirstOrDefault(c => AutomationProperties.GetName(c) == name);
        return match ?? throw new InvalidOperationException($"No {typeof(T).Name} named '{name}'");
    }

    private static EnvironmentReport MakeReport(
        CliToolStatus? claude = null,
        CliToolStatus? gemini = null,
        LocalServerStatus? ollama = null,
        LocalServerStatus? lmStudio = null,
        string[]? envVars = null,
        string[]? vaults = null,
        bool elevated = false) =>
        new(
            claude ?? new CliToolStatus("claude", null, null, null, null),
            gemini ?? new CliToolStatus("gemini", null, null, null, null),
            new CliToolStatus("codex", null, null, null, null),
            ollama ?? new LocalServerStatus("Ollama", "http://127.0.0.1:11434", false, Array.Empty<ModelInfo>()),
            lmStudio ?? new LocalServerStatus("LM Studio", "http://127.0.0.1:1234", false, Array.Empty<ModelInfo>()),
            envVars ?? Array.Empty<string>(),
            vaults ?? Array.Empty<string>(),
            new[]
            {
                new MonitorInfo(0, "eDP-1", new ScreenRect(0, 0, 2560, 1440), new ScreenRect(0, 0, 2560, 1400), true, 1.5),
                new MonitorInfo(1, "HDMI-1", new ScreenRect(2560, 0, 1440, 1080), new ScreenRect(2560, 0, 1440, 1040), false, 1.0),
            },
            elevated);

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskpilot-linux-uisettings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDir(string dir)
    {
        try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

    /// <summary>Collects Avalonia binding warnings and errors while a test runs.</summary>
    private sealed class BindingErrorSink : ILogSink, IDisposable
    {
        private readonly ILogSink? _previous;

        public BindingErrorSink()
        {
            _previous = Logger.Sink;
            Logger.Sink = this;
        }

        public List<string> Errors { get; } = new();

        public bool IsEnabled(LogEventLevel level, string area) =>
            (level >= LogEventLevel.Warning && area == LogArea.Binding) || (_previous?.IsEnabled(level, area) ?? false);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            Log(level, area, source, messageTemplate, Array.Empty<object?>());

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (level >= LogEventLevel.Warning && area == LogArea.Binding)
                lock (Errors) Errors.Add($"{level} {source?.GetType().Name}: {messageTemplate} | {string.Join(" | ", propertyValues)}");
            _previous?.Log(level, area, source, messageTemplate, propertyValues);
        }

        public void Dispose() => Logger.Sink = _previous;
    }

    private sealed class FakeCatalog : IModelCatalog
    {
        public ModelListResult Result { get; set; } = new(Array.Empty<ModelInfo>(), null);
        public Exception? Throw { get; set; }
        public int Calls;
        public ProviderProfile? LastProfile { get; private set; }
        public string? LastKey { get; private set; }

        public Task<ModelListResult> ListModelsAsync(ProviderProfile profile, string apiKey, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            LastProfile = profile;
            LastKey = apiKey;
            if (Throw != null) throw Throw;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeDetector : IEnvironmentDetector
    {
        public EnvironmentReport? Report { get; set; }
        public Exception? Throw { get; set; }
        public int Calls;

        public Task<EnvironmentReport> DetectAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Throw != null) throw Throw;
            return Task.FromResult(Report!);
        }
    }

    private sealed class FakeToolHost : IToolHost
    {
        public ToolResult Result { get; set; } = ToolResult.Ok("");
        public Exception? Throw { get; set; }
        public string? LastName { get; private set; }
        public JsonElement LastArgs { get; private set; }

        public IReadOnlyList<ToolSpec> GetTools() => Array.Empty<ToolSpec>();

        public Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken ct)
        {
            LastName = name;
            LastArgs = arguments.Clone();
            if (Throw != null) throw Throw;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeSession : IAgentSession
    {
        public int ReloadCount;
        public Exception? ReloadError { get; set; }
        public event Action<AgentEvent>? EventRaised { add { } remove { } }
        public event Action<AgentState>? StateChanged { add { } remove { } }
        public AgentState State => AgentState.Idle;
        public string ActiveDescription => "fake";
        public Task<TurnResult> SendAsync(string message, CancellationToken ct = default) =>
            Task.FromResult(new TurnResult(TurnOutcome.Completed, "", new TurnStats(0, 0, 0, null, TimeSpan.Zero, null), null));
        public Task StopAsync(string reason = "Stopped by user") => Task.CompletedTask;
        public Task NewConversationAsync() => Task.CompletedTask;
        public Task ReloadSettingsAsync()
        {
            Interlocked.Increment(ref ReloadCount);
            return ReloadError != null ? Task.FromException(ReloadError) : Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
