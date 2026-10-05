using System.Text;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;

namespace Clawd.Terminal;

/// <summary>
/// The live terminal: xterm.js in a WebView2, fed with Orca's terminal stream. Colours, the
/// cursor, wide characters, IME input, selection and mouse reporting (the wheel scrolls Claude
/// Code itself once it enables mouse mode) are xterm.js's. Keys typed into it come out of
/// <see cref="Input"/>; the grid that fits the view comes out of <see cref="Resized"/>.
/// <para>
/// The page is Assets\Terminal\terminal.html, served from the app folder under a virtual host;
/// nothing is fetched from the network and the page can't navigate anywhere else. Calls made
/// before the page is up are queued, so the view can be fed before it's ever shown. UI thread only.
/// </para>
/// </summary>
public sealed partial class LiveTerminalView : UserControl
{
    private const string Host = "clawd-terminal.local";
    private static readonly string Origin = $"https://{Host}/";

    private readonly WebView2 _web = new();
    private readonly List<string> _outbox = [];   // screen messages waiting for the page
    private readonly StringBuilder _feed = new();   // output coalesced until the next dispatcher turn
    private bool _feedQueued;
    private bool _focusWanted;
    private bool _initStarted;
    private bool _ready;
    private const double TerminalFontSize = 13;   // xterm.js's, in CSS pixels

    public LiveTerminalView()
    {
        // Default-coloured cells are transparent, and so is the page, so the flyout's Mica shows through.
        _web.DefaultBackgroundColor = Colors.Transparent;
        Background = null;
        Content = _web;
        IsTabStop = false;
        Loaded += (_, _) => _ = InitializeAsync();
        ActualThemeChanged += (_, _) => SendTheme();
    }

    /// <summary>Keys typed into the terminal, as the bytes to send to the agent's PTY.</summary>
    public event Action<string>? Input;

    /// <summary>The grid (cols, rows) that fits the view, after every change.</summary>
    public event Action<int, int>? Resized;

    /// <summary>The page was reloaded after its renderer crashed; the screen is empty until the
    /// next snapshot, so resubscribe.</summary>
    public event Action? Reloaded;

    /// <summary>Ctrl+T or Ctrl+W pressed in the terminal: "t" or "w", for the chat window.</summary>
    public event Action<string>? Shortcut;

    /// <summary>The fitted grid; (0, 0) until the page has laid out.</summary>
    public (int Cols, int Rows) GridSize { get; private set; }

    /// <summary>Why the WebView2 couldn't start (no WebView2 runtime, for one), or null.</summary>
    public string? InitError { get; private set; }

    /// <summary>The WebView2 couldn't start: the view stays empty and takes no input.</summary>
    public event Action<string>? InitFailed;

    /// <summary>A fresh screen from Orca: reset the emulator and replay the serialized buffer.</summary>
    public Task Load(string snapshot)
    {
        DropScreen();
        Send(new { t = "load", d = snapshot });
        return Task.CompletedTask;
    }

