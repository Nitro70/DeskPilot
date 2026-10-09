using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskPilot.Core.Abstractions;
using DeskPilot.Linux.Services;

namespace DeskPilot.Linux.ViewModels;

/// <summary>One entry of the conversation log. Each kind has its own DataTemplate.</summary>
public abstract class LogItem : ObservableObject
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Plain-text form for "Copy log".</summary>
    public abstract string ToPlainText();
}

public sealed class UserMessageItem : LogItem
{
    public UserMessageItem(string text) => Text = text;

    public string Text { get; }

    /// <summary>Added by the UI when sending (the session may echo it back as a UserMessageEvent).</summary>
    public bool IsLocal { get; init; }

    public override string ToPlainText() => "You: " + Text;
}

/// <summary>Assistant text or thinking: may grow while partial chunks stream in.</summary>
public abstract class StreamingTextItem : LogItem
{
    private string _text = "";
    private bool _isStreaming;

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value ?? "");
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set => SetProperty(ref _isStreaming, value);
    }

    public void Append(string chunk)
    {
        if (!string.IsNullOrEmpty(chunk)) Text += chunk;
    }
}

public sealed class AssistantTextItem : StreamingTextItem
{
    public override string ToPlainText() => "DeskPilot: " + Text;
}

public sealed class ThinkingItem : StreamingTextItem
{
    private bool _isShown = true;

    /// <summary>False when the user hid thinking (Settings > Interface); the log keeps the item either way.</summary>
    public bool IsShown
    {
        get => _isShown;
        set => SetProperty(ref _isShown, value);
    }

    private bool _isExpanded;

    /// <summary>Kept on the item so the expanded state survives container recycling in the virtualized log.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public override string ToPlainText() => "[thinking] " + Text;
}

public sealed class ToolStepItem : LogItem
{
    /// <summary>Thumbnails are decoded this wide (pixels) to keep memory low.</summary>
    public const int ThumbnailDecodeWidth = 240;

    private bool _isRunning = true;
    private bool _isError;
    private string _resultText = "";
    private TimeSpan? _duration;
    private byte[]? _imageBytes;
    private Bitmap? _thumbnail;
    private bool _thumbnailFailed;

    public ToolStepItem(string callId, string toolName, string summary)
    {
        CallId = callId;
        ToolName = toolName;
        Summary = string.IsNullOrWhiteSpace(summary) ? toolName : summary;
    }

    public string CallId { get; }
    public string ToolName { get; }
    public string Summary { get; }

    /// <summary>Icon path data (see <see cref="Icons"/>).</summary>
    public string Icon => ToolIcons.For(ToolName);

    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    public bool IsError
    {
        get => _isError;
        private set => SetProperty(ref _isError, value);
    }

    public string ResultText
    {
        get => _resultText;
        private set
        {
            if (!SetProperty(ref _resultText, value)) return;
            OnPropertyChanged(nameof(ResultPreview));
            OnPropertyChanged(nameof(ResultTooltip));
        }
    }

    /// <summary>First few lines of the result (errors are shown inline; more is in the tooltip).</summary>
    public string ResultPreview => Formatting.Preview(ResultText, 3, 320);

    /// <summary>Null when there is no result text, so no empty tooltip pops up.</summary>
    public string? ResultTooltip => string.IsNullOrWhiteSpace(ResultText) ? null : Formatting.Preview(ResultText, 24, 2400);

    public TimeSpan? Duration
    {
        get => _duration;
        private set
        {
            if (SetProperty(ref _duration, value)) OnPropertyChanged(nameof(DurationText));
        }
    }

    public string DurationText => Duration is { } d ? Formatting.Duration(d) : "";

    public bool HasImage => _imageBytes != null;

    /// <summary>The compressed image as received (only kept for the most recent steps).</summary>
    public byte[]? ImageBytes => _imageBytes;

    public int ImageWidth { get; private set; }
    public int ImageHeight { get; private set; }

