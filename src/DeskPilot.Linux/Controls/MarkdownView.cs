using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using DeskPilot.Linux.Services;

namespace DeskPilot.Linux.Controls;

/// <summary>
/// Renders markdown-lite text (see <see cref="MarkdownLite"/>) as selectable text blocks, so assistant replies are
/// formatted and still copyable. Links are shown as text only: nothing in the log opens a browser by itself.
/// </summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    private string? _rendered;

    public MarkdownView()
    {
        Spacing = 0;
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty) Render();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Theme brushes are found once attached; re-render if the first pass ran detached.
        if (Children.Count == 0 && !string.IsNullOrEmpty(Markdown)) Render(force: true);
    }

    private void Render(bool force = false)
    {
        var text = Markdown;
        if (!force && string.Equals(text, _rendered, StringComparison.Ordinal)) return;
        _rendered = text;
        Children.Clear();
        foreach (var control in Build(MarkdownLite.Parse(text))) Children.Add(control);
    }

    /// <summary>The controls for a parsed document (also used by tests).</summary>
    public IReadOnlyList<Control> Build(IReadOnlyList<MdBlock> blocks)
    {
        var codeBg = Brush("Panel2Brush", Color.Parse("#1C202A"));
        var border = Brush("BorderBrush", Color.Parse("#2A2F3B"));
        var accent = Brush("AccentHoverBrush", Color.Parse("#8692FF"));
        var subtle = Brush("SubtleTextBrush", Color.Parse("#A7ADBC"));
        var mono = MonoFont();

        var result = new List<Control>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            bool last = i == blocks.Count - 1;
            switch (block.Kind)
            {
                case MdBlockKind.Code:
                    result.Add(new Border
                    {
                        Background = codeBg,
                        BorderBrush = border,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(10, 8),
                        Margin = new Thickness(0, 4, 0, last ? 0 : 8),
                        Child = new SelectableTextBlock
                        {
                            Text = block.Code ?? "",
                            FontFamily = mono,
                            FontSize = 12.5,
                            TextWrapping = TextWrapping.Wrap,
                        },
                    });
                    break;

                case MdBlockKind.Heading:
                {
                    var tb = NewText(last ? new Thickness(0, 6, 0, 0) : new Thickness(0, 6, 0, 4));
                    tb.FontWeight = FontWeight.SemiBold;
                    tb.FontSize = block.Level switch { 1 => 18, 2 => 16, _ => 14.5 };
                    AddInlines(tb.Inlines!, block.Inlines, codeBg, accent, mono);
                    result.Add(tb);
                    break;
                }

                case MdBlockKind.Bullet:
                case MdBlockKind.Numbered:
                {
                    var marker = block.Kind == MdBlockKind.Bullet ? "•" : block.Marker ?? "1.";
                    var grid = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                        Margin = new Thickness(4 + 16 * block.Level, 1, 0, last ? 0 : 1),
                    };
                    var markerText = new TextBlock { Text = marker, Foreground = subtle, Margin = new Thickness(0, 0, 8, 0), MinWidth = 10 };
                    var body = NewText(default);
                    AddInlines(body.Inlines!, block.Inlines, codeBg, accent, mono);
                    Grid.SetColumn(body, 1);
                    grid.Children.Add(markerText);
                    grid.Children.Add(body);
                    result.Add(grid);
                    break;
                }

                default:
                {
                    var tb = NewText(new Thickness(0, 0, 0, last ? 0 : 8));
                    AddInlines(tb.Inlines!, block.Inlines, codeBg, accent, mono);
                    result.Add(tb);
                    break;
                }
            }
        }
        return result;
    }

    private static SelectableTextBlock NewText(Thickness margin) => new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = margin,
        Inlines = new InlineCollection(),
    };

    private static void AddInlines(InlineCollection target, IReadOnlyList<MdInline> inlines, IBrush codeBg, IBrush accent, FontFamily mono)
    {
        foreach (var inline in inlines)
        {
            switch (inline.Kind)
            {
                case MdInlineKind.Bold:
                    target.Add(new Run(inline.Text) { FontWeight = FontWeight.SemiBold });
                    break;
                case MdInlineKind.Code:
                    target.Add(new Run(inline.Text) { FontFamily = mono, Background = codeBg, FontSize = 12.5 });
                    break;
                case MdInlineKind.Link:
                    target.Add(new Run(inline.Text) { Foreground = accent, TextDecorations = TextDecorations.Underline });
                    if (!string.IsNullOrWhiteSpace(inline.Url) && !string.Equals(inline.Url, inline.Text, StringComparison.Ordinal))
                        target.Add(new Run($" ({inline.Url})") { Foreground = accent, FontSize = 12 });
                    break;
                case MdInlineKind.LineBreak:
                    target.Add(new LineBreak());
                    break;
                default:
                    target.Add(new Run(inline.Text));
                    break;
            }
        }
    }

    private IBrush Brush(string key, Color fallback)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush) return brush;
        if (Application.Current?.TryFindResource(key, ActualThemeVariant, out value) == true && value is IBrush appBrush) return appBrush;
        return new SolidColorBrush(fallback);
    }

    private FontFamily MonoFont()
    {
        if (this.TryFindResource("MonoFont", ActualThemeVariant, out var value) && value is FontFamily f) return f;
        if (Application.Current?.TryFindResource("MonoFont", ActualThemeVariant, out value) == true && value is FontFamily af) return af;
        return new FontFamily("DejaVu Sans Mono, Liberation Mono, Consolas, Courier New");
    }
}
