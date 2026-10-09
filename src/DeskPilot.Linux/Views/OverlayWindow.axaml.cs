using Avalonia;
using Avalonia.Controls;
using DeskPilot.Core.Abstractions;
using DeskPilot.Linux.ViewModels;

namespace DeskPilot.Linux.Views;

/// <summary>
/// Small always-on-top status pill shown while the agent works. It never takes focus and has no taskbar entry.
/// Linux cannot exclude a window from screenshots, so the overlay controller hides it around every capture.
/// </summary>
public partial class OverlayWindow : Window
{
    /// <summary>Designer only.</summary>
    public OverlayWindow()
    {
        InitializeComponent();
    }

    public OverlayWindow(OverlayViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Opened += (_, _) => ApplyOpaqueFallback();
    }

    /// <summary>
    /// Without a compositing manager the transparent margin around the card would show as black: draw the card
    /// edge to edge instead.
    /// </summary>
    private void ApplyOpaqueFallback()
    {
        if (ActualTransparencyLevel != WindowTransparencyLevel.None) return;
        Card.Margin = new Thickness(0);
        Card.CornerRadius = new CornerRadius(0);
        Card.BoxShadow = default;
        if (this.TryFindResource("CardBrush", ActualThemeVariant, out var brush) && brush is Avalonia.Media.IBrush b) Background = b;
    }

    /// <summary>The window's area in physical screen pixels (what the agent's coordinates use).</summary>
    public ScreenRect PhysicalBounds
    {
        get
        {
            double scale = DesktopScaling > 0 ? DesktopScaling : 1;
            var size = ClientSize;
            return new ScreenRect(Position.X, Position.Y, (int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale));
        }
    }
}