    /// <summary>Small decoded preview, created on first use (on the UI thread, by the binding).</summary>
    public Bitmap? Thumbnail
    {
        get
        {
            if (_thumbnail != null || _imageBytes == null || _thumbnailFailed) return _thumbnail;
            int decodeWidth = ImageWidth is > 0 and <= ThumbnailDecodeWidth ? 0 : ThumbnailDecodeWidth;
            _thumbnail = DecodeImage(_imageBytes, decodeWidth);
            _thumbnailFailed = _thumbnail == null;
            return _thumbnail;
        }
    }

    public void Complete(ToolResultEvent result, bool keepImage)
    {
        IsError = result.IsError;
        ResultText = result.Text ?? "";
        Duration = result.Duration;
        if (keepImage && result.Image != null) SetImage(result.Image);
        IsRunning = false;
    }

    /// <summary>Marks a step whose result never arrived (e.g. the turn was cut short).</summary>
    public void Abandon()
    {
        if (!IsRunning) return;
        IsRunning = false;
    }

    public void SetImage(ToolImage image)
    {
        try
        {
            _imageBytes = image.GetBytes();
            ImageWidth = image.Width;
            ImageHeight = image.Height;
        }
        catch (FormatException)
        {
            _imageBytes = null;
        }
        _thumbnail = null;
        _thumbnailFailed = false;
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(Thumbnail));
    }

    public void ReleaseImage()
    {
        if (_imageBytes == null && _thumbnail == null) return;
        _imageBytes = null;
        // Not disposed here: a realized container may still draw it this frame; the finalizer frees it.
        _thumbnail = null;
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(Thumbnail));
    }

    /// <summary>Decodes an encoded image (PNG/JPEG). decodeWidth = 0 keeps the full size. Null when it is not an image.</summary>
    public static Bitmap? DecodeImage(byte[] bytes, int decodeWidth)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return decodeWidth > 0
                ? Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.HighQuality)
                : new Bitmap(stream);
        }
        catch (Exception)
        {
            // Not an image, or no rendering platform (plain unit tests): no thumbnail.
            return null;
        }
    }

    public override string ToPlainText()
    {
        var line = $"> {Summary}";
        if (DurationText.Length > 0) line += $" ({DurationText})";
        if (IsError) line += $"  [error] {Formatting.Preview(ResultText, 2, 300)}";
        return line;
    }
}

public sealed class StatusLineItem : LogItem
{
    public StatusLineItem(string message, StatusLevel level)
    {
        Message = message;
        Level = level;
    }

    public string Message { get; }
    public StatusLevel Level { get; }
    public bool IsWarning => Level == StatusLevel.Warning;
    public bool IsError => Level == StatusLevel.Error;

    public string Icon => Level switch
    {
        StatusLevel.Warning => Icons.Warning,
        StatusLevel.Error => Icons.Error,
        _ => Icons.Info,
    };

    public override string ToPlainText() => Level switch
    {
        StatusLevel.Warning => "[warning] " + Message,
        StatusLevel.Error => "[error] " + Message,
        _ => "[info] " + Message,
    };
}

public sealed class TurnFooterItem : LogItem
{
    public TurnFooterItem(TurnResult result)
    {
        Result = result;
        Text = Formatting.TurnSummary(result);
    }

    public TurnResult Result { get; }
    public TurnOutcome Outcome => Result.Outcome;
    public string Text { get; }
    public string? Error => string.IsNullOrWhiteSpace(Result.Error) ? null : Result.Error;
    public bool HasError => Error != null;
    public bool IsProblem => Outcome is TurnOutcome.Failed or TurnOutcome.StepLimit;
    public bool IsCompleted => Outcome == TurnOutcome.Completed;
    public bool IsCancelled => Outcome == TurnOutcome.Cancelled;
    public bool IsStepLimit => Outcome == TurnOutcome.StepLimit;
    public bool IsFailed => Outcome == TurnOutcome.Failed;

    public string Icon => Outcome switch
    {
        TurnOutcome.Cancelled => Icons.Stop,
        TurnOutcome.StepLimit => Icons.Warning,
        TurnOutcome.Failed => Icons.Error,
        _ => Icons.Check,
    };

