using Clawd.Core;
using Clawd.Core.Chat;
using Clawd.Core.Transcripts;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Clawd.UI;

/// <summary>
/// Fluent styling in one place. The app uses WinUI's own controls and type ramp; the few colours
/// it sets itself (secondary text, cards, the user's bubble, status tints) are styles whose
/// setters use {ThemeResource}, loaded once at startup, so they follow a light/dark switch live
/// exactly like the built-in controls do.
/// </summary>
internal static class Ui
{
    // Status tints: the Windows accent palette's orange, and the system success/critical colours.
    private const string StylesXaml = """
        <ResourceDictionary
            xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <ResourceDictionary.ThemeDictionaries>
            <ResourceDictionary x:Key="Default">
              <SolidColorBrush x:Key="ClawdOrangeBrush" Color="#F7630C" />
            </ResourceDictionary>
            <ResourceDictionary x:Key="Light">
              <SolidColorBrush x:Key="ClawdOrangeBrush" Color="#CA5010" />
            </ResourceDictionary>
            <ResourceDictionary x:Key="HighContrast">
              <SolidColorBrush x:Key="ClawdOrangeBrush" Color="{ThemeResource SystemColorHighlightColor}" />
            </ResourceDictionary>
          </ResourceDictionary.ThemeDictionaries>
          <Style x:Key="ClawdSecondaryText" TargetType="TextBlock">
            <Setter Property="Foreground" Value="{ThemeResource TextFillColorSecondaryBrush}" />
          </Style>
          <Style x:Key="ClawdTertiaryText" TargetType="TextBlock">
            <Setter Property="Foreground" Value="{ThemeResource TextFillColorTertiaryBrush}" />
          </Style>
          <Style x:Key="ClawdOrangeText" TargetType="TextBlock">
            <Setter Property="Foreground" Value="{ThemeResource ClawdOrangeBrush}" />
          </Style>
          <Style x:Key="ClawdCriticalText" TargetType="TextBlock">
            <Setter Property="Foreground" Value="{ThemeResource SystemFillColorCriticalBrush}" />
          </Style>
          <Style x:Key="ClawdOnAccentText" TargetType="TextBlock">
            <Setter Property="Foreground" Value="{ThemeResource TextOnAccentFillColorPrimaryBrush}" />
          </Style>
          <Style x:Key="ClawdSecondaryIcon" TargetType="FontIcon">
            <Setter Property="Foreground" Value="{ThemeResource TextFillColorSecondaryBrush}" />
          </Style>
          <Style x:Key="ClawdOrangeIcon" TargetType="FontIcon">
            <Setter Property="Foreground" Value="{ThemeResource ClawdOrangeBrush}" />
          </Style>
          <Style x:Key="ClawdSuccessIcon" TargetType="FontIcon">
            <Setter Property="Foreground" Value="{ThemeResource SystemFillColorSuccessBrush}" />
          </Style>
          <Style x:Key="ClawdCriticalIcon" TargetType="FontIcon">
            <Setter Property="Foreground" Value="{ThemeResource SystemFillColorCriticalBrush}" />
          </Style>
          <Style x:Key="ClawdCard" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource CardBackgroundFillColorDefaultBrush}" />
            <Setter Property="BorderBrush" Value="{ThemeResource CardStrokeColorDefaultBrush}" />
            <Setter Property="BorderThickness" Value="1" />
            <Setter Property="CornerRadius" Value="8" />
            <Setter Property="Padding" Value="16" />
          </Style>
          <Style x:Key="ClawdLayer" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource LayerFillColorDefaultBrush}" />
            <Setter Property="BorderBrush" Value="{ThemeResource CardStrokeColorDefaultBrush}" />
          </Style>
          <Style x:Key="ClawdBubble" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource AccentFillColorDefaultBrush}" />
            <Setter Property="CornerRadius" Value="16" />
            <Setter Property="Padding" Value="12,8" />
          </Style>
          <Style x:Key="ClawdDivider" TargetType="Rectangle">
            <Setter Property="Fill" Value="{ThemeResource DividerStrokeColorDefaultBrush}" />
          </Style>
        </ResourceDictionary>
        """;

