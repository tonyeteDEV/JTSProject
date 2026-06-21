using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace JTS_App.Controls;

public sealed class MarkdownPreviewControl : StackPanel
{
    private static readonly Regex NumberedListRegex = new(@"^\s*\d+\.\s+", RegexOptions.Compiled);

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(MarkdownPreviewControl),
        new PropertyMetadata(string.Empty, OnTextChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public MarkdownPreviewControl()
    {
        Spacing = 10;
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((MarkdownPreviewControl)d).Render();
    }

    private void Render()
    {
        Children.Clear();
        if (string.IsNullOrWhiteSpace(Text))
        {
            Children.Add(new TextBlock
            {
                Text = "No markdown content.",
                Foreground = Brush("#8F9AAA")
            });
            return;
        }

        foreach (var block in ParseBlocks(Text))
            Children.Add(RenderBlock(block));
    }

    private static IEnumerable<MarkdownBlock> ParseBlocks(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                i++;
                continue;
            }

            if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                    code.Add(lines[i++]);
                if (i < lines.Length) i++;
                yield return new MarkdownBlock(MarkdownBlockType.Code, code);
                continue;
            }

            if (IsTableStart(lines, i))
            {
                var tableLines = new List<string> { lines[i] };
                i += 2;
                while (i < lines.Length && LooksLikeTableLine(lines[i]))
                    tableLines.Add(lines[i++]);
                yield return new MarkdownBlock(MarkdownBlockType.Table, tableLines);
                continue;
            }

