using System.Globalization;
using System.Resources;

[assembly: NeutralResourcesLanguage("en-US")]

namespace Clawd.Core;

/// <summary>
/// Text that Clawd.Core builds (card titles, sidebar statuses, relative times, step summaries),
/// from Localization/Strings.resx (English, built into Clawd.Core.dll) and Strings.ko.resx (the
/// ko satellite assembly). The app speaks Korean when the Windows display language is Korean and
/// English otherwise; the language is read from <see cref="CultureInfo.CurrentUICulture"/>
/// on every call, so tests can switch it per test. The WinUI project's own text lives in .resw
/// files and asks <see cref="Culture"/> which language to load, so both halves always agree.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Manager = new(typeof(Strings).FullName!, typeof(Strings).Assembly);
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>The language Clawd shows for a system UI culture: ko-KR for any Korean, else en-US.</summary>
    public static CultureInfo Resolve(CultureInfo system) => system.TwoLetterISOLanguageName == "ko" ? Korean : English;

    /// <summary>The language Clawd is showing now.</summary>
    public static CultureInfo Culture => Resolve(CultureInfo.CurrentUICulture);

    public static bool IsKorean => Culture == Korean;

    /// <summary>The string for <paramref name="key"/>; the key itself when it's missing, so a gap shows up instead of crashing.</summary>
    public static string Get(string key) => Manager.GetString(key, Culture) ?? key;

    /// <summary>A composite format string filled in. Each language may use any subset of the
    /// arguments, in any order (Korean puts the verb last; English may pick a different argument).</summary>
    public static string Format(string key, params object?[] args) => string.Format(Culture, Get(key), args);

    /// <summary>Chooses <c>key_One</c> or <c>key_Other</c> by <paramref name="count"/> (English needs
    /// both; Korean repeats the same text), then formats it with the count as {0}.</summary>
    public static string Plural(string key, long count, params object?[] more) =>
        string.Format(Culture, Get(key + (count == 1 ? "_One" : "_Other")), [count, .. more]);
}
