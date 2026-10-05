using Clawd.Native;

namespace Clawd.Services;

/// <summary>
/// A Win32 popup menu built from a description, for the notification-area icon and right clicks
/// on Clawd: the same native menus Explorer uses, in the system's light or dark theme.
/// </summary>
internal sealed class NativeMenu
{
    private abstract record Entry;
    private sealed record Item(string Title, Action? Action, string? Shortcut, bool Checked) : Entry;
    private sealed record Header(string Title) : Entry;
    private sealed record Separator : Entry;
    private sealed record Sub(string Title, NativeMenu Menu) : Entry;

    private readonly List<Entry> _entries = [];

    public NativeMenu Add(string title, Action action, string? shortcut = null, bool isChecked = false)
    {
        _entries.Add(new Item(title, action, shortcut, isChecked));
        return this;
    }

    /// <summary>A greyed-out line: a section title or a detail.</summary>
    public NativeMenu AddHeader(string title)
    {
        _entries.Add(new Header(title));
        return this;
    }

    public NativeMenu AddSeparator()
    {
        if (_entries.Count > 0 && _entries[^1] is not Separator) _entries.Add(new Separator());
        return this;
    }

    public NativeMenu AddSubmenu(string title, NativeMenu menu)
    {
        _entries.Add(new Sub(title, menu));
        return this;
    }

    /// <summary>Shows the menu at a screen point and runs the chosen item after it closes.</summary>
    public void Show(IntPtr owner, Win32.Point at)
    {
        var actions = new List<Action>();
        var handle = Build(actions);
        try
        {
            // Without the owner in front, the menu wouldn't close when clicking elsewhere.
            Win32.SetForegroundWindow(owner);
            var chosen = Win32.TrackPopupMenuEx(handle, Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON | Win32.TPM_BOTTOMALIGN, at.X, at.Y, owner, IntPtr.Zero);
            Win32.PostMessage(owner, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (chosen > 0 && chosen <= actions.Count) actions[chosen - 1]();
        }
        finally
        {
            Win32.DestroyMenu(handle);   // destroys the submenus with it
        }
    }

    private IntPtr Build(List<Action> actions)
    {
        var menu = Win32.CreatePopupMenu();
        foreach (var entry in _entries)
        {
            switch (entry)
            {
                case Item item:
                    actions.Add(item.Action ?? (() => { }));
                    var text = Escape(item.Title) + (item.Shortcut is { } s ? "\t" + s : "");
                    Win32.AppendMenu(menu, Win32.MF_STRING | (item.Checked ? Win32.MF_CHECKED : 0), (IntPtr)actions.Count, text);
                    break;
                case Header header:
                    Win32.AppendMenu(menu, Win32.MF_STRING | Win32.MF_GRAYED, IntPtr.Zero, Escape(header.Title));
                    break;
                case Separator:
                    Win32.AppendMenu(menu, Win32.MF_SEPARATOR, IntPtr.Zero, null);
                    break;
                case Sub sub:
                    Win32.AppendMenu(menu, Win32.MF_POPUP, sub.Menu.Build(actions), Escape(sub.Title));
                    break;
            }
        }
        return menu;
    }

    /// <summary>"&amp;" marks an access key in a menu; agent names and messages may contain one.</summary>
    private static string Escape(string text) => text.Replace("&", "&&");
}
