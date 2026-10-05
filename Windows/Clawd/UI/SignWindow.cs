using Clawd.Core.Chat;
using Clawd.Native;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Clawd.UI;

/// <summary>
/// The card Clawd holds up above its head: a request waiting, work done, or the hover summary. An
/// acrylic flyout like a Windows notification, with a coloured badge readable from across the room.
/// It never takes focus; clicking it acts, the × closes it.
/// </summary>
internal sealed class SignWindow
{
    private Window? _window;
    private Sign? _current;
    private Action? _onTap;
    private Action? _onClose;
    private (int W, int H) _size;
    private PointInt32 _at = new(int.MinValue, 0);

    /// <summary>Where the card is while it shows, physical pixels.</summary>
    public Win32.Rect? Bounds { get; private set; }

    public void Set(Sign? sign, Action? onTap = null, Action? onClose = null)
    {
        _onTap = onTap;
        _onClose = onClose ?? (() => Set(null));
        if (Equals(sign, _current)) return;
        _current = sign;
        if (sign is null)
        {
            Bounds = null;
            _window?.AppWindow.Hide();
            return;
        }
        var window = _window ??= Create();
        var content = Build(sign);
        window.Content = content;
        // The width the text wants, with room for the window's border and layout rounding: laid out
        // exactly at its measured width, the last word of a line can wrap onto a line the height
        // has no room for. The height is then measured at the card's own width.
        content.Measure(new Windows.Foundation.Size(440, double.PositiveInfinity));
        var width = Math.Clamp(Math.Ceiling(content.DesiredSize.Width) + 4, 220, 440);
        content.Measure(new Windows.Foundation.Size(width - 4, double.PositiveInfinity));
        _size = ((int)width, (int)Math.Ceiling(content.DesiredSize.Height));
        _at = new PointInt32(int.MinValue, 0);   // re-place at the next Follow
    }

    /// <summary>Keeps the card centred above Clawd's head, inside the work area.</summary>
    public void Follow(int centerX, int headTop, Monitors.Monitor monitor)
    {
        if (_current is null || _window is null) return;
        var w = (int)Math.Round(_size.W * monitor.Scale);
        var h = (int)Math.Round(_size.H * monitor.Scale);
        var x = Math.Clamp(centerX - w / 2, monitor.Work.Left, Math.Max(monitor.Work.Left, monitor.Work.Right - w));
        var y = Math.Max(monitor.Work.Top, headTop - h);
        if (_at.X == x && _at.Y == y) return;
        var first = _at.X == int.MinValue;
        _at = new PointInt32(x, y);
        Bounds = new Win32.Rect { Left = x, Top = y, Right = x + w, Bottom = y + h };
        WindowChrome.Place(_window.AppWindow, new RectInt32(x, y, w, h));
        if (first) _window.AppWindow.Show(false);    }

    private static Window Create()
    {
        var w = new Window { SystemBackdrop = new DesktopAcrylicBackdrop(), Title = "Clawd" };
        WindowChrome.MakeFlyout(w);
        WindowChrome.NoActivate(w);
        return w;
    }

    private Grid Build(Sign sign)
    {
        var info = sign.Tone == SignTone.Info;
        var badgeSize = info ? 40.0 : 56.0;
        var badge = new Border
        {
            Width = badgeSize,
            Height = badgeSize,
            CornerRadius = new CornerRadius(badgeSize * 0.26),
            Background = Ui.Brush(Ui.Tint(sign.Tone)),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new FontIcon { Glyph = Ui.GlyphFor(sign.Symbol), FontSize = badgeSize * 0.5, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(badge, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

        var text = new StackPanel { Spacing = 3 };
        // The ramp steps nearest the Mac card's .largeTitle (26 pt) and .title (22 pt).
        text.Children.Add(Ui.Text(sign.Title, info ? "SubtitleTextBlockStyle" : "TitleTextBlockStyle"));
        if (sign.Name is { } name) text.Children.Add(Ui.Text(name, "SubtitleTextBlockStyle"));
        if (sign.Detail is { } detail)
        {
            var d = Ui.Text(detail, "BodyLargeTextBlockStyle", maxLines: 4);
            d.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(d);
        }
        if (sign.Hint is { } hint)
        {
            var h = Ui.Secondary(hint);
            h.Margin = new Thickness(0, 4, 0, 0);
            text.Children.Add(h);
        }

        var close = new Button
        {
            Content = new FontIcon { Glyph = Ui.Glyph.Cancel, FontSize = 10 },
            Padding = new Thickness(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 8, 0),
        };
        ToolTipService.SetToolTip(close, L.Get("Sign_Close"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, L.Get("Sign_Close"));
        close.Click += (_, _) => _onClose?.Invoke();

        var row = new Grid { ColumnSpacing = 14, Padding = new Thickness(18, 16, 36, 16) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(badge);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var root = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        root.Children.Add(row);
        root.Children.Add(close);
        root.Tapped += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside(d, close)) return;
            _onTap?.Invoke();
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(root, string.Join(", ", new[] { sign.Title, sign.Name, sign.Detail, sign.Hint }.Where(s => s is not null)));
        return root;
    }

    private static bool IsInside(DependencyObject d, DependencyObject container)
    {
        for (var p = d; p is not null; p = VisualTreeHelper.GetParent(p)) if (p == container) return true;
        return false;
    }
}
