namespace Clawd.Core;

/// <summary>
/// Files dropped on Clawd, the chat or the terminal become their paths, quoted the way Windows
/// Terminal and Explorer's "Copy as path" write them, so PowerShell, cmd and Claude Code all read
/// each one as a single path (and images as images).
/// </summary>
public static class FileDrop
{
    /// <summary>Characters PowerShell or cmd would read specially. A Windows path can't contain a
    /// double quote, so wrapping in double quotes is always enough.</summary>
    private const string Special = " \t&()[]{}^=;!'+,`~$@#%|<>";

    public static string Quote(string path) => path.Any(c => Special.Contains(c)) ? $"\"{path}\"" : path;

    /// <summary>The dropped files' paths, separated by spaces.</summary>
    public static string Text(IEnumerable<string> paths) =>
        string.Join(" ", paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Quote));

    /// <summary><paramref name="text"/> added to a draft, with a space between them when the draft doesn't end in one.</summary>
    public static string Append(string text, string draft)
    {
        if (text.Length == 0) return draft;
        if (draft.Length == 0 || char.IsWhiteSpace(draft[^1])) return draft + text + " ";
        return draft + " " + text + " ";
    }

    /// <summary>Typed into a terminal as a paste, so the paths arrive whole and nothing is submitted.</summary>
    public static string Paste(string text) => "\u001b[200~" + text + " \u001b[201~";
}
