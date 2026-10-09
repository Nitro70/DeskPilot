using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using DeskPilot.Linux.Services;

namespace DeskPilot.Linux.Views;

public enum MessageKind { Info, Warning, Error, Question }

/// <summary>
/// A small themed message box (Avalonia has none): a heading, selectable text and one or two buttons.
/// The result is true for the primary button, false for the secondary one or closing the window.
/// </summary>
public partial class MessageWindow : Window
{
    private bool _result;

    /// <summary>Designer only.</summary>
    public MessageWindow()
    {
        InitializeComponent();
    }

    public MessageWindow(string heading, string body, MessageKind kind = MessageKind.Info, string primary = "OK", string? secondary = null) : this()
    {
        HeadingText.Text = heading;
        BodyText.Text = body;
        PrimaryButton.Content = primary;
        SecondaryButton.Content = secondary ?? "";
        SecondaryButton.IsVisible = secondary != null;
        if (secondary == null) PrimaryButton.IsCancel = true;

        var (icon, fg, bg) = kind switch
        {
            MessageKind.Warning => (IconGeometries.Warning, "WarningBrush", "WarningSoftBrush"),
            MessageKind.Error => (IconGeometries.Error, "DangerBrush", "DangerSoftBrush"),
            MessageKind.Question => (IconGeometries.Info, "AccentHoverBrush", "AccentSoftBrush"),
            _ => (IconGeometries.Info, "AccentHoverBrush", "AccentSoftBrush"),
        };
        Glyph.Data = icon;
        if (this.TryFindResource(fg, ActualThemeVariant, out var f) && f is IBrush fb) Glyph.Foreground = fb;
        if (this.TryFindResource(bg, ActualThemeVariant, out var b) && b is IBrush bb) IconBox.Background = bb;

        // A question defaults to the safe answer (the secondary button).
        Opened += (_, _) => (secondary != null ? SecondaryButton : PrimaryButton).Focus();
    }

    public bool Result => _result;

    /// <summary>Shows the message modal to <paramref name="owner"/> when it is visible, otherwise as its own window.</summary>
    public static async Task<bool> ShowAsync(Window? owner, string heading, string body, MessageKind kind = MessageKind.Info,
        string primary = "OK", string? secondary = null)
    {
        var w = new MessageWindow(heading, body, kind, primary, secondary);
        if (owner is { IsVisible: true })
        {
            return await w.ShowDialog<bool>(owner);
        }
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.Closed += (_, _) => done.TrySetResult(w._result);
        w.Show();
        return await done.Task;
    }

    private void OnPrimary(object? sender, RoutedEventArgs e)
    {
        _result = true;
        Close(true);
    }

    private void OnSecondary(object? sender, RoutedEventArgs e)
    {
        _result = false;
        Close(false);
    }
}