    // Which ramp style each of ours builds on.
    private static readonly (string Key, string BasedOn)[] Bases =
    [
        ("ClawdSecondaryText", "BodyTextBlockStyle"), ("ClawdTertiaryText", "CaptionTextBlockStyle"),
        ("ClawdOrangeText", "BodyTextBlockStyle"), ("ClawdCriticalText", "BodyTextBlockStyle"), ("ClawdOnAccentText", "BodyTextBlockStyle"),
    ];

    /// <summary>Loads the styles into the application's resources; call once, after XamlControlsResources.</summary>
    public static void Install(ResourceDictionary app)
    {
        try
        {
            var styles = (ResourceDictionary)XamlReader.Load(StylesXaml);
            foreach (var (key, basedOn) in Bases)
                if (styles[key] is Style style && app[basedOn] is Style based) style.BasedOn = based;
            app.MergedDictionaries.Add(styles);
        }
        catch (Exception e)
        {
            // Never expected; but a style that fails to parse must not stop Clawd from starting.
            Log.Error($"styles: {e.Message}");
        }
    }

    public static T Res<T>(string key) where T : class =>
        Application.Current.Resources.TryGetValue(key, out var v) && v is T t ? t : throw new KeyNotFoundException(key);

    private static Style? StyleOrNull(string key) => Application.Current.Resources.TryGetValue(key, out var v) ? v as Style : null;

    // MARK: Text

    public static TextBlock Text(string text, string style = "BodyTextBlockStyle", bool wrap = true, int maxLines = 0, bool select = false) => new()
    {
        Text = text,
        Style = StyleOrNull(style),
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap && maxLines == 0 ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        MaxLines = maxLines,
        IsTextSelectionEnabled = select,
    };

    public static TextBlock Secondary(string text, bool wrap = true, int maxLines = 0, bool select = false) => Text(text, "ClawdSecondaryText", wrap, maxLines, select);
    public static TextBlock Caption(string text, int maxLines = 1) => Text(text, "ClawdTertiaryText", wrap: maxLines != 1, maxLines: maxLines);

    public static TextBlock Strong(string text, int maxLines = 0)
    {
        var t = Text(text, "BodyStrongTextBlockStyle", maxLines: maxLines);
        return t;
    }

    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    // MARK: Icons (Segoe Fluent Icons)

    public static class Glyph
    {
        public const string Permission = "", Message = "", Chat = "", Check = "", CheckCircle = "",
            Info = "", Warning = "", Error = "", Pause = "", Play = "", Moon = "",
            Hammer = "", Keyboard = "", Link = "", Cancel = "", Terminal = "",
            Document = "", Pencil = "", Search = "", Globe = "", People = "",
            Checklist = "", Puzzle = "", Wrench = "", Lightbulb = "", OpenIn = "",
            Send = "", Paste = "", Undo = "", Power = "", Connect = "", Copy = "",
            ChevronDown = "", ChevronRight = "", Settings = "", Dot = "", Download = "";
    }

    public static FontIcon Icon(string glyph, string? style = null, double size = 16) => new()
    {
        Glyph = glyph,
        FontSize = size,
        Style = style is null ? null : StyleOrNull(style),
    };

    public static string GlyphFor(SignSymbol s) => s switch
    {
        SignSymbol.Hand => Ui.Glyph.Permission,
        SignSymbol.Bubble => Ui.Glyph.Message,
        SignSymbol.Check => Ui.Glyph.Check,
        SignSymbol.CheckCircle => Ui.Glyph.CheckCircle,
        SignSymbol.Warning => Ui.Glyph.Warning,
        SignSymbol.Pause => Ui.Glyph.Pause,
        SignSymbol.Play => Ui.Glyph.Play,
        SignSymbol.Moon => Ui.Glyph.Moon,
        SignSymbol.Hammer => Ui.Glyph.Hammer,
        SignSymbol.Keyboard => Ui.Glyph.Keyboard,
        SignSymbol.Link => Ui.Glyph.Link,
        SignSymbol.Unlink => Ui.Glyph.Cancel,
        SignSymbol.Error => Ui.Glyph.Error,
        _ => Ui.Glyph.Info,
    };

