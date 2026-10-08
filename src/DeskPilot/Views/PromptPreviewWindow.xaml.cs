using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using DeskPilot.Services;

namespace DeskPilot.Views;

/// <summary>Read-only view of a rendered system prompt (opened from the Advanced settings page).</summary>
public partial class PromptPreviewWindow : Window
{
    public PromptPreviewWindow(string prompt)
    {
        InitializeComponent();
        NativeUi.Prepare(this);

        Prompt = prompt ?? "";
        PromptText.Text = Prompt;
        InfoText.Text = Describe(Prompt);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !e.Handled)
            {
                e.Handled = true;
                Close();
            }
        };
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

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Prompt);
            CopyButton.Content = "Copied";
        }
        catch (ExternalException)
        {
            // Another app holds the clipboard open; the user can select the text and copy instead.
            CopyButton.Content = "Clipboard busy";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
