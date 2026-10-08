using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DeskPilot.Services;

/// <summary>
/// Attached property that renders markdown-lite text into a read-only RichTextBox, so assistant
/// replies are formatted and still selectable/copyable.
/// </summary>
public static class MarkdownView
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(MarkdownView), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    private static readonly FontFamily MonoFont = new("Cascadia Mono, Cascadia Code, Consolas, Courier New");

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RichTextBox box) box.Document = Render(MarkdownLite.Parse(e.NewValue as string), box);
    }

    /// <summary>Builds a FlowDocument. Colors come from the theme resources visible to <paramref name="context"/>.</summary>
    public static FlowDocument Render(IReadOnlyList<MdBlock> blocks, FrameworkElement? context)
    {
        Brush Res(string key, Brush fallback) => context?.TryFindResource(key) as Brush ?? Application.Current?.TryFindResource(key) as Brush ?? fallback;
        var codeBg = Res("Panel2Brush", Brushes.DimGray);
        var border = Res("BorderBrush", Brushes.Gray);
        var accent = Res("AccentBrush", Brushes.SteelBlue);
        var subtle = Res("SubtleTextBrush", Brushes.Gray);

        var doc = new FlowDocument { PagePadding = new Thickness(0) };
        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case MdBlockKind.Code:
                    doc.Blocks.Add(new Paragraph(new Run(block.Code ?? ""))
                    {
                        FontFamily = MonoFont,
                        FontSize = 12.5,
                        Background = codeBg,
                        BorderBrush = border,
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(10, 8, 10, 8),
                        Margin = new Thickness(0, 4, 0, 8),
                    });
                    break;
                case MdBlockKind.Heading:
                {
                    var p = new Paragraph { FontWeight = FontWeights.SemiBold, FontSize = block.Level switch { 1 => 18, 2 => 16, _ => 14.5 }, Margin = new Thickness(0, 6, 0, 4) };
                    AddInlines(p.Inlines, block.Inlines, codeBg, accent);
                    doc.Blocks.Add(p);
                    break;
                }
                case MdBlockKind.Bullet:
                case MdBlockKind.Numbered:
                {
                    double indent = 18 + 16 * block.Level;
                    var p = new Paragraph { Margin = new Thickness(indent, 1, 0, 1), TextIndent = -14 };
                    var marker = block.Kind == MdBlockKind.Bullet ? "•" : block.Marker ?? "1.";
                    p.Inlines.Add(new Run(marker + " ") { Foreground = subtle });
                    AddInlines(p.Inlines, block.Inlines, codeBg, accent);
                    doc.Blocks.Add(p);
                    break;
                }
                default:
                {
                    var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                    AddInlines(p.Inlines, block.Inlines, codeBg, accent);
                    doc.Blocks.Add(p);
                    break;
                }
            }
        }

        // Drop the trailing paragraph gap so the bubble hugs its text.
        if (doc.Blocks.LastBlock is Paragraph last && last.Margin.Bottom > 0)
            last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        return doc;
    }

    private static void AddInlines(InlineCollection target, IReadOnlyList<MdInline> inlines, Brush codeBg, Brush accent)
    {
        foreach (var inline in inlines)
        {
            switch (inline.Kind)
            {
                case MdInlineKind.Bold:
                    target.Add(new Bold(new Run(inline.Text)));
                    break;
                case MdInlineKind.Code:
                    target.Add(new Run(inline.Text) { FontFamily = MonoFont, Background = codeBg });
                    break;
                case MdInlineKind.Link:
                    // Shown as text only: clicking links in the log must never open a browser by itself.
                    target.Add(new Run(inline.Text) { Foreground = accent, ToolTip = inline.Url });
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
}
