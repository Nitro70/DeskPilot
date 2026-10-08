using System.IO;
using Microsoft.Win32;

namespace DeskPilot.Services;

/// <summary>
/// "Start with Windows": a per-user Run entry (HKCU, no admin rights needed) that starts DeskPilot
/// minimized at logon. The key path and the exe path are injectable so tests never touch the real
/// Run key.
/// </summary>
public sealed class StartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultValueName = "DeskPilot";
    public const string MinimizedArgument = "--minimized";

    private readonly string _keyPath;
    private readonly string _valueName;
    private readonly Func<string?> _exePath;

    /// <param name="keyPath">Subkey of HKEY_CURRENT_USER that holds the value.</param>
    /// <param name="valueName">Registry value name.</param>
    /// <param name="exePath">The program to register; defaults to the running exe.</param>
    public StartupRegistration(string keyPath = RunKeyPath, string valueName = DefaultValueName, Func<string?>? exePath = null)
    {
        _keyPath = keyPath;
        _valueName = valueName;
        _exePath = exePath ?? (() => Environment.ProcessPath);
    }

    /// <summary>The real Run entry for the running DeskPilot.exe.</summary>
    public static StartupRegistration Default { get; } = new();

    public string KeyPath => _keyPath;

    public string ValueName => _valueName;

    public static string BuildCommand(string exePath) => $"\"{exePath}\" {MinimizedArgument}";

    /// <summary>The command this program would register, or null when it cannot be registered (no exe, or running under dotnet.exe).</summary>
    public string? ExpectedCommand
    {
        get
        {
            var exe = _exePath();
            if (string.IsNullOrWhiteSpace(exe)) return null;
            if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase)) return null;
            return BuildCommand(exe);
        }
    }

    public string? GetRegisteredCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);
        return key?.GetValue(_valueName) as string;
    }

    public bool IsEnabled() => !string.IsNullOrWhiteSpace(GetRegisteredCommand());

    /// <summary>True when the entry exists and points at this program.</summary>
    public bool IsCurrent()
    {
        var expected = ExpectedCommand;
        return expected != null && string.Equals(GetRegisteredCommand(), expected, StringComparison.OrdinalIgnoreCase);
    }

    public void Enable()
    {
        var command = ExpectedCommand
            ?? throw new InvalidOperationException("Start with Windows needs DeskPilot.exe; it cannot be registered while running under dotnet.exe.");
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true)
            ?? throw new InvalidOperationException($@"Could not open HKEY_CURRENT_USER\{_keyPath}.");
        key.SetValue(_valueName, command, RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
        key?.DeleteValue(_valueName, throwOnMissingValue: false);
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled) Enable();
        else Disable();
    }

    /// <summary>
    /// Makes the registry match the setting: registers, re-registers when the exe moved, or removes.
    /// Returns true when something was changed.
    /// </summary>
    public bool Sync(bool enabled)
    {
        if (enabled)
        {
            if (IsCurrent()) return false;
            Enable();
            return true;
        }
        if (!IsEnabled()) return false;
        Disable();
        return true;
    }
}
