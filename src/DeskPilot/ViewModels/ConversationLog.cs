using System.Collections.ObjectModel;
using System.Text;
using DeskPilot.Core.Abstractions;

namespace DeskPilot.ViewModels;

/// <summary>
/// Turns the session's event stream into log items: merges streaming chunks, pairs tool calls with
/// their results, caps the item count and keeps images only for the most recent steps.
/// Not thread-safe: call it on the UI thread only.
/// </summary>
public sealed class ConversationLog
{
    private readonly Dictionary<string, ToolStepItem> _pendingSteps = new(StringComparer.Ordinal);
    private readonly LinkedList<ToolStepItem> _stepsWithImages = new();
    private UserMessageItem? _awaitingEcho;

    public ConversationLog(int maxItems = 2000, int maxImages = 40)
    {
        MaxItems = Math.Max(10, maxItems);
        MaxImages = Math.Max(0, maxImages);
    }

    public ObservableCollection<LogItem> Items { get; } = new();

    public int MaxItems { get; }
    public int MaxImages { get; }

    /// <summary>Keep screenshot images for thumbnails (off when the UI hides screenshots).</summary>
    public bool KeepImages { get; set; } = true;

    /// <summary>True once a footer was added for the turn started by the latest user message.</summary>
    public bool TurnFooterAdded { get; private set; }

    public int ImageCount => _stepsWithImages.Count;

    /// <summary>Adds the user's message right away when sending; a later identical UserMessageEvent is ignored.</summary>
    public UserMessageItem AddLocalUserMessage(string text)
    {
        CloseStreaming();
        var item = new UserMessageItem(text) { IsLocal = true };
        _awaitingEcho = item;
        TurnFooterAdded = false;
        Add(item);
        return item;
    }

    public StatusLineItem AddStatus(string message, StatusLevel level = StatusLevel.Info)
    {
        CloseStreaming();
        var item = new StatusLineItem(message, level);
        Add(item);
        return item;
    }

    /// <summary>Adds the footer for the current turn unless one was already added (from an event or a result).</summary>
    public TurnFooterItem? AddTurnResult(TurnResult result)
    {
        CloseStreaming();
        AbandonPendingSteps();
        if (TurnFooterAdded) return null;
        TurnFooterAdded = true;
        var footer = new TurnFooterItem(result);
        Add(footer);
        return footer;
    }

    public void Apply(AgentEvent e)
    {
        switch (e)
        {
            case UserMessageEvent u:
                CloseStreaming();
                if (_awaitingEcho != null && string.Equals(_awaitingEcho.Text.Trim(), u.Text?.Trim(), StringComparison.Ordinal))
                {
                    _awaitingEcho = null;
                    return;
                }
                _awaitingEcho = null;
                TurnFooterAdded = false;
                Add(new UserMessageItem(u.Text ?? "") { Timestamp = u.Timestamp });
                break;

            case AssistantTextEvent a:
                AppendStreaming(a.Text, a.IsPartial, a.Timestamp, ts => new AssistantTextItem { Timestamp = ts });
                break;

            case ThinkingEvent t:
                AppendStreaming(t.Text, t.IsPartial, t.Timestamp, ts => new ThinkingItem { Timestamp = ts });
                break;

            case ToolCallEvent call:
            {
                CloseStreaming();
                var step = new ToolStepItem(call.CallId, call.ToolName, call.Summary) { Timestamp = call.Timestamp };
                if (_pendingSteps.TryGetValue(call.CallId, out var stale)) stale.Abandon();
                _pendingSteps[call.CallId] = step;
                Add(step);
                break;
            }

            case ToolResultEvent result:
            {
                CloseStreaming();
                if (!_pendingSteps.Remove(result.CallId, out var step))
                {
                    // The call scrolled out of the capped log, or a backend reported only the result.
                    step = new ToolStepItem(result.CallId, result.ToolName, result.ToolName) { Timestamp = result.Timestamp };
                    Add(step);
                }
                step.Complete(result, KeepImages);
                if (step.HasImage) TrackImage(step);
                break;
            }

            case StatusEvent status:
                AddStatus(status.Message, status.Level);
                break;

            case TurnCompletedEvent done:
                AddTurnResult(done.Result);
                break;
        }
    }

    public void Clear()
    {
        foreach (var step in _stepsWithImages) step.ReleaseImage();
        _stepsWithImages.Clear();
        _pendingSteps.Clear();
        _awaitingEcho = null;
        TurnFooterAdded = false;
        Items.Clear();
    }

    /// <summary>Releases every kept image (used when the user turns screenshots off).</summary>
    public void ReleaseImages()
    {
        foreach (var step in _stepsWithImages) step.ReleaseImage();
        _stepsWithImages.Clear();
    }

    public string ToPlainText()
    {
        var sb = new StringBuilder();
        foreach (var item in Items)
        {
            sb.Append('[').Append(item.TimeText).Append("] ").AppendLine(item.ToPlainText());
        }
        return sb.ToString();
    }

    /// <summary>Marks every open streaming item as complete.</summary>
    public void CloseStreaming()
    {
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i] is not StreamingTextItem s) break;
            s.IsStreaming = false;
        }
    }

    private void AppendStreaming<T>(string? text, bool partial, DateTimeOffset timestamp, Func<DateTimeOffset, T> create) where T : StreamingTextItem
    {
        text ??= "";
        var open = FindOpenStreaming<T>();
        if (partial)
        {
            if (open != null)
            {
                open.Append(text);
                return;
            }
            if (text.Length == 0) return;
            var item = create(timestamp);
            item.Text = text;
            item.IsStreaming = true;
            Add(item);
            return;
        }

        if (open != null)
        {
            open.IsStreaming = false;
            // A complete block after streamed chunks usually repeats them: replace instead of duplicating.
            if (text.Length == 0 || text.StartsWith(open.Text, StringComparison.Ordinal) || Normalize(text) == Normalize(open.Text))
            {
                if (text.Length > 0) open.Text = text;
                return;
            }
        }
        if (string.IsNullOrWhiteSpace(text)) return;
        var block = create(timestamp);
        block.Text = text;
        Add(block);
    }

    private T? FindOpenStreaming<T>() where T : StreamingTextItem
    {
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i] is T match) return match.IsStreaming ? match : null;
            if (Items[i] is not StreamingTextItem) return null;
        }
        return null;
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();

    private void AbandonPendingSteps()
    {
        foreach (var step in _pendingSteps.Values) step.Abandon();
        _pendingSteps.Clear();
    }

    private void TrackImage(ToolStepItem step)
    {
        _stepsWithImages.AddLast(step);
        while (_stepsWithImages.Count > MaxImages && _stepsWithImages.First != null)
        {
            var oldest = _stepsWithImages.First.Value;
            _stepsWithImages.RemoveFirst();
            oldest.ReleaseImage();
        }
    }

    private void Add(LogItem item)
    {
        Items.Add(item);
        while (Items.Count > MaxItems)
        {
            var removed = Items[0];
            Items.RemoveAt(0);
            if (removed is ToolStepItem step)
            {
                if (_pendingSteps.TryGetValue(step.CallId, out var pending) && ReferenceEquals(pending, step)) _pendingSteps.Remove(step.CallId);
                if (_stepsWithImages.Remove(step)) step.ReleaseImage();
            }
            if (ReferenceEquals(removed, _awaitingEcho)) _awaitingEcho = null;
        }
    }
}
