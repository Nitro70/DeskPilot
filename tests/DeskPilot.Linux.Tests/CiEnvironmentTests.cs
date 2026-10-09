using System.Diagnostics;

namespace DeskPilot.Linux.Tests;

/// <summary>Proves the CI desktop sessions are what the other tests expect (a 1920x1080 X11 or Wayland screen).</summary>
public class CiEnvironmentTests
{
    internal static (int Exit, string Output) Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(15000)) { try { p.Kill(true); } catch (InvalidOperationException) { } }
        return (p.HasExited ? p.ExitCode : -1, stdout.Result + stderr.Result);
    }

    [X11Fact]
    public void X11_display_is_1920x1080()
    {
        var (exit, output) = Run("xdpyinfo");
        Assert.True(exit == 0, output);
        Assert.Contains("1920x1080", output);
    }

    [WaylandFact]
    public void Sway_headless_output_exists()
    {
        if (Environment.GetEnvironmentVariable("DESKPILOT_TEST_SESSION") != "wayland") return;
        var (exit, output) = Run("swaymsg", "-t", "get_outputs");
        Assert.True(exit == 0, output);
        Assert.Contains("HEADLESS", output);
    }
}
