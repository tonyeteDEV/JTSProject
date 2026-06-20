using System.Text.RegularExpressions;
using JTS_App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace JTS_App.Controls;

public sealed partial class ChatMessageBubble : UserControl
{
    private static readonly Regex NumberedListRegex = new(@"^\s*\d+\.\s+", RegexOptions.Compiled);

    public event EventHandler<PendingAgentAction>? ApplyPreviewRequested;
    public event EventHandler<PendingAgentAction>? CancelPreviewRequested;
    public event EventHandler<PendingAgentAction>? EditPreviewRequested;

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message),
        typeof(ChatMessageView),
        typeof(ChatMessageBubble),
        new PropertyMetadata(null, OnMessageChanged));

    public ChatMessageView? Message
    {
        get => (ChatMessageView?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public ChatMessageBubble()
    {
        InitializeComponent();
    }

    private static void OnMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ChatMessageBubble)d).Render();
    }

    private void Render()
    {
        ContentPanel.Children.Clear();
        if (Message is null) return;

        var isAssistant = Message.Author.Equals("Assistant", StringComparison.OrdinalIgnoreCase);
        AccentBar.Visibility = isAssistant ? Visibility.Visible : Visibility.Collapsed;
        BubbleBorder.HorizontalAlignment = isAssistant ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        BubbleBorder.MaxWidth = isAssistant ? double.PositiveInfinity : 980;
        BubbleBorder.Background = Brush(isAssistant ? "#172536" : "#145044");
        BubbleBorder.BorderBrush = Brush(isAssistant ? "#3B82F6" : "#2DD4BF");

        ContentPanel.Children.Add(new TextBlock
        {
            Text = Message.Author,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush("#F5F7FA")
        });

        foreach (var block in ParseBlocks(Message.Content))
        {
            ContentPanel.Children.Add(RenderBlock(block));
        }

        if (Message.Preview is not null)
            ContentPanel.Children.Add(RenderPreview(Message.Preview));
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
                {
                    code.Add(lines[i]);
                    i++;
                }
                if (i < lines.Length) i++;
                yield return new MarkdownBlock(MarkdownBlockType.Code, code);
                continue;
            }

            if (IsTableStart(lines, i))
            {
                var tableLines = new List<string> { lines[i] };
                i += 2;
                while (i < lines.Length && LooksLikeTableLine(lines[i]))
                {
                    tableLines.Add(lines[i]);
                    i++;
                }
                yield return new MarkdownBlock(MarkdownBlockType.Table, tableLines);
                continue;
            }

            if (lines[i].TrimStart().StartsWith(">", StringComparison.Ordinal))
            {
                var quote = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith(">", StringComparison.Ordinal))
                {
                    quote.Add(lines[i].TrimStart().TrimStart('>').TrimStart());
                    i++;
                }
                yield return new MarkdownBlock(MarkdownBlockType.Quote, quote);
                continue;
            }

            if (IsListLine(lines[i]))
            {
                var items = new List<string>();
                while (i < lines.Length && IsListLine(lines[i]))
                {
                    items.Add(CleanListLine(lines[i]));
                    i++;
                }
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
            {
                paragraph.Add(lines[i].Trim());
                i++;
            }

            yield return new MarkdownBlock(MarkdownBlockType.Paragraph, paragraph);
        }
    }

    private UIElement RenderBlock(MarkdownBlock block)
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
        var rich = new RichTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#F5F7FA"),
            IsTextSelectionEnabled = true
        };
        var paragraph = new Paragraph();
        AddInlineRuns(paragraph, text);
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
                Text = "•",
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

    private UIElement RenderPreview(PendingAgentAction action)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = PreviewHeading(action),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush("#FFFFFF")
        });
        panel.Children.Add(new TextBlock
        {
            Text = PreviewTitle(action),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush("#DCE4EF"),
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(action.Description))
        {
            panel.Children.Add(new TextBlock
            {
                Text = action.Description,
                Foreground = Brush("#DCE4EF"),
                TextWrapping = TextWrapping.Wrap
            });
        }
        panel.Children.Add(new TextBlock
        {
            Text = PreviewMeta(action),
            Foreground = Brush("#B8C2D0"),
            TextWrapping = TextWrapping.Wrap
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        buttons.Children.Add(PreviewButton("Accept", "#1F6FEB", (_, _) => ApplyPreviewRequested?.Invoke(this, action)));
        buttons.Children.Add(PreviewButton("Modify", "#30363D", (_, _) => EditPreviewRequested?.Invoke(this, action)));
        buttons.Children.Add(PreviewButton("Cancel", "#30363D", (_, _) => CancelPreviewRequested?.Invoke(this, action)));
        panel.Children.Add(buttons);

        return new Border
        {
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(12),
            Background = Brush("#1F2D3A"),
            BorderBrush = Brush("#5EA8FF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = panel
        };
    }

    private static string PreviewHeading(PendingAgentAction action) => action.Kind switch
    {
        AgentActionKind.CreateTask or AgentActionKind.CreateAndScheduleTask => "Task preview",
        AgentActionKind.UpdateTask => "Update preview",
        AgentActionKind.DeleteTask => "Delete preview",
        AgentActionKind.ScheduleTask or AgentActionKind.UpdateCalendar or AgentActionKind.DeleteCalendar => "Calendar preview",
        AgentActionKind.AddTaskComment or AgentActionKind.DeleteTaskComment => "Comment preview",
        AgentActionKind.AddTimeEntry or AgentActionKind.UpdateTimeEntry or AgentActionKind.DeleteTimeEntry => "Time preview",
        _ => "Preview"
    };

    private static Button PreviewButton(string text, string background, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = text,
            Background = Brush(background),
            Foreground = Brush("#FFFFFF"),
            MinWidth = 86
        };
        button.Click += click;
        return button;
    }

    private static string PreviewTitle(PendingAgentAction action) =>
        action.Kind is AgentActionKind.CreateTask or AgentActionKind.CreateAndScheduleTask
            ? action.Title ?? "New task"
            : action.Task?.Title ?? action.Title ?? "Untitled";

    private static string PreviewMeta(PendingAgentAction action)
    {
        var parts = new List<string>();
        if (action.Project is not null) parts.Add($"Project: {action.Project.Name}");
        if (action.Start is not null) parts.Add($"Start: {action.Start:ddd dd/MM HH:mm}");
        if (action.End is not null) parts.Add($"End: {action.End:ddd dd/MM HH:mm}");
        if (action.DueDate is not null) parts.Add($"Due: {action.DueDate:ddd dd/MM/yyyy}");
        if (action.HasPriority) parts.Add($"Priority: {action.Priority}");
        if (action.HasWorkType) parts.Add($"Type: {action.WorkType}");
        if (action.Status is not null) parts.Add($"Status: {action.Status}");
        if (action.TimeEntryId is not null) parts.Add($"Time entry: {action.TimeEntryId}");
        if (!string.IsNullOrWhiteSpace(action.Comment)) parts.Add($"Comment: {action.Comment}");
        return parts.Count == 0 ? "No additional details." : string.Join(" | ", parts);
    }

    private static UIElement RenderTable(IReadOnlyList<string> lines)
    {
        var rows = lines
            .Select(ParseTableCells)
            .Where(r => r.Count > 0)
            .ToList();
        if (rows.Count == 0) return RenderParagraph(string.Join(Environment.NewLine, lines));

        var columnCount = rows.Max(r => r.Count);
        var grid = new Grid();

        for (var c = 0; c < columnCount; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < columnCount; c++)
            {
                var value = c < rows[r].Count ? rows[r][c] : string.Empty;
                var cell = CreateTableCell(value, r == 0, r);
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
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
            MaxWidth = 260,
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
        var parts = Regex.Split(text, @"(\*\*[^*]+\*\*)");
        foreach (var part in parts.Where(p => p.Length > 0))
        {
            if (part.StartsWith("**", StringComparison.Ordinal) && part.EndsWith("**", StringComparison.Ordinal) && part.Length > 4)
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = part[2..^2],
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
            }
            else
            {
                paragraph.Inlines.Add(new Run { Text = part });
            }
        }
    }

    private static bool IsTableStart(string[] lines, int index)
    {
        return index + 1 < lines.Length &&
               LooksLikeTableLine(lines[index]) &&
               Regex.IsMatch(lines[index + 1], @"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$");
    }

    private static bool LooksLikeTableLine(string line)
    {
        return line.Count(ch => ch == '|') >= 2;
    }

    private static List<string> ParseTableCells(string line)
    {
        return line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();
    }

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
