namespace Clawd.Core.Terminal;

/// <summary>
/// The bytes a terminal sends for a key press, for the terminal view before pairing (the live
/// view's xterm.js does this itself). Uses Windows virtual-key codes; printable text arrives
/// separately through the text input, so IMEs compose Korean properly.
/// </summary>
public static class TerminalKeys
{
    [Flags]
    public enum Modifiers { None = 0, Control = 1, Shift = 2, Alt = 4 }

    // Windows virtual-key codes.
    public const int Back = 0x08, Tab = 0x09, Enter = 0x0D, Escape = 0x1B, PageUp = 0x21, PageDown = 0x22,
        End = 0x23, Home = 0x24, Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28, Delete = 0x2E,
        // The [, \ and ] keys of US and Korean layouts.
        OpenBracket = 0xDB, Backslash = 0xDC, CloseBracket = 0xDD;

    /// <summary>The escape sequence or control character for a key, or null when it is ordinary text
    /// (or a key the terminal doesn't use).</summary>
    public static string? Map(int key, Modifiers mods)
    {
        var ctrl = mods.HasFlag(Modifiers.Control);
        var shift = mods.HasFlag(Modifiers.Shift);
        var alt = mods.HasFlag(Modifiers.Alt);
        // Ctrl-letter: the control character (Ctrl-C interrupts, Ctrl-D ends, ...).
        if (ctrl && !alt && key is >= 'A' and <= 'Z') return ((char)(key - 'A' + 1)).ToString();
        // Ctrl-[ is Esc, Ctrl-\ and Ctrl-] the next two control characters, as on the Mac.
        if (ctrl && !alt && key is OpenBracket or Backslash or CloseBracket) return ((char)(0x1B + key - OpenBracket)).ToString();
        switch (key)
        {
            // Shift-Enter is a newline inside Claude Code's prompt rather than a submit.
            case Enter: return shift || alt ? "\u001b\r" : "\r";
            case Tab: return shift ? "\u001b[Z" : "\t";
            // Ctrl-Backspace deletes a word, as Option-Delete does on the Mac.
            case Back: return ctrl ? "\u0017" : "\u007f";
            case Delete: return "\u001b[3~";
            case Up: return "\u001b[A";
            case Down: return "\u001b[B";
            case Right: return ctrl ? "\u001bf" : "\u001b[C";
            case Left: return ctrl ? "\u001bb" : "\u001b[D";
            case Escape: return "\u001b";
            case Home: return "\u0001";
            case End: return "\u0005";
            case PageUp: return "\u001b[5~";
            case PageDown: return "\u001b[6~";
            default: return null;
        }
    }

    /// <summary>Bracketed paste: a pasted newline must not submit the prompt halfway.</summary>
    public static string Paste(string text) => "\u001b[200~" + text + "\u001b[201~";
}
