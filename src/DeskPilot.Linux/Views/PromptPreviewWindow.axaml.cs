using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace DeskPilot.Linux.Views;

/// <summary>Read-only view of a rendered system prompt (opened from the Advanced settings page).</summary>
public partial class PromptPreviewWindow : Window
{
    /// <summary>Parameterless constructor for the XAML loader and designer only.</summary>
    public PromptPreviewWindow() : this("")
    {
    }

    public PromptPreviewWindow(string prompt)
    {
        InitializeComponent();
        Prompt = prompt ?? "";
        PromptText.Text = Prompt;
        InfoText.Text = Describe(Prompt);
    }

    public string Prompt { get; }

    /// <summary>"4,812 characters, about 1,203 tokens..." (a rough 4-characters-per-token estimate).</summary>
    public static string Describe(string prompt)
    {
        var chars = prompt.Length;
        var tokens = (int)Math.Ceiling(chars / 4.0);
        return $"{chars.ToString("N0", CultureInfo.InvariantCulture)} characters, about {tokens.ToString("N0", CultureInfo.InvariantCulture)} tokens. " +
               "Rendered with the edited settings and the active profile. The tool list is empty here; the real prompt names the tools available when a request starts.";
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && !e.Handled)
        {
            e.Handled = true;
            Close();
        }
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is not { } clipboard)
            {
                CopyButton.Content = "No clipboard";
                return;
            }
            await clipboard.SetTextAsync(Prompt);
            CopyButton.Content = "Copied";
        }
        catch (Exception)
        {
            // Copying can fail when the clipboard is unavailable; selecting the text and pressing Ctrl+C still works.
            CopyButton.Content = "Clipboard busy";
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
