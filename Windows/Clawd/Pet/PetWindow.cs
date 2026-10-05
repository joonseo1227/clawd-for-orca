using Clawd.Native;

namespace Clawd.Pet;

/// <summary>What the pet's window reports; the app decides what each means.</summary>
internal interface IPetInput
{
    void Clicked();
    void DoubleClicked();
    void ContextMenu(Win32.Point at);
    void DragStarted();
    /// <summary>The window's new top-left corner, physical pixels, while it is being dragged.</summary>
    void Dragged(int x, int y);
    void DragEnded();
    void FilesDropped(IReadOnlyList<string> paths);
}

/// <summary>Clawd's window: draws the sprite and turns mouse input into clicks, drags and drops.</summary>
internal sealed class PetWindow(IPetInput input) : LayeredWindow("ClawdPet", acceptFiles: true)
{
    private Win32.Point? _press;     // where the button went down, screen pixels
    private Win32.Point _grab;       // cursor offset from the window's top-left corner
    private bool _dragging;
    private bool _double;
    private int _clicks;             // presses in the current run of quick clicks
    private long _lastPress;         // Environment.TickCount64 of the last press

    /// <summary>A click that wobbles a little is still a click; only a real move picks Clawd up.</summary>
    private const double DragThreshold = 4;   // DIPs

    public double Scale { get; set; } = 1;

    protected override IntPtr? Message(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_LBUTTONDOWN or Win32.WM_LBUTTONDBLCLK:
            {
                var cursor = Monitors.Cursor;
                var r = Bounds;
                _press = cursor;
                _grab = new Win32.Point { X = cursor.X - r.Left, Y = cursor.Y - r.Top };
                _dragging = false;
                // Windows reports every second press of a quick run as a double-click (2nd, 4th, …);
                // counted here like the Mac's clickCount, so only the 2nd goes to Orca and the rest
                // are ordinary clicks.
                var now = Environment.TickCount64;
                var quick = now - _lastPress <= Win32.GetDoubleClickTime();
                _lastPress = now;
                _clicks = msg == Win32.WM_LBUTTONDBLCLK || (quick && _clicks >= 2) ? _clicks + 1 : 1;
                // The first click of the pair already toggled the chat; the second goes to Orca.
                _double = _clicks == 2;
                Win32.SetCapture(Handle);
                return IntPtr.Zero;
            }
            case Win32.WM_MOUSEMOVE when _press is { } press:
            {
                var cursor = Monitors.Cursor;
                if (!_dragging)
                {
                    var moved = Math.Sqrt(Math.Pow(cursor.X - press.X, 2) + Math.Pow(cursor.Y - press.Y, 2)) / Scale;
                    if (moved < DragThreshold) return IntPtr.Zero;
                    _dragging = true;
                    input.DragStarted();
                }
                input.Dragged(cursor.X - _grab.X, cursor.Y - _grab.Y);
                return IntPtr.Zero;
            }
            case Win32.WM_LBUTTONUP when _press is not null:
            {
                _press = null;
                Win32.ReleaseCapture();
                if (_dragging) input.DragEnded();
                else if (_double) input.DoubleClicked();
                else input.Clicked();
                _dragging = false;
                return IntPtr.Zero;
            }
            case Win32.WM_CAPTURECHANGED when _press is not null && lParam != Handle:
                // Capture taken away mid-press (a system dialog, Alt-Tab): end the drag where it is.
                _press = null;
                if (_dragging) input.DragEnded();
                _dragging = false;
                return IntPtr.Zero;
            case Win32.WM_RBUTTONUP:
                input.ContextMenu(Monitors.Cursor);
                return IntPtr.Zero;
            case Win32.WM_DROPFILES:
                input.FilesDropped(DroppedFiles(wParam));
                return IntPtr.Zero;
            default:
                return base.Message(msg, wParam, lParam);
        }
    }

    private static List<string> DroppedFiles(IntPtr drop)
    {
        var paths = new List<string>();
        try
        {
            var count = Win32.DragQueryFile(drop, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < count; i++)
            {
                var length = Win32.DragQueryFile(drop, i, null, 0);
                var buffer = new char[length + 1];
                Win32.DragQueryFile(drop, i, buffer, length + 1);
                paths.Add(new string(buffer, 0, (int)length));
            }
        }
        finally
        {
            Win32.DragFinish(drop);
        }
        return paths;
    }
}

/// <summary>A cookie's window: drawn from <see cref="Core.Pet.Snack.Render"/>, and draggable.</summary>
internal sealed class SnackWindow : LayeredWindow
{
    private readonly Core.Pet.Snack _snack;
    private readonly Core.Pet.DragTracker _tracker = new();
    private Win32.Point _grab;
    private bool _held;

    public SnackWindow(Core.Pet.Snack snack) : base("ClawdSnack") => _snack = snack;

    /// <summary>Maps the snack between world and screen; set by the app each frame.</summary>
    public ScreenSpace Space { get; set; }
    public int HeightPx { get; set; }

    protected override IntPtr? Message(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_LBUTTONDOWN:
                var cursor = Monitors.Cursor;
                var r = Bounds;
                _grab = new Win32.Point { X = cursor.X - r.Left, Y = cursor.Y - r.Top };
                _held = true;
                _snack.Held = true;
                Win32.SetCapture(Handle);
                return IntPtr.Zero;
            case Win32.WM_MOUSEMOVE when _held:
                var c = Monitors.Cursor;
                _snack.Pos = Space.Origin(c.X - _grab.X, c.Y - _grab.Y, HeightPx);
                _tracker.Add(_snack.Pos);
                return IntPtr.Zero;
            case Win32.WM_LBUTTONUP or Win32.WM_CAPTURECHANGED when _held:
                _held = false;
                if (msg == Win32.WM_LBUTTONUP) Win32.ReleaseCapture();
                _snack.Held = false;
                _snack.Vel = _tracker.Velocity();
                return IntPtr.Zero;
            default:
                return base.Message(msg, wParam, lParam);
        }
    }
}
