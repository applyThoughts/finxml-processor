using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace FinXmlProcessor.Desktop.Controls;

/// <summary>
/// Renders a practical subset of Markdown (headings, paragraphs, bullet and numbered lists, fenced code, block
/// quotes, pipe tables, bold and inline code) using plain Avalonia controls, so documentation can be shown in-app
/// without an extra dependency. Links render as their text.
/// </summary>
public sealed partial class MarkdownBlock : ContentControl
{
    public static readonly StyledProperty<string?> MarkdownProperty = AvaloniaProperty.Register<MarkdownBlock, string?>(nameof(Markdown));

    private static readonly FontFamily Mono = new("Menlo,Consolas,monospace");

    static MarkdownBlock()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownBlock>((c, _) => c.Rebuild());
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void Rebuild()
    {
        var panel = new StackPanel { Spacing = 6, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        string[] lines = (Markdown ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int i = 0;
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
            {
                return;
            }

            panel.Children.Add(InlineText(string.Join(' ', paragraph), 13.5, FontWeight.Normal, wrap: true));
            paragraph.Clear();
        }

        while (i < lines.Length)
        {
            string line = lines[i];
            string trimmed = line.TrimEnd();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimEnd().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Add(lines[i]);
                    i++;
                }

                i++; // closing fence
                panel.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10),
                    Child = new TextBlock { Text = string.Join('\n', code), FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.NoWrap },
                });
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                i++;
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                FlushParagraph();
                int level = trimmed.TakeWhile(c => c == '#').Count();
                string text = trimmed[level..].Trim();
                double size = level switch { 1 => 22, 2 => 17, 3 => 14.5, _ => 13.5 };
                TextBlock heading = InlineText(text, size, FontWeight.SemiBold, wrap: true);
                heading.Margin = new Thickness(0, level == 1 ? 4 : 12, 0, 2);
                panel.Children.Add(heading);
                i++;
                continue;
            }

            if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                FlushParagraph();
                var quote = new List<string>();
                while (i < lines.Length && lines[i].TrimEnd().StartsWith("> ", StringComparison.Ordinal))
                {
                    quote.Add(lines[i].TrimEnd()[2..]);
                    i++;
                }

                panel.Children.Add(new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x80, 0x80, 0x80)),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(10, 2, 0, 2),
                    Child = InlineText(string.Join(' ', quote), 13.5, FontWeight.Normal, wrap: true),
                });
                continue;
            }

            if (trimmed.StartsWith('|'))
            {
                FlushParagraph();
                var rows = new List<string[]>();
                while (i < lines.Length && lines[i].TrimEnd().StartsWith('|'))
                {
                    string r = lines[i].Trim().Trim('|');
                    if (!Regex.IsMatch(r, @"^[\s\-:|]+$"))
                    {
                        rows.Add(r.Split('|').Select(c => c.Trim()).ToArray());
                    }

                    i++;
                }

                panel.Children.Add(BuildTable(rows));
                continue;
            }

            Match bullet = BulletPattern().Match(line);
            if (bullet.Success)
            {
                FlushParagraph();
                var list = new StackPanel { Spacing = 3, Margin = new Thickness(6, 0, 0, 0) };
                int number = 0;
                while (i < lines.Length && (bullet = BulletPattern().Match(lines[i])).Success)
                {
                    string marker = bullet.Groups["marker"].Value;
                    string text = bullet.Groups["text"].Value;
                    i++;
                    // Continuation lines are indented and not new bullets.
                    while (i < lines.Length && lines[i].Length > 0 && lines[i][0] == ' ' && !BulletPattern().IsMatch(lines[i]) && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                    {
                        text += " " + lines[i].Trim();
                        i++;
                    }

                    number++;
                    string prefix = marker.EndsWith('.') ? marker : "•";
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("24,*") };
                    var markerBlock = new TextBlock { Text = prefix, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Top };
                    Grid.SetColumn(markerBlock, 0);
                    TextBlock content = InlineText(text, 13.5, FontWeight.Normal, wrap: true);
                    Grid.SetColumn(content, 1);
                    row.Children.Add(markerBlock);
                    row.Children.Add(content);
                    list.Children.Add(row);
                }

                panel.Children.Add(list);
                continue;
            }

            paragraph.Add(trimmed.Trim());
            i++;
        }

        FlushParagraph();
        Content = panel;
    }

    private static Control BuildTable(List<string[]> rows)
    {
        if (rows.Count == 0)
        {
            return new TextBlock();
        }

        int columns = rows.Max(r => r.Length);
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        for (int c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(c == columns - 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto));
        }

        for (int r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < columns; c++)
            {
                string text = c < rows[r].Length ? rows[r][c] : string.Empty;
                var border = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(6, 4),
                    Background = r == 0 ? new SolidColorBrush(Color.FromArgb(0x20, 0x80, 0x80, 0x80)) : null,
                    Child = InlineText(text, 12.5, r == 0 ? FontWeight.SemiBold : FontWeight.Normal, wrap: true),
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }

        return grid;
    }

    /// <summary>Handles **bold**, `code` and [text](url) (rendered as text).</summary>
    private static TextBlock InlineText(string text, double size, FontWeight weight, bool wrap)
    {
        var block = new TextBlock { FontSize = size, FontWeight = weight, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        text = LinkPattern().Replace(text, "$1");
        foreach (Match m in InlinePattern().Matches(text))
        {
            string token = m.Value;
            if (token.StartsWith("**", StringComparison.Ordinal) && token.Length > 4)
            {
                block.Inlines!.Add(new Run(token[2..^2]) { FontWeight = FontWeight.SemiBold });
            }
            else if (token.StartsWith('`') && token.Length > 2)
            {
                block.Inlines!.Add(new Run(token[1..^1]) { FontFamily = Mono, FontSize = size - 1, Background = new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80)) });
            }
            else
            {
                block.Inlines!.Add(new Run(token));
            }
        }

        return block;
    }

    [GeneratedRegex(@"^\s*(?<marker>[-*]|\d+\.)\s+(?<text>.*)$")]
    private static partial Regex BulletPattern();

    [GeneratedRegex(@"\*\*[^*]+\*\*|`[^`]+`|[^*`]+|\*|`")]
    private static partial Regex InlinePattern();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex LinkPattern();
}
