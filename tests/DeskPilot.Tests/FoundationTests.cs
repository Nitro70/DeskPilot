using System.Text.Json;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Prompts;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;

namespace DeskPilot.Tests;

public class FoundationTests
{
    [Theory]
    [InlineData("ctrl+c", "ctrl+c")]
    [InlineData("Ctrl + Shift + Esc", "ctrl+shift+escape")]
    [InlineData("control+alt+delete", "ctrl+alt+delete")]
    [InlineData("WIN", "win")]
    [InlineData("cmd+space", "win+space")]
    [InlineData("ctrl++", "ctrl++")]
    [InlineData("shift+Return", "shift+enter")]
    [InlineData("ctrl-c", "ctrl+c")]
    [InlineData("alt+F4", "alt+f4")]
    public void KeyCombo_normalizes(string input, string expected)
    {
        Assert.True(KeyCombo.TryParse(input, out var combo, out var error), error);
        Assert.Equal(expected, combo.ToString());
    }

    [Fact]
    public void KeyCombo_rejects_two_main_keys()
    {
        Assert.False(KeyCombo.TryParse("ctrl+a+b", out _, out var error));
        Assert.Contains("more than one", error);
    }

    [Fact]
    public void Settings_roundtrip_and_defaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskpilot-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            Assert.Single(store.Current.Profiles);
            Assert.Equal(ProviderKind.ClaudeCli, store.Current.ActiveProfile!.Kind);
            Assert.Equal("haiku", store.Current.ActiveProfile!.Model);
            Assert.False(store.Current.Safety.AllowAdmin);

            store.Update(s => s.Vault.Path = @"C:\Notes");
            var reloaded = new SettingsStore(Path.Combine(dir, "settings.json"));
            Assert.Equal(@"C:\Notes", reloaded.Current.Vault.Path);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Secret_roundtrip_and_mask()
    {
        var protectedKey = SecretProtector.Protect("sk-test-1234567890");
        Assert.NotEqual("sk-test-1234567890", protectedKey);
        Assert.Equal("sk-test-1234567890", SecretProtector.Unprotect(protectedKey));
        Assert.Equal("sk-t…7890", SecretProtector.Mask("sk-test-1234567890"));
        Assert.Equal("", SecretProtector.Unprotect("not-base64!"));
    }

    [Fact]
    public void JsonArgs_is_lenient()
    {
        var args = JsonArgs.ParseArguments("{\"x\":\"12.6\",\"y\":30,\"flag\":\"yes\",\"keys\":\"ctrl,c\"}");
        Assert.Equal(13, JsonArgs.GetInt(args, "x"));
        Assert.Equal(30, JsonArgs.GetInt(args, "y"));
        Assert.True(JsonArgs.GetBool(args, "flag"));
        Assert.Equal(new[] { "ctrl", "c" }, JsonArgs.GetStringList(args, "keys"));
        Assert.Equal(JsonValueKind.Object, JsonArgs.ParseArguments("not json").ValueKind);
        Assert.Equal(5, JsonArgs.GetInt(JsonArgs.ParseArguments("\"{\\\"x\\\":5}\""), "x"));
    }

    [Fact]
    public void CommandLine_split_handles_quotes()
    {
        Assert.Equal(new[] { "--flag", "a b", "c\"d" }, CommandLine.Split("--flag \"a b\" c\\\"d"));
        Assert.Empty(CommandLine.Split("   "));
    }

    [Fact]
    public void Prompt_fills_placeholders()
    {
        var s = SettingsStore.CreateDefault();
        var ctx = new PromptContext(s, s.ActiveProfile!, Array.Empty<ToolSpec>(), "screenshots are 1280x720", VaultAvailable: true, new DateTime(2026, 10, 7, 9, 30, 0));
        var prompt = PromptBuilder.Build(ctx);
        Assert.DoesNotContain("{{", prompt);
        Assert.Contains("Wednesday, October 7, 2026", prompt);
        Assert.Contains("vault_search", prompt);
        Assert.Contains("Administrator mode is OFF", prompt);

        s.Vault.Path = "";
        var noVault = PromptBuilder.Build(ctx with { VaultAvailable = false });
        Assert.DoesNotContain("vault_search", noVault);
    }

    [Fact]
    public async Task ObservedToolHost_stops_and_limits_steps()
    {
        var control = new AgentRunControl();
        var events = new List<AgentEvent>();
        var inner = new EchoHost();
        var host = new ObservedToolHost(inner, control, () => 2, e => events.Add(e));
        control.BeginTurn();
        var a = JsonArgs.EmptyObject();
        Assert.False((await host.ExecuteAsync("echo", a, CancellationToken.None)).IsError);
        Assert.False((await host.ExecuteAsync("echo", a, CancellationToken.None)).IsError);
        Assert.True((await host.ExecuteAsync("echo", a, CancellationToken.None)).IsError);
        control.RequestStop("test");
        var stopped = await host.ExecuteAsync("echo", a, CancellationToken.None);
        Assert.StartsWith("STOPPED", stopped.Text);
        Assert.Equal(8, events.Count);
    }

    private sealed class EchoHost : IToolHost
    {
        public IReadOnlyList<ToolSpec> GetTools() => new[] { ToolSpec.Create("echo", "echo", "{\"type\":\"object\"}") };
        public Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken ct) => Task.FromResult(ToolResult.Ok("ok"));
    }
}
