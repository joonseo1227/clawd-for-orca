using Clawd.Core.Chat;
using Clawd.Core.Markdown;
using Clawd.Core.Orca;
using Clawd.Core.Transcripts;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Windows.UI.Text;

namespace Clawd.UI;

/// <summary>
/// The selected agent's current turn, step by step: the prompt, what Claude says, and the tool
/// calls and thinking in between, runs of steps folded into one expander. Rows are kept by id
/// and reused while unchanged, so expanded steps and text selections survive updates; the view
/// follows new steps only while the reader is at the bottom.
/// </summary>
internal sealed class TimelineView
{
    private readonly string _agent;
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _panel = new() { Spacing = 10, Padding = new Thickness(20) };
    private readonly Dictionary<string, (object Key, UIElement Element)> _rows = [];
    private readonly HashSet<string> _expanded = [];
    private bool _atBottom = true;
    private bool _focusAnswer;   // the dialog's first answer is to take the focus once it is loaded

    public TimelineView(string agent)
    {
        _agent = agent;
        _scroll = new ScrollViewer { Content = _panel };
        _scroll.ViewChanged += (_, _) => _atBottom = _scroll.VerticalOffset + _scroll.ViewportHeight >= _scroll.ExtentHeight - 40;
    }

    public UIElement Root => _scroll;

    /// <summary>The permission dialog's first answer, when one is showing.</summary>
    public Button? FirstAnswer { get; private set; }

