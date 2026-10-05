using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clawd.Core.Updates;

/// <summary>A Windows release newer than the running app, with the installer to download.</summary>
public sealed record UpdateRelease(AppVersion Version, Uri Page, string AssetName, Uri Download, long Size, string Sha256);

/// <summary>
/// Reads GitHub's release list. The repository also holds the Mac app's releases (<c>macos-v…</c>)
/// and GitHub's "latest release" is whichever was published last, so the list is filtered by tag
/// rather than asking for the latest. An installer is only trusted with the SHA-256 digest GitHub
/// records for every uploaded asset: the MSI isn't code-signed, so that digest, fetched over HTTPS
/// from the API, is what the download is checked against.
/// </summary>
public static class UpdateFeed
{
    public const string Url = "https://api.github.com/repos/joonseo1227/clawd-for-orca/releases?per_page=30";
    public const string TagPrefix = "windows-v";
    public static readonly Uri ReleasesPage = new("https://github.com/joonseo1227/clawd-for-orca/releases");

    /// <summary>The installer's architecture: an x64 Clawd running emulated on ARM64 updates to x64.</summary>
    public static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>The release workflow's file name (Installer/Clawd.Installer.wixproj).</summary>
    public static string AssetName(string version, string arch) => $"Clawd-{version}-Windows-{arch}.msi";

    /// <summary>The newest published Windows release above <paramref name="current"/> that has a
    /// usable installer for <paramref name="arch"/>; null when there is none. A newer release that
    /// can't be used is skipped with a log line, so an older usable one may still be offered.
    /// <paramref name="anyHost"/> accepts links outside github.com (CLAWD_UPDATE_FEED tests).</summary>
    public static UpdateRelease? Newest(string json, AppVersion current, string arch, bool anyHost = false)
    {
        JsonArray? releases;
        try { releases = JsonNode.Parse(json) as JsonArray; }
        catch (JsonException e) { throw new FormatException($"unreadable release list: {e.Message}", e); }
        if (releases is null) throw new FormatException("the release list isn't a JSON array");

        var candidates = releases.OfType<JsonObject>()
            .Where(r => r.Bool("draft") != true && r.Bool("prerelease") != true)
            .Select(r => (Release: r, Tag: r.Str("tag_name") ?? ""))
            .Where(r => r.Tag.StartsWith(TagPrefix, StringComparison.Ordinal))
            .Select(r => (r.Release, Text: r.Tag[TagPrefix.Length..], Version: AppVersion.Parse(r.Tag[TagPrefix.Length..])))
            .Where(r => r.Version is not null && r.Version > current)
            .OrderByDescending(r => r.Version);
        foreach (var (release, text, version) in candidates)
        {
            var name = AssetName(text, arch);
            if (Usable(release, version!, name, anyHost, out var update, out var why)) return update;
            Log.Error($"update {text} skipped: {why}");
        }
        return null;
    }

    private static bool Usable(JsonObject release, AppVersion version, string name, bool anyHost, out UpdateRelease? update, out string why)
    {
        update = null;
        var asset = release.Objects("assets").FirstOrDefault(a => a.Str("name") == name);
        if (asset is null) { why = $"no {name}"; return false; }
        if (!Uri.TryCreate(asset.Str("browser_download_url"), UriKind.Absolute, out var download) || !Trusted(download, anyHost))
        {
            why = $"download link not on github.com: {asset.Str("browser_download_url")}";
            return false;
        }
        if (!Uri.TryCreate(release.Str("html_url"), UriKind.Absolute, out var page) || !Trusted(page, anyHost))
        {
            why = $"release page not on github.com: {release.Str("html_url")}";
            return false;
        }
        var digest = asset.Str("digest");
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 7 + 64 || !digest[7..].All(char.IsAsciiHexDigit))
        {
            why = $"no SHA-256 digest ({digest ?? "missing"})";
            return false;
        }
        if (asset.Num("size") is not { } size || size <= 0) { why = "no size"; return false; }
        update = new UpdateRelease(version, page, name, download, (long)size, digest[7..].ToLowerInvariant());
        why = "";
        return true;
    }

    private static bool Trusted(Uri uri, bool anyHost) =>
        anyHost ? uri.Scheme is "https" or "http" or "file" : uri.Scheme == "https" && uri.Host == "github.com";
}
