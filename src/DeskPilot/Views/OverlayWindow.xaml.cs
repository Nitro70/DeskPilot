using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DeskPilot.Services;
using DeskPilot.ViewModels;

namespace DeskPilot.Views;

/// <summary>
/// Small always-on-top status pill shown while the agent works. It never takes focus
/// (WS_EX_NOACTIVATE), has no taskbar entry (WS_EX_TOOLWINDOW) and is excluded from screen capture.
/// </summary>
public partial class OverlayWindow : Window
{
    public OverlayWindow(OverlayViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        NativeUi.Prepare(this);
        SourceInitialized += (_, _) => ApplyNoActivateStyle();
        IsVisibleChanged += (_, _) => UpdateSpinner();
    }

    public nint Handle => new WindowInteropHelper(this).Handle;

    private void ApplyNoActivateStyle()
    {
        var hwnd = Handle;
        if (hwnd == 0) return;
        var style = (long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
        style = (style | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW) & ~Win32.WS_EX_APPWINDOW;
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, (nint)style);
    }

    private void UpdateSpinner()
    {
        // Animate only while visible so a hidden overlay costs nothing.
        if (IsVisible)
        {
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever };
            ArcRotation.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
        else
        {
            ArcRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }
}