    public void Update(ChatModel model, AppController app)
    {
        if (model.Current is not { } row || row.Id != _agent) return;
        // The dialog is built anew on every update; its focused answer hands focus to the new one.
        var answerFocused = FirstAnswer is { FocusState: not FocusState.Unfocused };
        var hadAnswers = FirstAnswer is not null;
        FirstAnswer = null;
        var children = new List<UIElement>();
        if (model.Timeline.Count == 0 && !model.TimelineReady)
        {
            // A fraction of a second while the transcript is read; the summary first would flash a different layout.
        }
        else if (model.Timeline.Count == 0)
        {
            Summary(row, model, app, children);
        }
        else
        {
            var live = row.Kind == RowKind.Working;
            var entries = TimelineEntry.Build(model.Timeline, live);
            for (var i = 0; i < entries.Count; i++) children.Add(Row(entries[i], live && i == entries.Count - 1));
            // The permission dialog, or a working indicator between steps.
            var runningStep = model.Timeline[^1].IsRunningStep;
            if (row.Kind == RowKind.Permission) children.Add(PermissionRequest(row, model, app));
            else if (live && !runningStep) children.Add(Ui.Row(8, Ui.Spinner(), Ui.Secondary(L.Get("Timeline_Working"))));
        }
        var same = children.Count == _panel.Children.Count && children.Select((c, i) => ReferenceEquals(c, _panel.Children[i])).All(x => x);
        if (same) return;
        var follow = _atBottom;
        _panel.Children.Clear();
        foreach (var c in children) _panel.Children.Add(c);
        // A dialog that just appeared takes the focus too, as on the Mac nothing else takes Enter. It
        // can come with the transcript or the agent's new state rather than with the prompt read off
        // the screen, which the chat window acts on, and be rebuilt before its buttons have loaded:
        // the request stands until an answer has the focus.
        if (FirstAnswer is null) _focusAnswer = false;
        else if (answerFocused || !hadAnswers) _focusAnswer = true;
        if (_focusAnswer && FirstAnswer is { } answer)
        {
            answer.GotFocus += (_, _) => _focusAnswer = false;
            Ui.FocusWhenLoaded(answer);
        }
        // Scrolled up to read something: leave the view where it is.
        if (follow) _scroll.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _scroll.ChangeView(null, _scroll.ScrollableHeight, null, true));
    }

    /// <summary>Reuses the element built for an unchanged entry.</summary>
    private UIElement Cached(string id, object key, Func<UIElement> build)
    {
        if (_rows.TryGetValue(id, out var hit) && Equals(hit.Key, key)) return hit.Element;
        var element = build();
        _rows[id] = (key, element);
        return element;
    }

    private UIElement Row(TimelineEntry entry, bool isLast) => entry switch
    {
        TimelineEntry.Single { Item: var item } => item.Kind switch
        {
            TimelineKind.User => Cached(item.Id, item, () => UserBubble(item.Text)),
            TimelineKind.Text => Cached(item.Id, item, () => Markdown(item.Text)),
            _ => Cached(item.Id, (item, isLast), () => Step(item, isLast)),
        },
        TimelineEntry.Steps steps => Cached(steps.Id, (steps, isLast), () => StepGroup(steps, isLast)),
        _ => new Grid(),
    };

    /// <summary>The user's own message, on the right in the accent colour.</summary>
    public static UIElement UserBubble(string text)
    {
        var block = new RichTextBlock { IsTextSelectionEnabled = true, Style = null };
        var p = new Paragraph();
        AddInlines(p.Inlines, text);
        block.Blocks.Add(p);
        block.Foreground = Ui.Res<Microsoft.UI.Xaml.Media.Brush>("TextOnAccentFillColorPrimaryBrush");
        return new Border
        {
            Style = Ui.Res<Style>("ClawdBubble"),
            Child = block,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(80, 4, 0, 4),
        };
    }

    // MARK: Steps

    private static UIElement StepLabel(TimelineItem item)
    {
        if (item.Kind == TimelineKind.Thinking)
            return Ui.Row(8, Ui.Icon(Ui.Glyph.Lightbulb, "ClawdSecondaryIcon", 14), Ui.Secondary(StepText.ThinkingLabel(item), wrap: false));
        var failed = item.Failed && item.Result is not null;
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, MaxWidth = 520 };
        text.Inlines.Add(new Run { Text = item.ToolName, FontWeight = FontWeights.Bold });
        text.Inlines.Add(new Run { Text = "  " + item.Text, Foreground = Ui.Res<Microsoft.UI.Xaml.Media.Brush>("TextFillColorSecondaryBrush") });
        return Ui.Row(8, Ui.Icon(Ui.GlyphFor(StepText.Symbol(item.ToolName ?? "")), failed ? "ClawdCriticalIcon" : "ClawdSecondaryIcon", 14), text);
    }

    /// <summary>One tool call (expandable to its output) or one stretch of thinking.</summary>
    private UIElement Step(TimelineItem item, bool running)
    {
        if (item.Kind == TimelineKind.Thinking && item.Text.Length == 0)
            return running ? Ui.Row(8, StepLabel(item), Ui.Spinner(14)) : StepLabel(item);
        if (item.Kind == TimelineKind.Thinking)
            return Expander(item.Id, StepLabel(item), Ui.Secondary(item.Text, select: true));
        var output = Ui.Text(string.IsNullOrEmpty(item.Result) ? L.Get("Timeline_NoOutput") : item.Result, item.Failed ? "ClawdCriticalText" : "ClawdSecondaryText", select: true);
        output.FontFamily = Ui.Mono;
        output.FontSize = 12;
        var header = item.Result is null ? Ui.Row(8, StepLabel(item), Ui.Spinner(14)) : StepLabel(item);
        var expander = Expander(item.Id, header, output);
        expander.IsEnabled = item.Result is not null;   // still running: nothing to show yet
        return expander;
    }

    private UIElement StepGroup(TimelineEntry.Steps steps, bool live)
    {
        UIElement header;
        if (live && steps.Items.Count > 0)
        {
            // Working: the step in progress, with how many came before it.
            header = Ui.Row(8, StepLabel(steps.Items[^1]), Ui.Caption($"+{steps.Items.Count - 1}"), Ui.Spinner(14));
        }
        else
        {
            var (title, detail, failed) = StepText.Summary(steps.Items);
            var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            text.Inlines.Add(new Run { Text = title, FontWeight = FontWeights.Bold });
            text.Inlines.Add(new Run { Text = "  " + detail, Foreground = Ui.Res<Microsoft.UI.Xaml.Media.Brush>("TextFillColorSecondaryBrush") });
            header = Ui.Row(8, Ui.Icon(failed ? Ui.Glyph.Error : Ui.Glyph.Checklist, failed ? "ClawdCriticalIcon" : "ClawdSecondaryIcon", 14), text);
        }
        var content = new StackPanel { Spacing = 8 };
        for (var i = 0; i < steps.Items.Count; i++) content.Children.Add(Step(steps.Items[i], live && i == steps.Items.Count - 1));
        return Expander(steps.Id, header, content);
    }

    private Expander Expander(string id, UIElement header, UIElement content)
    {
        var e = new Expander
        {
            Header = header,
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            IsExpanded = _expanded.Contains(id),
            // WinUI's default 48 DIP header is a settings-page row; at that height a few folded step
            // groups push the prompt out of view, where the Mac's disclosure rows are about 22. 32 is
            // the header's own chevron button, so the header stays an easy click target.
            MinHeight = 32,
        };
        e.Expanding += (_, _) => _expanded.Add(id);
        e.Collapsed += (_, _) => _expanded.Remove(id);
        return e;
    }

    // MARK: Without a transcript

    /// <summary>For agents without a transcript (e.g. Codex): the last prompt and final reply Orca reports.</summary>
    private void Summary(ChatRow row, ChatModel model, AppController app, List<UIElement> children)
    {
        if (!string.IsNullOrEmpty(row.Agent.Prompt)) children.Add(UserBubble(row.Agent.Prompt));
        if (row.Kind == RowKind.Permission) children.Add(PermissionRequest(row, model, app));
        else if (!string.IsNullOrEmpty(row.Agent.LastMessage) && row.Kind != RowKind.Working) children.Add(Markdown(row.Agent.LastMessage));
        else if (row.Kind == RowKind.Working || model.Busy)
            children.Add(Ui.Row(8, Ui.Spinner(), Ui.Secondary(row.Agent.Tool is { } t ? L.Format("Timeline_Running", t) : L.Get("Timeline_Working"))));
    }

    // MARK: Permission

    /// <summary>The permission dialog with its answers when it could be read off the screen; otherwise
    /// what Orca says the agent wants to run, answered in Orca.</summary>
    private UIElement PermissionRequest(ChatRow row, ChatModel model, AppController app)
    {
        var body = new StackPanel { Spacing = 10 };
        var title = model.Prompt?.Title ?? L.Get("Timeline_PermissionRequest");
        var heading = Ui.Row(8, Ui.Icon(Ui.Glyph.Permission, "ClawdOrangeIcon", 16), Ui.Text(title, "ClawdOrangeText"));
        body.Children.Add(heading);
        if (model.Prompt is { } p)
        {
            // The command (or file) reads as code, with what it is for under it.
            if (p.Command is { } command)
            {
                var c = Ui.Text(command, maxLines: 6, select: true);
                c.FontFamily = Ui.Mono;
                body.Children.Add(c);
            }
            foreach (var line in p.Explanation) body.Children.Add(Ui.Secondary(line, select: true));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var o in p.Options)
            {
                var b = Ui.Button(o.Title, () => app.ChatAnswer(o.Number, row.Agent), accent: o.Number == 1,
                    tooltip: o.Shortcut is null ? o.Label : $"{o.Label}  Ctrl+{o.Number}");
                b.IsEnabled = !model.Busy;
                FirstAnswer ??= b;
                buttons.Children.Add(b);
            }
            body.Children.Add(buttons);
        }
        else
        {
            if (row.Agent.Tool is { } tool) body.Children.Add(Ui.Secondary(tool));
            if (!string.IsNullOrEmpty(row.Agent.ToolInput))
            {
                var input = Ui.Text(row.Agent.ToolInput, maxLines: 8, select: true);
                input.FontFamily = Ui.Mono;
                body.Children.Add(input);
            }
            var answer = Ui.Row(8, Ui.Button(L.Get("Timeline_AnswerInOrca"), () => app.OpenFromChat(row.Agent), accent: true));
            if (model.Busy) answer.Children.Add(Ui.Spinner());
            body.Children.Add(answer);
        }
        return Ui.Card(body);
    }

    // MARK: Markdown

    public static UIElement Markdown(string source)
    {
        var panel = new StackPanel { Spacing = 10 };
        foreach (var block in MarkdownBlock.Parse(source)) panel.Children.Add(Block(block));
        return panel;
    }

    private static RichTextBlock Rich(string text, string style = "BodyTextBlockStyle")
    {
        var block = new RichTextBlock { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        if (style != "BodyTextBlockStyle" && Application.Current.Resources.TryGetValue(style, out var s) && s is Style textStyle)
        {
            // Headings: take size and weight from the type ramp style.
            foreach (var setter in textStyle.Setters.OfType<Setter>())
            {
                if (setter.Property == TextBlock.FontSizeProperty && setter.Value is double size) block.FontSize = size;
                if (setter.Property == TextBlock.FontWeightProperty && setter.Value is FontWeight weight) block.FontWeight = weight;
            }
        }
        var p = new Paragraph();
        AddInlines(p.Inlines, text);
        block.Blocks.Add(p);
        return block;
    }

    private static UIElement Block(MarkdownBlock block)
    {
        switch (block)
        {
            case MarkdownBlock.Heading h:
                var heading = Rich(h.Text, h.Level <= 1 ? "SubtitleTextBlockStyle" : h.Level == 2 ? "BodyLargeTextBlockStyle" : "BodyStrongTextBlockStyle");
                heading.FontWeight = FontWeights.Bold;
                heading.Margin = new Thickness(0, 4, 0, 0);
                return heading;
            case MarkdownBlock.Paragraph p:
                return Rich(p.Text);
            case MarkdownBlock.ListItem item:
                var grid = new Grid { ColumnSpacing = 6, Margin = new Thickness(item.Indent * 18, 0, 0, 0) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                var marker = Ui.Secondary(item.Marker, wrap: false);
                marker.HorizontalAlignment = HorizontalAlignment.Right;
                grid.Children.Add(marker);
                var text = Rich(item.Text);
                Grid.SetColumn(text, 1);
                grid.Children.Add(text);
                return grid;
            case MarkdownBlock.Code code:
                var codeText = Ui.Text(code.Text, wrap: false, select: true);
                codeText.FontFamily = Ui.Mono;
                codeText.FontSize = 12.5;
                return Ui.Card(new ScrollViewer
                {
                    Content = codeText,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    HorizontalScrollMode = ScrollMode.Enabled,
                }, 12);
            case MarkdownBlock.Quote q:
                var quote = new Grid { ColumnSpacing = 10 };
                quote.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                quote.ColumnDefinitions.Add(new ColumnDefinition());
                quote.Children.Add(Ui.Divider(vertical: true));
                var qt = Rich(q.Text);
                qt.Foreground = Ui.Res<Microsoft.UI.Xaml.Media.Brush>("TextFillColorSecondaryBrush");
                Grid.SetColumn(qt, 1);
                quote.Children.Add(qt);
                return quote;
            case MarkdownBlock.Table t:
                var table = new Grid { ColumnSpacing = 18, RowSpacing = 8 };
                var columns = Math.Max(t.Header.Count, t.Rows.Count == 0 ? 0 : t.Rows.Max(r => r.Count));
                for (var c = 0; c < columns; c++) table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var all = new List<IReadOnlyList<string>> { t.Header };
                all.AddRange(t.Rows);
                for (var r = 0; r < all.Count; r++)
                {
                    // Grid row 1 is the line under the header, as on the Mac; data rows follow it.
                    var gridRow = r == 0 ? 0 : r + 1;
                    table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    if (r == 0)
                    {
                        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        var line = Ui.Divider();
                        Grid.SetRow(line, 1);
                        Grid.SetColumnSpan(line, Math.Max(1, columns));
                        table.Children.Add(line);
                    }
                    for (var c = 0; c < all[r].Count; c++)
                    {
                        var cell = Rich(all[r][c]);
                        if (r == 0) cell.FontWeight = FontWeights.Bold;
                        Grid.SetRow(cell, gridRow);
                        Grid.SetColumn(cell, c);
                        table.Children.Add(cell);
                    }
                }
                return Ui.Card(new ScrollViewer
                {
                    Content = table,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    HorizontalScrollMode = ScrollMode.Enabled,
                }, 12);
            default:
                return Ui.Divider();
        }
    }

    /// <summary>Inline Markdown as runs: bold, italic, struck through, code in the monospaced font,
    /// links that open in the browser (web and mail only). Bold rather than semibold, as on the Mac,
    /// here and for the step names and table headers: with a Korean display language Latin text
    /// falls back to a font whose semibold looks almost regular.</summary>
    public static void AddInlines(InlineCollection inlines, string text)
    {
        foreach (var span in InlineMarkdown.Parse(text))
        {
            var run = new Run { Text = span.Text };
            if (span.Bold) run.FontWeight = FontWeights.Bold;
            if (span.Italic) run.FontStyle = Windows.UI.Text.FontStyle.Italic;
            if (span.Code) run.FontFamily = Ui.Mono;
            if (span.Strike) run.TextDecorations = TextDecorations.Strikethrough;
            if (span.Link is { } link && Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
            {
                var h = new Hyperlink { NavigateUri = uri };
                h.Inlines.Add(run);
                inlines.Add(h);
            }
            else
            {
                inlines.Add(run);
            }
        }
    }
}