    public override string ToPlainText() => Error == null ? $"--- {Text}" : $"--- {Text}: {Error}";
}

/// <summary>Icon path data per tool name.</summary>
public static class ToolIcons
{
    public static readonly string Default = Icons.Wrench;

    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["screenshot"] = Icons.Camera,
        ["zoom"] = Icons.Magnify,
        ["click"] = Icons.Cursor,
        ["move_mouse"] = Icons.Move,
        ["drag"] = Icons.Move,
        ["scroll"] = Icons.Scroll,
        ["type_text"] = Icons.Keyboard,
        ["press_keys"] = Icons.Keyboard,
        ["wait"] = Icons.Clock,
        ["list_windows"] = Icons.Windows,
        ["focus_window"] = Icons.Focus,
        ["launch"] = Icons.Launch,
        ["ui_elements"] = Icons.List,
        ["get_clipboard"] = Icons.Clipboard,
        ["set_clipboard"] = Icons.Clipboard,
        ["run_command"] = Icons.Terminal,
        ["vault_search"] = Icons.Magnify,
        ["vault_read"] = Icons.Document,
        ["vault_list"] = Icons.Folder,
        ["vault_append"] = Icons.Pencil,
    };

    public static string For(string? toolName) =>
        toolName != null && Map.TryGetValue(toolName, out var icon) ? icon : Default;
}

public static class Formatting
{
    public static string Duration(TimeSpan d)
    {
        if (d < TimeSpan.FromMilliseconds(50)) return "<0.1s";
        if (d.TotalSeconds < 10) return d.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        if (d.TotalSeconds < 60) return ((int)Math.Round(d.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + "s";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m {d.Seconds:00}s";
        return $"{(int)d.TotalHours}h {d.Minutes:00}m";
    }

    public static string Tokens(int n)
    {
        if (n < 1000) return n.ToString(CultureInfo.InvariantCulture);
        if (n < 1_000_000) return (n / 1000.0).ToString(n < 10_000 ? "0.0" : "0", CultureInfo.InvariantCulture) + "k";
        return (n / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M";
    }

    public static string Cost(double usd) =>
        "$" + usd.ToString(usd < 0.01 ? "0.0000" : usd < 1 ? "0.000" : "0.00", CultureInfo.InvariantCulture);

    public static string Outcome(TurnOutcome outcome) => outcome switch
    {
        TurnOutcome.Completed => "Done",
        TurnOutcome.Cancelled => "Stopped",
        TurnOutcome.Failed => "Failed",
        TurnOutcome.StepLimit => "Step limit reached",
        _ => outcome.ToString(),
    };

    public static string TurnSummary(TurnResult result)
    {
        var s = result.Stats;
        var parts = new List<string> { Outcome(result.Outcome) };
        if (s != null)
        {
            parts.Add(s.Steps == 1 ? "1 step" : $"{s.Steps.ToString(CultureInfo.InvariantCulture)} steps");
            if (s.InputTokens > 0 || s.OutputTokens > 0) parts.Add($"{Tokens(s.InputTokens)} in / {Tokens(s.OutputTokens)} out tokens");
            if (s.CostUsd is { } cost && cost > 0) parts.Add(Cost(cost));
            parts.Add(Duration(s.Duration));
            if (!string.IsNullOrWhiteSpace(s.Model)) parts.Add(s.Model!);
        }
        return string.Join("  ·  ", parts);
    }

    /// <summary>The first <paramref name="maxLines"/> non-empty lines, capped at <paramref name="maxChars"/>.</summary>
    public static string Preview(string? text, int maxLines, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).Take(maxLines + 1).ToList();
        var joined = string.Join("\n", lines.Take(maxLines));
        bool cut = lines.Count > maxLines;
        if (joined.Length > maxChars)
        {
            joined = joined[..maxChars];
            cut = true;
        }
        return cut ? joined + "…" : joined;
    }
}
