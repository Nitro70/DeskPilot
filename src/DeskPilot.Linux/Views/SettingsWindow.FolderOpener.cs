using System.ComponentModel;
using System.Diagnostics;

namespace DeskPilot.Linux.Views;

/// <summary>Opens a folder in the user's file manager with xdg-open (the freedesktop way every desktop supports).</summary>
internal static class SettingsFolderOpener
{
    public const string Opener = "xdg-open";

    /// <summary>Creates the folder when missing, then runs xdg-open on it. Returns an error message, or null when it started.</summary>
    public static string? Open(string dir, Func<string, IReadOnlyList<string>, bool> start)
    {
        if (string.IsNullOrWhiteSpace(dir)) return "There is no folder to open.";
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Could not open the folder: " + ex.Message;
        }
        try
        {
            return start(Opener, new[] { dir }) ? null : $"Could not open the folder. It is {dir}";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return $"{Opener} is not available ({ex.Message}). The folder is {dir}";
        }
    }

    /// <summary>Starts a program without a shell and without waiting for it.</summary>
    public static bool DefaultStart(string file, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi);
        return process != null;
    }
}
