using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Prompts;
using DeskPilot.Core.Settings;
using DeskPilot.Services;
using DeskPilot.ViewModels;
using DeskPilot.Views;
using Microsoft.Win32;

namespace DeskPilot.Tests;

public class UiSettingsTests
{
    // ------------------------------------------------------------------ parsing

    [Theory]
    [InlineData("80", 1, 1000, true, 80)]
    [InlineData(" 1000 ", 1, 1000, true, 1000)]
    [InlineData("0", 1, 1000, false, 0)]
    [InlineData("1001", 1, 1000, false, 0)]
    [InlineData("abc", 1, 1000, false, 0)]
    [InlineData("", 1, 1000, false, 0)]
    [InlineData("1,280", 320, 3840, true, 1280)]
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
    [InlineData("file:///C:/x", false)]
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
        Assert.True(SettingsParsing.TryParseEnvLines("A=1\r\n# comment\n\n  B = two words \nC=x=y", out var env, out var error), error);
        Assert.Equal(3, env.Count);
        Assert.Equal("1", env["A"]);
        Assert.Equal("two words", env["B"]);
        Assert.Equal("x=y", env["C"]);

        Assert.False(SettingsParsing.TryParseEnvLines("A=1\nnovalue", out _, out var e1));
        Assert.Contains("Line 2", e1);
        Assert.False(SettingsParsing.TryParseEnvLines("=x", out _, out _));
        Assert.False(SettingsParsing.TryParseEnvLines("MY VAR=x", out _, out _));

        var text = SettingsParsing.FormatEnvLines(new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" });
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
        Assert.Equal(new[] { ".obsidian", "Archive" }, SettingsParsing.ParseFolderNames(".obsidian, /Archive/ , .OBSIDIAN"));

        Assert.True(SettingsParsing.TryParseExtensions("md, .TXT, *.org .md", out var ext, out var error), error);
        Assert.Equal(new[] { ".md", ".txt", ".org" }, ext);
        Assert.False(SettingsParsing.TryParseExtensions(" , ", out _, out var e1));
        Assert.Contains("at least one", e1);
        Assert.False(SettingsParsing.TryParseExtensions(".md, a/b", out _, out _));
    }

