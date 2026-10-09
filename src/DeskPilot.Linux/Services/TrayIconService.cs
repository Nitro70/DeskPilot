using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;

namespace DeskPilot.Linux.Services;

/// <summary>
/// Tray icon (StatusNotifierItem on Linux) with Show / Stop / Settings / Exit. Some desktops have no tray
/// (GNOME without the AppIndicator extension): the icon then simply does not appear. Create and use on the UI thread.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    public const string IconUri = "avares://deskpilot/Assets/DeskPilot.png";

    private readonly TrayIcon _trayIcon;
    private readonly NativeMenuItem _stopItem;
    private bool _disposed;

    public TrayIconService()
    {
        var menu = new NativeMenu();
        menu.Add(Item("Show DeskPilot", () => ShowRequested?.Invoke()));
        _stopItem = Item("Stop the current task", () => StopRequested?.Invoke());
        _stopItem.IsEnabled = false;
        menu.Add(_stopItem);
        menu.Add(Item("Settings", () => SettingsRequested?.Invoke()));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item("Exit DeskPilot", () => ExitRequested?.Invoke()));

        _trayIcon = new TrayIcon
        {
            Icon = LoadAppIcon(),
            ToolTipText = "DeskPilot",
            Menu = menu,
            IsVisible = true,
        };
        _trayIcon.Clicked += (_, _) => ShowRequested?.Invoke();

        // Registered with the application so it is removed with the app even after a crash in shutdown.
        if (Application.Current is { } app)
        {
            var icons = TrayIcon.GetIcons(app) ?? new TrayIcons();
            icons.Add(_trayIcon);
            TrayIcon.SetIcons(app, icons);
        }
    }

    public event Action? ShowRequested;
    public event Action? StopRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public bool IsStopEnabled => _stopItem.IsEnabled;
    public string ToolTipText => _trayIcon.ToolTipText ?? "";

    /// <summary>Updates the tooltip and enables Stop while a task runs.</summary>
    public void SetState(AgentState state)
    {
        if (_disposed) return;
        bool busy = state is AgentState.Starting or AgentState.Running or AgentState.Stopping;
        _stopItem.IsEnabled = busy;
        var label = state switch
        {
            AgentState.Running => "working",
            AgentState.Starting => "starting",
            AgentState.Stopping => "stopping",
            AgentState.Error => "error",
            _ => "idle",
        };
        _trayIcon.ToolTipText = $"DeskPilot ({label})";
    }

    /// <summary>The app icon, or null when it cannot be loaded.</summary>
    public static WindowIcon? LoadAppIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(IconUri));
            return new WindowIcon(stream);
        }
        catch (Exception ex)
        {
            Log.Warn($"App icon could not be loaded: {ex.Message}");
            return null;
        }
    }

    private static NativeMenuItem Item(string header, Action onClick)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => onClick();
        return item;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _trayIcon.IsVisible = false;
            if (Application.Current is { } app && TrayIcon.GetIcons(app) is { } icons) icons.Remove(_trayIcon);
            _trayIcon.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Tray icon cleanup failed: {ex.Message}");
        }
    }
}
