using System.IO;
using System.Windows;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DeskPilot.Services;

/// <summary>Notification-area icon with Show / Stop / Settings / Exit. Create and use on the UI thread.</summary>
public sealed class TrayIconService : IDisposable
{
    public const string IconResourceUri = "pack://application:,,,/DeskPilot;component/Assets/DeskPilot.ico";

    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _stopItem;
    private readonly Drawing.Icon? _icon;
    private bool _disposed;

    public TrayIconService()
    {
        _icon = LoadAppIcon(Forms.SystemInformation.SmallIconSize);

        _menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            Renderer = new Forms.ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = false },
            BackColor = DarkMenuColors.Background,
            ForeColor = DarkMenuColors.Text,
            Padding = new Forms.Padding(2, 4, 2, 4),
        };
        var showItem = AddItem("Show DeskPilot", () => ShowRequested?.Invoke());
        showItem.Font = new Drawing.Font(showItem.Font, Drawing.FontStyle.Bold);
        _stopItem = AddItem("Stop the current task", () => StopRequested?.Invoke());
        _stopItem.Enabled = false;
        AddItem("Settings", () => SettingsRequested?.Invoke());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        AddItem("Exit DeskPilot", () => ExitRequested?.Invoke());

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon ?? Drawing.SystemIcons.Application,
            Text = "DeskPilot",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke();
        _notifyIcon.BalloonTipClicked += (_, _) => ShowRequested?.Invoke();
    }

    public event Action? ShowRequested;
    public event Action? StopRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    /// <summary>Updates the tooltip and enables Stop while a task runs.</summary>
    public void SetState(AgentState state)
    {
        if (_disposed) return;
        bool busy = state is AgentState.Starting or AgentState.Running or AgentState.Stopping;
        _stopItem.Enabled = busy;
        var label = state switch
        {
            AgentState.Running => "working",
            AgentState.Starting => "starting",
            AgentState.Stopping => "stopping",
            AgentState.Error => "error",
            _ => "idle",
        };
        _notifyIcon.Text = $"DeskPilot ({label})";
    }

    public void ShowBalloon(string title, string text)
    {
        if (_disposed) return;
        try { _notifyIcon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info); }
        catch (Exception ex) { Log.Warn($"Tray balloon failed: {ex.Message}"); }
    }

    /// <summary>The app icon from the WPF resources at the requested size, or null when unavailable.</summary>
    public static Drawing.Icon? LoadAppIcon(Drawing.Size size)
    {
        try
        {
            var info = Application.GetResourceStream(new Uri(IconResourceUri, UriKind.Absolute));
            if (info == null) return null;
            using var stream = info.Stream;
            return new Drawing.Icon(stream, size);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UriFormatException)
        {
            Log.Warn($"App icon could not be loaded: {ex.Message}");
            return null;
        }
    }

    private Forms.ToolStripMenuItem AddItem(string text, Action onClick)
    {
        var item = new Forms.ToolStripMenuItem(text) { ForeColor = DarkMenuColors.Text, Padding = new Forms.Padding(4, 3, 4, 3) };
        item.Click += (_, _) => onClick();
        _menu.Items.Add(item);
        return item;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon?.Dispose();
    }

    private sealed class DarkMenuColors : Forms.ProfessionalColorTable
    {
        public static readonly Drawing.Color Background = Drawing.Color.FromArgb(0x14, 0x17, 0x1F);
        public static readonly Drawing.Color Text = Drawing.Color.FromArgb(0xE8, 0xEA, 0xF0);
        private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(0x23, 0x28, 0x36);
        private static readonly Drawing.Color Border = Drawing.Color.FromArgb(0x3B, 0x42, 0x52);

        public override Drawing.Color ToolStripDropDownBackground => Background;
        public override Drawing.Color MenuBorder => Border;
        public override Drawing.Color MenuItemBorder => Hover;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override Drawing.Color MenuItemPressedGradientBegin => Hover;
        public override Drawing.Color MenuItemPressedGradientEnd => Hover;
        public override Drawing.Color ImageMarginGradientBegin => Background;
        public override Drawing.Color ImageMarginGradientMiddle => Background;
        public override Drawing.Color ImageMarginGradientEnd => Background;
        public override Drawing.Color SeparatorDark => Border;
        public override Drawing.Color SeparatorLight => Background;
    }
}