    [Fact]
    public void Regex_lines_must_compile()
    {
        Assert.True(SettingsParsing.TryParseRegexLines("\\brunas\\b\n\n-verb\\s+runas", out var patterns, out _));
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

    // ------------------------------------------------------------------ hotkey

    [Theory]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt, Key.X, "Ctrl+Alt+X")]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift, Key.D1, "Ctrl+Shift+1")]
    [InlineData(ModifierKeys.Alt | ModifierKeys.Windows, Key.F12, "Alt+Win+F12")]
    [InlineData(ModifierKeys.None, Key.Pause, "Pause")]
    [InlineData(ModifierKeys.Control, Key.PageDown, "Ctrl+PageDown")]
    [InlineData(ModifierKeys.Control, Key.NumPad5, "Ctrl+NumPad5")]
    public void Hotkey_format(ModifierKeys modifiers, Key key, string expected)
    {
        Assert.Equal(expected, SettingsHotkey.Format(modifiers, key));
        Assert.True(KeyCombo.TryParse(expected, out _, out var error), error);
    }

    [Fact]
    public void Hotkey_modifier_only_is_not_a_combination()
    {
        Assert.Null(SettingsHotkey.Format(ModifierKeys.Control, Key.LeftCtrl));
        Assert.Null(SettingsHotkey.Format(ModifierKeys.Alt, Key.RightAlt));
        Assert.Equal("Ctrl+Alt+", SettingsHotkey.FormatModifiers(ModifierKeys.Control | ModifierKeys.Alt));
    }

    [Theory]
    [InlineData("Ctrl+Alt+X", true)]
    [InlineData("ctrl+shift+f5", true)]
    [InlineData("F9", true)]
    [InlineData("Pause", true)]
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
        Assert.False(vm.CaptureHotkey(ModifierKeys.Control | ModifierKeys.Alt, Key.LeftAlt));
        Assert.Contains("Ctrl+Alt+", vm.HotkeyHint);
        Assert.Equal("Ctrl+Alt+X", vm.StopHotkey);

        Assert.True(vm.CaptureHotkey(ModifierKeys.Control | ModifierKeys.Shift, Key.Q));
        Assert.Equal("Ctrl+Shift+Q", vm.StopHotkey);
        Assert.Equal("Ctrl+Shift+Q", vm.Settings.Ui.StopHotkey);
        Assert.Equal("", vm.HotkeyHint);

        Assert.False(vm.CaptureHotkey(ModifierKeys.None, Key.Q));
        Assert.Equal("Ctrl+Shift+Q", vm.StopHotkey);
        Assert.Contains("Ctrl, Alt or Win", vm.HotkeyHint);

        Assert.True(vm.CaptureHotkey(ModifierKeys.None, Key.Back));
        Assert.Equal(SettingsHotkey.DefaultHotkey, vm.StopHotkey);
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
        vm.Effort = "low";

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

        vm.TemperatureText = "";
        Assert.True(vm.Commit());
        Assert.Null(profile.Temperature);
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

        // Fixing the fields clears the errors on the next commit.
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
        var vm = new ProfileViewModel(profile);

        Assert.True(vm.HasStoredKey);
        Assert.False(vm.ShowKeyEntry);
        Assert.Equal("Saved key: " + SecretProtector.Mask(oldKey), vm.StoredKeyText);
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
        Assert.True(vm.Commit());
        Assert.NotEqual("sk-new-key-1234567890", profile.ApiKeyProtected);
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
        profile.ApiKeyProtected = Convert.ToBase64String(Encoding.UTF8.GetBytes("not dpapi"));
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
    }

    [Fact]
    public void Profile_effort_options_keep_unknown_values()
    {
        var profile = ProviderPresets.CreateProfile(ProviderPresets.AnthropicApiId);
        profile.Effort = "turbo";
        var vm = new ProfileViewModel(profile);
        Assert.Contains(vm.EffortOptions, o => o.Value == "");
        Assert.Contains(vm.EffortOptions, o => o.Value == "high");
        Assert.Contains(vm.EffortOptions, o => o.Value == "turbo");
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

        vm.Model = "deepseek-r1:8b";
        Assert.Contains("turn off", vm.ModelHint);
        vm.SupportsVision = false;
        Assert.Equal("text only, thinking", vm.ModelHint);
    }

    // ------------------------------------------------------------------ settings view model

    [Fact]
    public void Settings_vm_loads_text_fields_and_commits_them()
    {
        using var vm = new SettingsViewModel(SettingsStore.CreateDefault());
        Assert.Equal("80", vm.MaxStepsText);
        Assert.Equal("1280", vm.MaxImageWidthText);
        Assert.Contains(".md", vm.VaultExtensionsText);

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
        vm.BlockedProcessesText = "KeePassXC.exe";
        vm.ElevationPatternsText = "\\bsudo\\b";
        vm.ElevatedTargetsText = "regedit\nmsconfig";

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
        Assert.Equal(new[] { "KeePassXC" }, s.Safety.BlockedProcesses);
        Assert.Equal(new[] { "\\bsudo\\b" }, s.Safety.ElevationTextPatterns);
        Assert.Equal(new[] { "regedit", "msconfig" }, s.Safety.ElevatedLaunchTargets);
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
        vm.OverlayPosition = OverlayCorner.TopCenter;
        vm.CloseToTray = true;
        vm.ShowThinking = false;
        vm.ShowScreenshots = false;
        vm.StartWithWindows = true;
        vm.VaultEnabled = false;
        vm.VaultAllowWrites = true;
        vm.ShowWelcomeAgain = false;

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
        Assert.False(s.Screen.DrawCursor);
        Assert.Equal(CoordinateMode.Normalized1000, s.Screen.Coordinates);
        Assert.False(s.Screen.ScreenshotAfterAction);
        Assert.False(s.Ui.MinimizeWhileWorking);
        Assert.False(s.Ui.ShowOverlay);
        Assert.Equal(OverlayCorner.TopCenter, s.Ui.OverlayPosition);
        Assert.True(s.Ui.CloseToTray);
        Assert.False(s.Ui.ShowThinking);
        Assert.False(s.Ui.ShowScreenshots);
        Assert.True(s.Ui.StartWithWindows);
        Assert.False(s.Vault.Enabled);
        Assert.True(s.Vault.AllowWrites);
        Assert.True(s.FirstRunCompleted);
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
        Assert.False(vm.TryCommit());
        Assert.NotEqual("", vm.Errors[nameof(SettingsViewModel.MonitorIndex)]);
        vm.MonitorIndex = 1;
        Assert.True(vm.TryCommit());
    }

    [Fact]
    public void Settings_vm_reverting_a_committed_value_applies_it()
    {
        using var vm = new SettingsViewModel(SettingsStore.CreateDefault());
        vm.MaxStepsText = "42";
        vm.BlockedProcessesText = "KeePassXC";
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
        vm.VaultPath = "relative\\folder";
        Assert.Contains("full folder path", vm.VaultPathStatus);
    }

    [Fact]
    public void Admin_restart_offer_depends_on_elevation()
    {
        using var vm = new SettingsViewModel(SettingsStore.CreateDefault()) { IsElevated = false };
        Assert.False(vm.ShowRestartAsAdmin);
        vm.AllowAdmin = true;
        Assert.True(vm.ShowRestartAsAdmin);
        vm.ApplyReport(MakeReport(elevated: true));
        Assert.True(vm.IsElevated);
        Assert.False(vm.ShowRestartAsAdmin);
    }

    // ------------------------------------------------------------------ detection

    [Fact]
    public void Detected_items_describe_the_environment()
    {
        var report = MakeReport(
            claude: new CliToolStatus("claude", Path.Combine("bin", "claude.exe"), "2.1.293", false, null),
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

        var gemini = items.Single(i => i.Title == "Gemini CLI");
        Assert.False(gemini.CanAdd);

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

        var welcome = new SettingsWelcomeViewModel(SettingsStore.CreateDefault(), partial);
        Assert.Equal(WelcomeClaudeState.NotFound, welcome.ClaudeState);
        Assert.Contains(welcome.OtherProviders, l => l.Contains("LM Studio"));
    }

    [Theory]
    [InlineData("qwen2.5vl:7b")]
    public void PickModel_prefers_vision(string expected)
    {
        var models = new[] { new ModelInfo("text", "t", false, true, null), new ModelInfo(expected, "v", true, true, null) };
        Assert.Equal(expected, SettingsViewModel.PickModel(models, "default"));
        Assert.Equal("b", SettingsViewModel.PickModel(new[] { new ModelInfo("a", "a", null, null, null), new ModelInfo("b", "b", null, null, null) }, "b"));
        Assert.Equal("a", SettingsViewModel.PickModel(new[] { new ModelInfo("a", "a", null, null, null) }, "zzz"));
        Assert.Equal("default", SettingsViewModel.PickModel(Array.Empty<ModelInfo>(), "default"));
        Assert.Null(SettingsViewModel.PickModel(Array.Empty<ModelInfo>(), ""));
    }

    [Fact]
    public async Task Detection_populates_panels_and_add_buttons_create_profiles()
    {
        var claudePath = Path.Combine(Path.GetTempPath(), "fake-bin", "claude.exe");
        var detector = new FakeDetector
        {
            Report = MakeReport(
                claude: new CliToolStatus("claude", claudePath, "2.1.293", true, "claude.ai, max"),
                ollama: new LocalServerStatus("Ollama", "http://127.0.0.1:11434", true, new[] { new ModelInfo("gemma3:12b", "g", true, true, null) }),
                vaults: new[] { Path.Combine(Path.GetTempPath(), "Vault A") }),
        };
        using var vm = new SettingsViewModel(SettingsStore.CreateDefault(), detector: detector);
        await vm.DetectEnvironmentAsync();

        Assert.Equal(1, detector.Calls);
        Assert.False(vm.IsDetecting);
        Assert.Equal("", vm.DetectionStatus);
        Assert.NotNull(vm.Report);
        Assert.Single(vm.DetectedVaults);
        Assert.Equal(2, vm.MonitorOptions.Count);
        Assert.Equal("Monitor 1: 2560 x 1440, 150% scale, primary", vm.MonitorOptions[0].Label);
        Assert.Contains(claudePath, vm.Profiles[0].CliHint);
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
        Assert.Equal(0, catalog.Calls); // nothing is fetched before the window is shown

        await vm.StartAsync();
        Assert.Equal(1, catalog.Calls);
        Assert.Equal(1, detector.Calls);

        vm.SelectedProfile = second;
        await WaitUntil(() => catalog.Calls == 2 && !second.IsLoadingModels);
        vm.SelectedProfile = vm.Profiles[0];
        vm.SelectedProfile = second;
        await Task.Delay(50);
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
            envVars: new[] { "GROQ_API_KEY" }));
        Assert.True(vm.HasOtherProviders);
        Assert.Contains(vm.OtherProviders, l => l.Contains("Ollama is running with 2 models, 1 with vision"));
        Assert.Contains(vm.OtherProviders, l => l.Contains("Gemini CLI"));
        Assert.Contains(vm.OtherProviders, l => l.Contains("GROQ_API_KEY"));
        Assert.Contains("Ctrl+Shift+F9", vm.StopText);
        Assert.Contains("top-left corner", vm.StopText);
        Assert.Contains("off", vm.AdminText);
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

    // ------------------------------------------------------------------ startup registration

    [Fact]
    public void Startup_registration_writes_only_the_given_key()
    {
        var keyPath = @"Software\DeskPilotTest-" + Guid.NewGuid().ToString("N");
        var exe = Path.Combine(Path.GetTempPath(), "DeskPilotTest", "DeskPilot.exe");
        var reg = new StartupRegistration(keyPath, "DeskPilot", () => exe);
        try
        {
            Assert.Equal(keyPath, reg.KeyPath);
            Assert.False(reg.IsEnabled());
            Assert.False(reg.Sync(false));

            Assert.True(reg.Sync(true));
            Assert.True(reg.IsEnabled());
            Assert.True(reg.IsCurrent());
            Assert.Equal($"\"{exe}\" --minimized", reg.GetRegisteredCommand());
            Assert.False(reg.Sync(true)); // already current

            // The exe moved: Sync re-registers the new path.
            var moved = Path.Combine(Path.GetTempPath(), "DeskPilotTest2", "DeskPilot.exe");
            var reg2 = new StartupRegistration(keyPath, "DeskPilot", () => moved);
            Assert.True(reg2.IsEnabled());
            Assert.False(reg2.IsCurrent());
            Assert.True(reg2.Sync(true));
            Assert.Equal($"\"{moved}\" --minimized", reg2.GetRegisteredCommand());

            Assert.True(reg.Sync(false));
            Assert.False(reg.IsEnabled());
            reg.Disable(); // removing twice is fine
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
        Assert.Null(Registry.CurrentUser.OpenSubKey(keyPath));
    }

    [Fact]
    public void Startup_registration_refuses_dotnet_host()
    {
        var keyPath = @"Software\DeskPilotTest-" + Guid.NewGuid().ToString("N");
        try
        {
            var reg = new StartupRegistration(keyPath, "DeskPilot", () => Path.Combine(Path.GetTempPath(), "dotnet.exe"));
            Assert.Null(reg.ExpectedCommand);
            Assert.Throws<InvalidOperationException>(() => reg.Enable());
            Assert.Null(new StartupRegistration(keyPath, "DeskPilot", () => null).ExpectedCommand);
            Assert.Equal(StartupRegistration.RunKeyPath, StartupRegistration.Default.KeyPath);
            Assert.Equal("\"x\\DeskPilot.exe\" --minimized", StartupRegistration.BuildCommand("x\\DeskPilot.exe"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    // ------------------------------------------------------------------ converters

    [Fact]
    public void Converters_map_values()
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var radio = new SettingsValueToBoolConverter();
        Assert.Equal(true, radio.Convert(ThinkingMode.On, typeof(bool?), "On", culture));
        Assert.Equal(false, radio.Convert(ThinkingMode.Off, typeof(bool?), "On", culture));
        Assert.Equal(ThinkingMode.Off, radio.ConvertBack(true, typeof(ThinkingMode), "Off", culture));
        Assert.Equal(System.Windows.Data.Binding.DoNothing, radio.ConvertBack(false, typeof(ThinkingMode), "Off", culture));
        Assert.Equal("png", radio.ConvertBack(true, typeof(string), "png", culture));

        var eq = new SettingsEqualsToVisibilityConverter();
        Assert.Equal(Visibility.Visible, eq.Convert("vault", typeof(Visibility), "models|vault", culture));
        Assert.Equal(Visibility.Collapsed, eq.Convert("screen", typeof(Visibility), "models|vault", culture));

        Assert.Equal(Visibility.Collapsed, new SettingsBoolToVisibilityConverter().Convert(false, typeof(Visibility), null, culture));
        Assert.Equal(Visibility.Visible, new SettingsBoolToVisibilityConverter { Invert = true }.Convert(false, typeof(Visibility), null, culture));
        Assert.Equal(Visibility.Collapsed, new SettingsTextToVisibilityConverter().Convert("", typeof(Visibility), null, culture));
        Assert.Equal(Visibility.Visible, new SettingsTextToVisibilityConverter().Convert("x", typeof(Visibility), null, culture));
    }

    [Fact]
    public void Error_bag_raises_indexer_changes()
    {
        var bag = new SettingsErrorBag();
        var raised = new List<string?>();
        bag.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        bag.Set("A", "bad");
        Assert.Equal("bad", bag["A"]);
        Assert.Equal("", bag["B"]);
        Assert.Contains("Item[]", raised);
        Assert.True(bag.HasErrors);
        bag.Remove("A");
        Assert.False(bag.HasErrors);
    }

    // ------------------------------------------------------------------ windows (constructed, never shown)

    [Fact]
    public void Windows_construct_and_bind_without_being_shown()
    {
        var dir = NewTempDir();
        try
        {
            RunSta(() =>
            {
                var bindingErrors = new BindingErrorListener();
                try
                {
                    var store = new SettingsStore(Path.Combine(dir, "settings.json"));
                    var session = new FakeSession();
                    var catalog = new FakeCatalog { Result = new ModelListResult(Array.Empty<ModelInfo>(), null) };
                    var detector = new FakeDetector { Report = MakeReport() };

                    var settings = new SettingsWindow(store, session, catalog, detector);
                    var vm = settings.ViewModel;
                    Assert.Same(vm, settings.DataContext);
                    Assert.Equal(store.FilePath, vm.SettingsFilePath);
                    Assert.NotSame(store.Current, vm.Settings);

                    // Walk every page and every kind of profile so all templates and bindings are exercised.
                    vm.ApplyReport(MakeReport(vaults: new[] { Path.Combine(dir, "vault") }));
                    vm.AdvancedMode = true;
                    foreach (var preset in new[] { ProviderPresets.OpenAiId, ProviderPresets.AnthropicApiId, ProviderPresets.OllamaId, ProviderPresets.GeminiCliId })
                        vm.AddPreset(preset);
                    foreach (var section in vm.VisibleSections.ToList())
                    {
                        vm.SelectedSection = section;
                        foreach (var profile in vm.Profiles.ToList())
                        {
                            vm.SelectedProfile = profile;
                            Layout(settings, 1000, 740);
                        }
                    }
                    Assert.Equal(0, detector.Calls); // detection starts on Loaded, which never happens here
                    Assert.Equal(0, session.ReloadCount);

                    var menu = settings.BuildAddMenu();
                    var presetItems = menu.Items.OfType<MenuItem>().Where(m => m.CommandParameter is string).ToList();
                    Assert.Equal(ProviderPresets.All.Count, presetItems.Count);
                    Assert.All(presetItems, m => Assert.False(string.IsNullOrEmpty(m.ToolTip as string)));
                    Assert.Equal(new[] { "Cloud", "Local", "Agents" }, menu.Items.OfType<MenuItem>().Where(m => !m.IsEnabled).Select(m => (string)m.Header));

                    var welcome = new WelcomeWindow(store, MakeReport(vaults: new[] { Path.Combine(dir, "vault") }));
                    Assert.Same(welcome.ViewModel, welcome.DataContext);
                    Assert.Equal(2, welcome.ViewModel.VaultOptions.Count);
                    Layout(welcome, 780, 720);

                    var preview = new PromptPreviewWindow("Hello prompt");
                    Assert.Equal("Hello prompt", preview.Prompt);
                    Layout(preview, 860, 660);
                    Assert.StartsWith("12 characters, about 3 tokens", PromptPreviewWindow.Describe("Hello prompt"));

                    settings.Close();
                    welcome.Close();
                    preview.Close();
                    Assert.False(File.Exists(store.FilePath)); // nothing was saved
                }
                finally
                {
                    bindingErrors.Dispose();
                }
                Assert.True(bindingErrors.Errors.Count == 0, "Binding errors:\n" + string.Join("\n", bindingErrors.Errors));
            });
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public void Model_box_keeps_the_model_when_the_list_or_profile_changes()
    {
        var dir = NewTempDir();
        try
        {
            RunSta(() =>
            {
                var store = new SettingsStore(Path.Combine(dir, "settings.json"));
                var window = new SettingsWindow(store, new FakeSession(), new FakeCatalog(), new FakeDetector { Report = MakeReport() });
                var vm = window.ViewModel;
                var claude = vm.Profiles[0];
                var openai = vm.AddPreset(ProviderPresets.OpenAiId);
                var combo = (ComboBox)window.FindName("ModelCombo");

                vm.SelectedProfile = claude;
                Layout(window, 1000, 740);
                combo.SelectedItem = claude.ModelOptions.First(o => o.Id == "sonnet"); // the user picks from the list
                DoEvents();
                Assert.Equal("sonnet", claude.Model);
                Assert.Equal(-1, combo.SelectedIndex);
                Assert.Equal("sonnet", combo.Text);

                // A refresh replaces the list the picked item came from.
                claude.SetModels(new[] { new ModelInfo("opus", "Opus", true, true, true), new ModelInfo("haiku", "Haiku", true, true, true) });
                Layout(window, 1000, 740);
                Assert.Equal("sonnet", claude.Model);

                // Switching profiles must not carry one profile's model into the other.
                vm.SelectedProfile = openai;
                Layout(window, 1000, 740);
                Assert.Equal("gpt-5-mini", openai.Model);
                Assert.Equal("sonnet", claude.Model);
                vm.SelectedProfile = claude;
                Layout(window, 1000, 740);
                Assert.Equal("sonnet", claude.Model);
                Assert.Equal("gpt-5-mini", openai.Model);
                window.Close();
            });
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public void Switching_profiles_and_redetecting_keep_every_choice()
    {
        var dir = NewTempDir();
        try
        {
            RunSta(() =>
            {
                var bindingErrors = new BindingErrorListener();
                var store = new SettingsStore(Path.Combine(dir, "settings.json"));
                store.Update(s =>
                {
                    var hand = ProviderPresets.CreateProfile(ProviderPresets.AnthropicApiId);
                    hand.Effort = "turbo-custom"; // an effort value the picker does not know, from a hand-edited file
                    s.Profiles.Add(hand);
                });
                var window = new SettingsWindow(store, new FakeSession(), new FakeCatalog(), new FakeDetector { Report = MakeReport() });
                var vm = window.ViewModel;
                var claude = vm.Profiles[0];
                claude.Effort = "high";
                claude.Thinking = ThinkingMode.On;
                var custom = vm.Profiles[1];
                var openai = vm.AddPreset(ProviderPresets.OpenRouterId);
                openai.Effort = "low";
                openai.Thinking = ThinkingMode.Off;
                openai.ReasoningStyle = ReasoningStyle.OpenRouter;
                vm.AdvancedMode = true;
                vm.MonitorSelection = MonitorSelection.Specific;
                vm.MonitorIndex = 1;

                foreach (var section in new[] { SettingsViewModel.SectionModels, SettingsViewModel.SectionAdvanced, SettingsViewModel.SectionScreen })
                {
                    vm.SelectSection(section);
                    foreach (var p in new[] { claude, openai, custom, claude, custom, openai })
                    {
                        vm.SelectedProfile = p;
                        Layout(window, 1000, 740);
                        if (section == SettingsViewModel.SectionModels)
                            Assert.Equal(p.Effort, FindByAutomationName<ComboBox>(window, "Effort").SelectedValue);
                    }
                }
                var monitorBox = FindByAutomationName<ComboBox>(window, "Monitor");
                Assert.Equal(1, monitorBox.SelectedValue);
                vm.ApplyReport(MakeReport()); // rebuilds the monitor list while the page is laid out
                Layout(window, 1000, 740);
                vm.ApplyReport(MakeReport());
                Layout(window, 1000, 740);
                bindingErrors.Dispose();

                Assert.Equal("high", claude.Effort);
                Assert.Equal(ThinkingMode.On, claude.Thinking);
                Assert.Equal("low", openai.Effort);
                Assert.Equal(ThinkingMode.Off, openai.Thinking);
                Assert.Equal(ReasoningStyle.OpenRouter, openai.ReasoningStyle);
                Assert.Equal("turbo-custom", custom.Effort);
                Assert.Equal(1, vm.MonitorIndex);
                Assert.True(bindingErrors.Errors.Count == 0, "Binding errors:\n" + string.Join("\n", bindingErrors.Errors));
                window.Close();
            });
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    private static T FindByAutomationName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node is T match && System.Windows.Automation.AutomationProperties.GetName(match) == name) return match;
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) queue.Enqueue(child);
        }
        throw new InvalidOperationException($"No {typeof(T).Name} named '{name}'");
    }

    /// <summary>Runs the work queued on this thread's dispatcher (no window is involved).</summary>
    private static void DoEvents() =>
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));

    private static void Layout(Window window, double width, double height)
    {
        if (window.Content is not FrameworkElement root) return;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    // ------------------------------------------------------------------ helpers

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
                new MonitorInfo(0, @"\\.\DISPLAY1", new ScreenRect(0, 0, 2560, 1440), new ScreenRect(0, 0, 2560, 1400), true, 1.5),
                new MonitorInfo(1, @"\\.\DISPLAY2", new ScreenRect(2560, 0, 1440, 1080), new ScreenRect(2560, 0, 1440, 1040), false, 1.0),
            },
            elevated);

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskpilot-uisettings-" + Guid.NewGuid().ToString("N"));
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

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Collects WPF binding errors that mention this module's view models.</summary>
    private sealed class BindingErrorListener : TraceListener
    {
        private static readonly string[] OwnTypes =
        {
            nameof(SettingsViewModel), nameof(ProfileViewModel), nameof(SettingsWelcomeViewModel), nameof(SettingsSectionItem),
            nameof(SettingsModelOption), nameof(SettingsDetectedItem), nameof(SettingsPlaceholderItem), nameof(SettingsWelcomeVaultOption),
            nameof(SettingsMonitorOption), "SettingsOption", nameof(SettingsErrorBag),
        };
        private readonly StringBuilder _line = new();

        public BindingErrorListener()
        {
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            PresentationTraceSources.DataBindingSource.Listeners.Add(this);
        }

        public List<string> Errors { get; } = new();

        public override void Write(string? message) => _line.Append(message);

        public override void WriteLine(string? message)
        {
            _line.Append(message);
            var text = _line.ToString();
            _line.Clear();
            if (OwnTypes.Any(t => text.Contains(t, StringComparison.Ordinal))) Errors.Add(text);
        }

        protected override void Dispose(bool disposing)
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
            base.Dispose(disposing);
        }
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
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