    public static string GlyphFor(StepSymbol s) => s switch
    {
        StepSymbol.Terminal => Ui.Glyph.Terminal,
        StepSymbol.Document => Ui.Glyph.Document,
        StepSymbol.Pencil => Ui.Glyph.Pencil,
        StepSymbol.Search => Ui.Glyph.Search,
        StepSymbol.Globe => Ui.Glyph.Globe,
        StepSymbol.People => Ui.Glyph.People,
        StepSymbol.Checklist => Ui.Glyph.Checklist,
        StepSymbol.Puzzle => Ui.Glyph.Puzzle,
        StepSymbol.Brain => Ui.Glyph.Lightbulb,
        _ => Ui.Glyph.Wrench,
    };

    // MARK: Containers

    public static Border Card(UIElement child, double padding = 16) => new()
    {
        Style = StyleOrNull("ClawdCard"),
        Child = child,
        Padding = new Thickness(padding),
    };

    /// <summary>Focuses <paramref name="control"/>, now or, when it was only just built, once it is in
    /// the visual tree: before that Focus does nothing and WinUI puts focus on the window's first control.</summary>
    public static void FocusWhenLoaded(Control control)
    {
        if (control.IsLoaded) { control.Focus(FocusState.Programmatic); return; }
        void Loaded(object sender, RoutedEventArgs e)
        {
            control.Loaded -= Loaded;
            control.Focus(FocusState.Programmatic);
        }
        control.Loaded += Loaded;
    }

    public static StackPanel Stack(double spacing, params UIElement[] children) => Stack(Orientation.Vertical, spacing, children);

    public static StackPanel Row(double spacing, params UIElement[] children) => Stack(Orientation.Horizontal, spacing, children);

    private static StackPanel Stack(Orientation o, double spacing, UIElement[] children)
    {
        var panel = new StackPanel { Orientation = o, Spacing = spacing };
        foreach (var c in children) panel.Children.Add(c);
        if (o == Orientation.Horizontal) foreach (var c in children.OfType<FrameworkElement>()) c.VerticalAlignment = VerticalAlignment.Center;
        return panel;
    }

    public static Microsoft.UI.Xaml.Shapes.Rectangle Divider(bool vertical = false) => new()
    {
        Style = StyleOrNull("ClawdDivider"),
        Width = vertical ? 1 : double.NaN,
        Height = vertical ? double.NaN : 1,
    };

    public static Button Button(string label, Action click, bool accent = false, string? tooltip = null)
    {
        var b = new Button { Content = label };
        if (accent) b.Style = StyleOrNull("AccentButtonStyle");
        b.Click += (_, _) => click();
        if (tooltip is not null) ToolTipService.SetToolTip(b, tooltip);
        return b;
    }

    public static Button IconButton(string glyph, string label, Action click)
    {
        var b = new Button { Content = Icon(glyph), Padding = new Thickness(8) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, label);
        ToolTipService.SetToolTip(b, label);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A small indeterminate ring, the WinUI ProgressView.</summary>
    public static ProgressRing Spinner(double size = 16) => new() { IsActive = true, Width = size, Height = size, MinWidth = size, MinHeight = size };

    // MARK: Fixed colours

    /// <summary>Badges on the cards above Clawd: white glyphs on these read in both themes.</summary>
    public static Color Tint(SignTone tone) => tone switch
    {
        SignTone.Urgent => Color.FromArgb(255, 0xCA, 0x50, 0x10),
        SignTone.Done => Color.FromArgb(255, 0x10, 0x7C, 0x10),
        _ => Color.FromArgb(255, 0x6B, 0x6B, 0x6B),
    };

    public static SolidColorBrush Brush(Color c) => new(c);

    public static readonly Color Transparent = Colors.Transparent;
}
