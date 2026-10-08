using System.Runtime.InteropServices;
using DeskPilot.Core.Abstractions;

namespace DeskPilot.Core.Desktop;

/// <summary>
/// Text clipboard access on a dedicated STA thread. Another app may hold the clipboard open for a moment,
/// so each operation is retried a few times before giving up.
/// </summary>
public sealed class WindowsClipboard : IClipboardService
{
    internal const int Attempts = 5;
    internal const int RetryDelayMs = 50;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public string? GetText() =>
        StaThread.Run(() => Retry(() =>
            System.Windows.Clipboard.ContainsText(System.Windows.TextDataFormat.UnicodeText)
                ? System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText)
                : null), Timeout, "read the clipboard");

    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        StaThread.Run(() => Retry(() =>
        {
            if (text.Length == 0) System.Windows.Clipboard.Clear();
            else System.Windows.Clipboard.SetDataObject(new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText, text), copy: true);
            return true;
        }), Timeout, "set the clipboard");
    }

    /// <summary>Retries while the clipboard is busy (COM/External exceptions such as CLIPBRD_E_CANT_OPEN).</summary>
    internal static T Retry<T>(Func<T> action, int attempts = Attempts, int delayMs = RetryDelayMs, Action<int>? sleep = null)
    {
        sleep ??= Thread.Sleep;
        for (int i = 1; ; i++)
        {
            try
            {
                return action();
            }
            catch (ExternalException) when (i < attempts)
            {
                sleep(delayMs);
            }
        }
    }
}