            if (lines[i].TrimStart().StartsWith(">", StringComparison.Ordinal))
            {
                var quote = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith(">", StringComparison.Ordinal))
                    quote.Add(lines[i++].TrimStart().TrimStart('>').TrimStart());
                yield return new MarkdownBlock(MarkdownBlockType.Quote, quote);
                continue;
            }

            if (IsListLine(lines[i]))
            {
                var items = new List<string>();
                while (i < lines.Length && IsListLine(lines[i]))
                    items.Add(CleanListLine(lines[i++]));
                yield return new MarkdownBlock(MarkdownBlockType.List, items);
                continue;
            }

            var paragraph = new List<string>();
            while (i < lines.Length &&
                   !string.IsNullOrWhiteSpace(lines[i]) &&
                   !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal) &&
                   !IsTableStart(lines, i) &&
                   !IsListLine(lines[i]) &&
                   !lines[i].TrimStart().StartsWith(">", StringComparison.Ordinal))
                paragraph.Add(lines[i++].Trim());

            yield return new MarkdownBlock(MarkdownBlockType.Paragraph, paragraph);
        }
    }

    private static UIElement RenderBlock(MarkdownBlock block)
    {
        return block.Type switch
        {
            MarkdownBlockType.Table => RenderTable(block.Lines),
            MarkdownBlockType.List => RenderList(block.Lines),
            MarkdownBlockType.Code => RenderCode(block.Lines),
            MarkdownBlockType.Quote => RenderQuote(block.Lines),
            _ => RenderParagraph(string.Join(Environment.NewLine, block.Lines))
        };
    }

    private static UIElement RenderParagraph(string text)
    {
        var trimmed = text.Trim();
        var level = HeadingLevel(trimmed);
        if (level > 0)
            trimmed = trimmed[level..].Trim();

        var rich = new RichTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(level > 0 ? "#FFFFFF" : "#DCE4EF"),
            IsTextSelectionEnabled = true,
            FontSize = level switch { 1 => 24, 2 => 20, 3 => 17, _ => 14 },
            FontWeight = level > 0 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Margin = level > 0 ? new Thickness(0, 8, 0, 2) : new Thickness(0)
        };
        var paragraph = new Paragraph();
        AddInlineRuns(paragraph, trimmed);
        rich.Blocks.Add(paragraph);
        return rich;
    }

    private static UIElement RenderList(IReadOnlyList<string> items)
    {
        var panel = new StackPanel { Spacing = 5 };
        foreach (var item in items)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock
            {
                Text = "\u2022",
                Foreground = Brush("#60A5FA"),
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Margin = new Thickness(2, 0, 0, 0)
            });
            var rich = (RichTextBlock)RenderParagraph(item);
            Grid.SetColumn(rich, 1);
            row.Children.Add(rich);
            panel.Children.Add(row);
        }
        return panel;
    }

    private static UIElement RenderCode(IReadOnlyList<string> lines)
    {
        return new Border
        {
            Padding = new Thickness(12),
            Background = Brush("#111318"),
            BorderBrush = Brush("#3A414C"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = new TextBlock
            {
                Text = string.Join(Environment.NewLine, lines),
                FontFamily = new FontFamily("Cascadia Code"),
                FontSize = 12,
                Foreground = Brush("#D7E1EE"),
                TextWrapping = TextWrapping.Wrap
            }
        };
    }

    private static UIElement RenderQuote(IReadOnlyList<string> lines)
    {
        return new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            Background = Brush("#252A32"),
            BorderBrush = Brush("#60A5FA"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(6),
            Child = RenderParagraph(string.Join(Environment.NewLine, lines))
        };
    }

    private static UIElement RenderTable(IReadOnlyList<string> lines)
    {
        var rows = lines.Select(ParseTableCells).Where(row => row.Count > 0).ToList();
        if (rows.Count == 0) return RenderParagraph(string.Join(Environment.NewLine, lines));

        var columnCount = rows.Max(row => row.Count);
        var grid = new Grid();
        for (var column = 0; column < columnCount; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        for (var row = 0; row < rows.Count; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var column = 0; column < columnCount; column++)
            {
                var value = column < rows[row].Count ? rows[row][column] : string.Empty;
                var cell = CreateTableCell(value, row == 0, row);
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
        }

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = new Border
            {
                BorderBrush = Brush("#3A414C"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Child = grid
            }
        };
    }

    private static Border CreateTableCell(string text, bool isHeader, int row)
    {
        var rich = new RichTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            MaxWidth = 320,
            Foreground = Brush(isHeader ? "#FFFFFF" : "#DCE4EF")
        };
        var paragraph = new Paragraph();
        AddInlineRuns(paragraph, text);
        rich.Blocks.Add(paragraph);

        return new Border
        {
            Padding = new Thickness(10, 7, 10, 7),
            MinWidth = 90,
            Background = Brush(isHeader ? "#1F5F9E" : row % 2 == 0 ? "#252A32" : "#22262D"),
            BorderBrush = Brush("#3A414C"),
            BorderThickness = new Thickness(0, 0, 1, 1),
            Child = rich
        };
    }

    private static void AddInlineRuns(Paragraph paragraph, string text)
    {
        var parts = Regex.Split(text, @"(\*\*[^*]+\*\*|`[^`]+`)");
        foreach (var part in parts.Where(part => part.Length > 0))
        {
            if (part.StartsWith("**", StringComparison.Ordinal) && part.EndsWith("**", StringComparison.Ordinal) && part.Length > 4)
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = part[2..^2],
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
            }
            else if (part.StartsWith('`') && part.EndsWith('`') && part.Length > 2)
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = part[1..^1],
                    FontFamily = new FontFamily("Cascadia Code"),
                    Foreground = Brush("#B9E6FF")
                });
            }
            else
            {
                paragraph.Inlines.Add(new Run { Text = part });
            }
        }
    }

    private static int HeadingLevel(string text)
    {
        var level = 0;
        while (level < text.Length && level < 6 && text[level] == '#') level++;
        return level > 0 && level < text.Length && char.IsWhiteSpace(text[level]) ? level : 0;
    }

    private static bool IsTableStart(string[] lines, int index) =>
        index + 1 < lines.Length &&
        LooksLikeTableLine(lines[index]) &&
        Regex.IsMatch(lines[index + 1], @"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$");

    private static bool LooksLikeTableLine(string line) => line.Count(ch => ch == '|') >= 2;

    private static List<string> ParseTableCells(string line) =>
        line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToList();

    private static bool IsListLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("- ", StringComparison.Ordinal) ||
               trimmed.StartsWith("* ", StringComparison.Ordinal) ||
               NumberedListRegex.IsMatch(line);
    }

    private static string CleanListLine(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            return trimmed[2..].Trim();
        return NumberedListRegex.Replace(trimmed, string.Empty).Trim();
    }

    private static SolidColorBrush Brush(string hex)
    {
        return new SolidColorBrush(ColorHelper.FromArgb(
            255,
            Convert.ToByte(hex.Substring(1, 2), 16),
            Convert.ToByte(hex.Substring(3, 2), 16),
            Convert.ToByte(hex.Substring(5, 2), 16)));
    }

    private sealed record MarkdownBlock(MarkdownBlockType Type, IReadOnlyList<string> Lines);

    private enum MarkdownBlockType
    {
        Paragraph,
        Table,
        List,
        Code,
        Quote
    }
}
