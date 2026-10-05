using Clawd.Core.Chat;
using Clawd.Core.Terminal;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace Clawd.UI;

/// <summary>
/// The agent's terminal before pairing: Orca's plain-text screen, recoloured with Claude Code's
/// palette, on a fixed cell grid that matches the size the terminal is fitted to. Typing goes to
/// the terminal through a transparent text box, so IMEs compose Korean properly.
/// </summary>
internal sealed class TerminalScreenView
{
    /// <summary>Cascadia Mono 12: one cell grid shared by the PTY fit and the drawing.</summary>
    public const double FontSize = 12, CellWidth = 7.23, LineHeight = 16, Padding = 12;

    private readonly Grid _root = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly StackPanel _lines = new() { Padding = new Thickness(Padding) };
    private readonly TextBox _input;
    private readonly TextBlock _compositionText = new() { FontFamily = new FontFamily("Cascadia Mono"), FontSize = FontSize };
    private readonly Border _composition;
    private readonly Action<string> _keys;
    private readonly Action<string> _composing;
    private readonly ScrollViewer _scroll;
    private ScreenState? _shown;
    private int? _shownRows;
    private ChatModel? _model;
    private bool _atBottom = true;

    public TerminalScreenView(Action<string> keys, Action<string> composing, Action<(double, double)> resized)
    {
        _keys = keys;
        _composing = composing;
        _input = new TextBox
        {
            Opacity = 0.01,
            AcceptsReturn = false,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            BorderThickness = new Thickness(0),
            Width = 2,
            Height = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(Padding),
        };
        // The Korean IME starts the next syllable's composition before it ends the previous one's
        // (start 글, end 한), so one ending doesn't mean nothing is being composed. Emptying the box
        // then would drop the syllable just started: 한글 would arrive as 한ㅡ. Committed syllables
        // are sent at once and the one still being composed stays in the box until it is done.
        _input.TextCompositionStarted += (_, _) =>
        {
            _compositions++;
            _composingFrom = _input.Text.Length;
        };
        _input.TextCompositionEnded += (_, _) =>
        {
            _compositions = Math.Max(0, _compositions - 1);
            if (_compositions > 0) Send(_composingFrom);
            else EndComposition();
        };
        // TextCompositionChanged only comes for the first syllable in the box; TextChanged for each.
        _input.TextCompositionChanged += (_, _) => ShowComposing();
        _input.TextChanged += (_, _) =>
        {
            if (_compositions == 0) Flush();
            else ShowComposing();
        };
        _input.PreviewKeyDown += KeyDown;
        _scroll = new ScrollViewer { Content = _lines, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _scroll.ViewChanged += (_, _) => _atBottom = _scroll.VerticalOffset + _scroll.ViewportHeight >= _scroll.ExtentHeight - 8;
        _root.Children.Add(_scroll);
        _root.Children.Add(_input);
        // What the IME is composing floats over the screen, so the terminal's size never changes.
        _composition = Ui.Card(_compositionText, 6);
        _composition.HorizontalAlignment = HorizontalAlignment.Left;
        _composition.VerticalAlignment = VerticalAlignment.Bottom;
        _composition.Margin = new Thickness(Padding);
        _composition.Visibility = Visibility.Collapsed;
        _root.Children.Add(_composition);
        _root.Tapped += (_, _) => Focus();
        _root.SizeChanged += (_, e) =>
        {
            resized((e.NewSize.Width, e.NewSize.Height));
            // Not fitted: the text is sized to the view, so it follows the view's size.
            if (_shownRows is null && _model is { } model) { _shown = null; Update(model); }
        };
        _root.Loaded += (_, _) => Focus();
        _root.ActualThemeChanged += (_, _) => { _shown = null; };
    }

    public UIElement Root => _root;

    public (int Cols, int Rows)? Grid => _root.ActualWidth > 100
        ? (Math.Max(40, (int)((_root.ActualWidth - 2 * Padding) / CellWidth)), Math.Max(10, (int)((_root.ActualHeight - 2 * Padding) / LineHeight)))
        : null;

    public void Focus() => _input.Focus(FocusState.Programmatic);

    private int _compositions;   // open IME compositions
    private int _composingFrom;  // where the newest one starts in the box
    private int _sent;           // characters at the start of the box already sent
    private bool _discarded;     // the agent was switched away from: nothing more goes to it

    /// <summary>Another agent was picked: a syllable still being composed is dropped rather than
    /// committed to the agent being left, as on the Mac.</summary>
    public void Discard()
    {
        _discarded = true;
        if (_compositions == 0) return;
        _compositions = 0;
        _composing("");
        var focus = Native.Win32.GetFocus();
        var context = Native.Win32.ImmGetContext(focus);
        if (context == IntPtr.Zero) return;
        Native.Win32.ImmNotifyIME(context, Native.Win32.NI_COMPOSITIONSTR, Native.Win32.CPS_CANCEL, 0);
        Native.Win32.ImmReleaseContext(focus, context);
    }

    /// <summary>Sends the box's text up to <paramref name="end"/> that hasn't been sent yet.</summary>
    private void Send(int end)
    {
        if (_discarded) return;
        end = Math.Min(end, _input.Text.Length);
        if (end <= _sent) return;
        var text = _input.Text[_sent..end];
        _sent = end;
        _keys(text);
    }

    /// <summary>Sends what is left and empties the box; only while nothing is being composed.</summary>
    private void Flush()
    {
        Send(_input.Text.Length);
        _sent = 0;
        if (_input.Text.Length > 0) _input.Text = "";
    }

    private void ShowComposing()
    {
        if (!_discarded) _composing(_input.Text[Math.Min(_sent, _input.Text.Length)..]);
    }

    private void EndComposition()
    {
        _compositions = 0;
        if (!_discarded) _composing("");
        Flush();
    }

    private void KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // The IME only lets a key through once it holds nothing, and may end a syllable on Space or an
        // arrow without saying so: what it composed is committed and goes before the key.
        if (_compositions > 0 && !ChatWindow.IsModifierOrImeKey(e.Key)) EndComposition();
        bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);
        var ctrl = Down(VirtualKey.Control);
        // Ctrl+T and Ctrl+W belong to the chat window.
        if (ctrl && e.Key is VirtualKey.T or VirtualKey.W) return;
        if (ctrl && e.Key == VirtualKey.V)
        {
            e.Handled = true;
            _ = Paste();
            return;
        }
        var mods = (ctrl ? TerminalKeys.Modifiers.Control : 0) | (Down(VirtualKey.Shift) ? TerminalKeys.Modifiers.Shift : 0) | (Down(VirtualKey.Menu) ? TerminalKeys.Modifiers.Alt : 0);
        if (TerminalKeys.Map((int)e.Key, mods) is { } keys)
        {
            e.Handled = true;
            _keys(keys);
        }
    }

    private async Task Paste()
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text)) return;
        var text = await content.GetTextAsync();
        if (text.Length > 0) _keys(TerminalKeys.Paste(text.Replace("\r\n", "\n")));
    }

    public void Update(ChatModel model)
    {
        _model = model;
        _compositionText.Text = model.Composing;
        // Read out as typing, not as terminal output (a Border has no automation peer, the text does).
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_compositionText, string.IsNullOrEmpty(model.Composing) ? "" : L.Format("Terminal_Typing", model.Composing));
        _composition.Visibility = string.IsNullOrEmpty(model.Composing) ? Visibility.Collapsed : Visibility.Visible;
        if (Equals(model.Screen, _shown) && model.FittedRows == _shownRows) return;
        // Not fitted, the screen may be taller than the view: start at the prompt, and follow it only
        // while the reader is there, as the Mac's bottom scroll anchor does.
        var follow = _shown is not ScreenState.Lines || _atBottom;
        _shown = model.Screen;
        _shownRows = model.FittedRows;
        _lines.Children.Clear();
        switch (model.Screen)
        {
            case ScreenState.Lines lines:
                var dark = _root.ActualTheme == ElementTheme.Dark;
                var styled = TerminalStyler.Lines(lines.Rows, model.FittedRows);
                var size = model.FittedRows is null ? UnfittedFontSize(styled) : FontSize;
                foreach (var line in styled) _lines.Children.Add(Line(line, dark, size));
                if (model.FittedRows is null && follow) _scroll.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _scroll.ChangeView(null, _scroll.ScrollableHeight, null, true));
                break;
            case ScreenState.Failed:
                _lines.Children.Add(Ui.Secondary(L.Get("Terminal_Unreadable")));
                break;
            default:
                _lines.Children.Add(Ui.Spinner());
                break;
        }
    }

    /// <summary>Not fitted (before the first resize, or Orca refused it): the font shrinks, down to 9,
    /// to fit the screen as Orca sizes it, as on the Mac.</summary>
    private double UnfittedFontSize(List<TerminalLine> lines)
    {
        if (_root.ActualWidth <= 0 || _root.ActualHeight <= 0) return FontSize;
        var columns = Math.Max(40, lines.Where(l => !l.IsRule).Select(l => TerminalStyler.Cells(l.Text)).DefaultIfEmpty(80).Max());
        var byWidth = (_root.ActualWidth - 2 * Padding) / (columns * CellWidth / FontSize);
        var byHeight = (_root.ActualHeight - 2 * Padding) / (Math.Max(lines.Count, 1) * LineHeight / FontSize);
        return Math.Max(9, Math.Min(FontSize, Math.Min(byWidth, byHeight)));
    }

    private static FrameworkElement Line(TerminalLine line, bool dark, double size)
    {
        var height = size / FontSize * LineHeight;
        if (line.IsRule) return new Grid { Height = height, Children = { new Microsoft.UI.Xaml.Shapes.Rectangle { Style = Ui.Res<Style>("ClawdDivider"), Height = 1, VerticalAlignment = VerticalAlignment.Center } } };
        var text = new TextBlock { FontFamily = Ui.Mono, FontSize = size, Height = height, LineHeight = height, TextWrapping = TextWrapping.NoWrap, IsTextSelectionEnabled = false };
        var offset = 0;
        foreach (var span in line.Spans)
        {
            var run = new Run { Text = span.Text };
            if (Color(span.Role, dark) is { } c) run.Foreground = new SolidColorBrush(c);
            if (span.Role == TerminalRole.Strong) run.FontWeight = FontWeights.SemiBold;
            if (span.Cursor)
            {
                // Block cursor: inverse video on the character under it. A TextBlock takes no
                // InlineUIContainer (adding one throws), but it does take highlighters.
                var cursor = new TextHighlighter
                {
                    Background = new SolidColorBrush(dark ? Colors(0xFF, 0xFF, 0xFF) : Colors(0x1A, 0x1A, 0x1A)),
                    Foreground = new SolidColorBrush(dark ? Colors(0, 0, 0) : Colors(0xFF, 0xFF, 0xFF)),
                };
                cursor.Ranges.Add(new TextRange { StartIndex = offset, Length = span.Text.Length });
                text.TextHighlighters.Add(cursor);
            }
            text.Inlines.Add(run);
            offset += span.Text.Length;
        }
        return text;
    }

    private static Color Colors(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(255, r, g, b);

    /// <summary>Claude Code's own colours for its dark and light themes, as on the Mac; dim text uses
    /// the system's secondary text colour.</summary>
    private static Color? Color(TerminalRole role, bool dark) => role switch
    {
        TerminalRole.Claude => Colors(215, 119, 87),
        TerminalRole.Success => dark ? Colors(78, 186, 101) : Colors(44, 122, 57),
        TerminalRole.Error => dark ? Colors(255, 107, 128) : Colors(171, 43, 63),
        TerminalRole.Permission => dark ? Colors(177, 185, 249) : Colors(87, 105, 247),
        TerminalRole.Plan => dark ? Colors(72, 150, 140) : Colors(0, 102, 102),
        TerminalRole.Dim => dark ? Windows.UI.Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x9E, 0, 0, 0),
        _ => null,
    };
}
