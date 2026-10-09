using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DeskPilot.Linux.Views;

/// <summary>Shown when the desktop services cannot be created (no session, missing helper tools): what failed and how to fix it.</summary>
public partial class StartupErrorWindow : Window
{
    /// <summary>Designer only.</summary>
    public StartupErrorWindow()
    {
        InitializeComponent();
    }

    public StartupErrorWindow(string error, IReadOnlyList<string> notes, string? logPath) : this()
    {
        ErrorText.Text = string.IsNullOrWhiteSpace(error) ? "Unknown error." : error;
        Notes = notes;
        NotesList.ItemsSource = notes;
        NotesCard.IsVisible = notes.Count > 0;
        LogText.Text = string.IsNullOrWhiteSpace(logPath) ? "" : $"Details are in the log file: {logPath}";
    }

    public IReadOnlyList<string> Notes { get; } = Array.Empty<string>();

    /// <summary>The user pressed Exit (closing the window means the same).</summary>
    public event EventHandler? ExitRequested;

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }
}
