using System.Xml.Linq;
using Clawd.Core;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Clawd;

/// <summary>
/// The app's own text, from Strings/&lt;language&gt;/Resources.resw. The build compiles those into
/// resources.pri, read here with MRT Core (the Windows App SDK's resource manager, which works
/// unpackaged). The language is the one Clawd.Core chose from the Windows display language
/// (<see cref="Strings.Culture"/>), set on the resource context so the WinUI and Core halves
/// never disagree. If resources.pri is missing or lacks these strings, the same .resw files,
/// embedded in Clawd.dll, are read directly instead, so the UI never shows bare keys.
/// </summary>
internal static class L
{
    private static Func<string, string?> _lookup = _ => null;

    /// <summary>Call once before any window or menu is built.</summary>
    public static void Initialize()
    {
        var language = Strings.Culture.Name;
        try
        {
            var manager = new ResourceManager();
            var context = manager.CreateResourceContext();
            context.QualifierValues["Language"] = language;
            var map = manager.MainResourceMap.GetSubtree("Resources");
            // Probe now, so a resources.pri without our strings falls back here rather than mid-UI.
            if (map.TryGetValue("Menu_Quit", context) is null) throw new InvalidOperationException("Resources/Menu_Quit not in resources.pri");
            _lookup = key => map.TryGetValue(key, context)?.ValueAsString;
            Log.Debug(() => $"strings: {language} from resources.pri");
        }
        catch (Exception e)
        {
            var embedded = Embedded(language);
            _lookup = key => embedded.GetValueOrDefault(key);
            Log.Error($"strings: resources.pri unavailable ({e.Message}); using embedded {language}");
        }
    }

    /// <summary>The string for <paramref name="key"/>, or the key itself if it's missing.</summary>
    public static string Get(string key) => _lookup(key) ?? key;

    /// <summary>A format string filled in; each language may use the arguments in any order, or leave some out.</summary>
    public static string Format(string key, params object?[] args) => string.Format(Strings.Culture, Get(key), args);

    /// <summary><c>key_One</c> or <c>key_Other</c> by <paramref name="count"/>, formatted with the count as {0}.</summary>
    public static string Plural(string key, long count, params object?[] more) =>
        string.Format(Strings.Culture, Get(key + (count == 1 ? "_One" : "_Other")), [count, .. more]);

    private static Dictionary<string, string> Embedded(string language)
    {
        var result = new Dictionary<string, string>();
        // English first, then the chosen language on top: a string missing from a translation stays readable.
        foreach (var name in new[] { "en-US", language }.Distinct())
        {
            using var stream = typeof(L).Assembly.GetManifestResourceStream($"Clawd.Strings.{name}.resw");
            if (stream is null) continue;
            foreach (var data in XDocument.Load(stream).Root!.Elements("data"))
                if (data.Attribute("name")?.Value is { } key && data.Element("value")?.Value is { } value) result[key] = value;
        }
        return result;
    }
}