    /// <summary>Appends output. Chunks are coalesced into one message per dispatcher turn and
    /// written by the page once per animation frame.</summary>
    public Task Feed(string data)
    {
        if (data.Length == 0) return Task.CompletedTask;
        _feed.Append(data);
        if (!_feedQueued)
        {
            _feedQueued = true;
            if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FlushFeed)) FlushFeed();
        }
        return Task.CompletedTask;
    }

    /// <summary>Empties the screen, so a terminal that fails to connect never shows the previous one.</summary>
    public Task Clear()
    {
        DropScreen();
        Send(new { t = "clear" });
        return Task.CompletedTask;
    }

    public void FocusTerminal()
    {
        _web.Focus(FocusState.Programmatic);
        if (_ready) Post(new { t = "focus" });
        else _focusWanted = true;
    }

    /// <summary>Releases the browser process. The view can't be used afterwards.</summary>
    public void Close()
    {
        _ready = false;
        _web.Close();
    }

    // MARK: Plumbing

    private void FlushFeed()
    {
        _feedQueued = false;
        if (_feed.Length == 0) return;
        var data = _feed.ToString();
        _feed.Clear();
        Send(new { t = "feed", d = data });
    }

    /// <summary>Anything not yet shown belonged to the screen being replaced.</summary>
    private void DropScreen()
    {
        _feed.Clear();
        _outbox.Clear();
    }

    private void Send(object message)
    {
        if (_ready) Post(message);
        else _outbox.Add(JsonSerializer.Serialize(message));
    }

    private void Post(object message)
    {
        try { _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message)); }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    private void SendTheme()
    {
        if (_ready) Post(new { t = "theme", dark = ActualTheme == ElementTheme.Dark });
    }

    private async Task InitializeAsync()
    {
        if (_initStarted) return;
        _initStarted = true;
        try
        {
            // The profile lives with Clawd's other files, not next to Clawd.exe (WebView2's default),
            // which may be read-only and wouldn't go away with %LOCALAPPDATA%\Clawd.
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null,
                Path.Combine(Clawd.Core.AppPaths.DataDirectory, "WebView2"), new CoreWebView2EnvironmentOptions());
            await _web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception e)
        {
            Clawd.Core.Log.Error($"WebView2: {e.Message}");
            // A COM error's message repeats its text on further lines; the first says it.
            InitError = e.Message.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? e.GetType().Name;
            InitFailed?.Invoke(InitError);
            return;
        }
        var core = _web.CoreWebView2;
        var folder = Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal");
        core.SetVirtualHostNameToFolderMapping(Host, folder, CoreWebView2HostResourceAccessKind.Deny);

        var settings = core.Settings;
        settings.IsWebMessageEnabled = true;
        settings.IsScriptEnabled = true;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;   // no F5 reload, Ctrl+P print, Ctrl+F find
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
#if DEBUG
        settings.AreDevToolsEnabled = true;
#else
        settings.AreDevToolsEnabled = false;
#endif

        // The page never leaves the bundled terminal; links go out through OpenLink.
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenLink(e.Uri);
        };
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.WebMessageReceived += (_, e) =>
        {
            if (!e.Source.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) return;
            Receive(e.WebMessageAsJson);
        };
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
                or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                or CoreWebView2ProcessFailedKind.FrameRenderProcessExited)
            {
                _ready = false;
                DropScreen();
                try { core.Reload(); } catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { return; }
                Reloaded?.Invoke();
            }
        };
        _web.Source = new Uri(Origin + "terminal.html");
    }

    private void Receive(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("t", out var t)) return;
            switch (t.GetString())
            {
                case "ready":
                    _ready = true;
                    SendTheme();
                    Post(new { t = "font", size = TerminalFontSize });
                    foreach (var message in _outbox) _web.CoreWebView2.PostWebMessageAsJson(message);
                    _outbox.Clear();
                    if (_focusWanted) { _focusWanted = false; Post(new { t = "focus" }); }
                    break;
                case "input" when m.TryGetProperty("d", out var d) && d.GetString() is { Length: > 0 } keys:
                    Input?.Invoke(keys);
                    break;
                case "resize" when m.TryGetProperty("cols", out var c) && m.TryGetProperty("rows", out var r)
                                   && c.TryGetInt32(out var cols) && r.TryGetInt32(out var rows) && cols > 0 && rows > 0:
                    GridSize = (cols, rows);
                    Resized?.Invoke(cols, rows);
                    break;
                case "shortcut" when m.TryGetProperty("k", out var k) && k.GetString() is { } key:
                    Shortcut?.Invoke(key);
                    break;
                case "link" when m.TryGetProperty("url", out var u) && u.GetString() is { } url:
                    OpenLink(url);
                    break;
                case "copy" when m.TryGetProperty("d", out var s) && s.GetString() is { Length: > 0 } text:
                    var package = new DataPackage();
                    package.SetText(text);
                    Clipboard.SetContent(package);
                    break;
            }
        }
        catch (JsonException) { }
    }

    /// <summary>Links come from terminal output, so only web and mail links open; file:, custom
    /// app schemes and the like could launch things the user never meant to.</summary>
    private static void OpenLink(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is not ("http" or "https" or "mailto")) return;
        _ = Windows.System.Launcher.LaunchUriAsync(uri);
    }
}
