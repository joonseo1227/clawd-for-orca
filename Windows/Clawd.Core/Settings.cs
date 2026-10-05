using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clawd.Core;

/// <summary>When to post Windows notifications: the default only covers a hidden Clawd, whose cards can't be seen.</summary>
public enum NotificationPolicy { WhenHidden, Always, Never }

public static class NotificationPolicyText
{
    public static string Title(this NotificationPolicy p) => p switch
    {
        NotificationPolicy.WhenHidden => Strings.Get("Notifications_WhenHidden"),
        NotificationPolicy.Always => Strings.Get("Notifications_Always"),
        _ => Strings.Get("Notifications_Never"),
    };
}

/// <summary>
/// Every preference the app keeps, in one JSON file (%LOCALAPPDATA%\Clawd\settings.json): an
/// unpackaged app has no ApplicationData container. Written atomically, read once at launch.
/// </summary>
public sealed class Settings
{
    public bool Sound { get; set; } = true;
    public bool Hidden { get; set; }
    public bool Orca { get; set; } = true;
    /// <summary>The first-launch welcome was shown.</summary>
    public bool Welcomed { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter<NotificationPolicy>))]
    public NotificationPolicy Notifications { get; set; } = NotificationPolicy.WhenHidden;
    /// <summary>The global "talk to Clawd" shortcut.</summary>
    public Shortcut Shortcut { get; set; } = Shortcut.Standard;
    /// <summary>Look for a newer release on GitHub once a day.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>When the release list was last read, so a restart doesn't ask GitHub again within the day.</summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }

    [JsonIgnore] public string? FilePath { get; private set; }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Settings Load(string? path = null)
    {
        path ??= Path.Combine(AppPaths.DataDirectory, "settings.json");
        Settings settings;
        try { settings = JsonSerializer.Deserialize<Settings>(File.ReadAllBytes(path), Options) ?? new Settings(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { settings = new Settings(); }
        settings.FilePath = path;
        return settings;
    }

    public void Save()
    {
        if (FilePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(this, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"settings not saved: {e.Message}");
        }
    }
}

/// <summary>
/// The system-wide "talk to Clawd" shortcut: a Windows virtual-key code and modifiers, registered
/// with RegisterHotKey and changeable in Settings. Stored by key, not character, so it follows the
/// key's position whatever the input language (a Korean layout would otherwise name J "ㅓ").
/// </summary>
public sealed record Shortcut(int Key, Shortcut.Mods Modifiers)
{
    [Flags]
    public enum Mods { None = 0, Alt = 1, Control = 2, Shift = 4, Windows = 8 }   // RegisterHotKey's MOD_* values

    public static readonly Shortcut Standard = new('J', Mods.Control | Mods.Alt);

    private static bool IsFunctionKey(int key) => key is >= 0x70 and <= 0x87;   // F1–F24

    /// <summary>A shortcut from a key press, or null when it can't be one: a global shortcut needs Ctrl,
    /// Alt or Windows (Shift alone would swallow typing), except for function keys.</summary>
    public static Shortcut? FromKeyPress(int key, Mods mods)
    {
        if (key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5) return null;   // a modifier on its own
        if (!IsFunctionKey(key) && (mods & (Mods.Control | Mods.Alt | Mods.Windows)) == 0) return null;
        return new Shortcut(key, mods);
    }

    /// <summary>"Ctrl+Alt+J", in the order Windows menus use.</summary>
    public string Display
    {
        get
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(Mods.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(Mods.Windows)) parts.Add("Win");
            if (Modifiers.HasFlag(Mods.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(Mods.Shift)) parts.Add("Shift");
            parts.Add(KeyName(Key));
            return string.Join("+", parts);
        }
    }

    public static string KeyName(int key) => key switch
    {
        >= 'A' and <= 'Z' or >= '0' and <= '9' => ((char)key).ToString(),
        >= 0x70 and <= 0x87 => $"F{key - 0x6F}",
        0x20 => "Space",
        0x0D => "Enter",
        0x09 => "Tab",
        0x1B => "Esc",
        0x08 => "Backspace",
        0x2E => "Delete",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        >= 0x60 and <= 0x69 => $"Num {key - 0x60}",
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        _ => $"#{key}",
    };
}
