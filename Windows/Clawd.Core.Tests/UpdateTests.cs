using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Clawd.Core.Updates;

namespace Clawd.Core.Tests;

public class AppVersionTests
{
    private static AppVersion V(string s) => AppVersion.Parse(s) ?? throw new FormatException(s);

    [Theory]
    [InlineData("1.0.0", 1, 0, 0, null)]
    [InlineData("10.20.30", 10, 20, 30, null)]
    [InlineData("1.0.0-beta.1", 1, 0, 0, "beta.1")]
    [InlineData("1.0.0+abc123", 1, 0, 0, null)]            // the informational version's commit
    [InlineData("1.0.0-beta.1+abc123", 1, 0, 0, "beta.1")]
    [InlineData(" 2.1.3\n", 2, 1, 3, null)]                 // VERSION's trailing newline
    public void Parses(string text, int major, int minor, int patch, string? pre) =>
        Assert.Equal(new AppVersion(major, minor, patch, pre), AppVersion.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.x")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-beta..1")]
    [InlineData("-1.0.0")]
    [InlineData("99999999999.0.0")]
    public void RejectsWhatIsNotAVersion(string? text) => Assert.Null(AppVersion.Parse(text));

    /// <summary>SemVer's own precedence example, in ascending order.</summary>
    [Fact]
    public void OrdersBySemVerPrecedence()
    {
        string[] ascending = ["0.9.0", "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2",
            "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "1.10.0", "2.0.0"];
        for (var i = 0; i < ascending.Length - 1; i++)
        {
            Assert.True(V(ascending[i]) < V(ascending[i + 1]), $"{ascending[i]} < {ascending[i + 1]}");
            Assert.True(V(ascending[i + 1]) > V(ascending[i]), $"{ascending[i + 1]} > {ascending[i]}");
        }
        Assert.Equal(0, V("1.0.0+a").CompareTo(V("1.0.0+b")));
        Assert.Equal("1.0.0-beta.1", V("1.0.0-beta.1+abc").ToString());
    }

    [Fact]
    public void TheRunningVersionIsWindowsVersionFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "VERSION"))) dir = dir.Parent;
        Assert.Equal(AppVersion.Parse(File.ReadAllText(Path.Combine(dir.FullName, "VERSION"))), AppVersion.Current);
    }
}

public class UpdateFeedTests
{
    internal const string Digest = "7a4fc838e931b91f7ba8bd37edf4e51bb245fa7ea96f7dc6765c09592ff12b47";

    internal static JsonObject Release(string tag, string? assetName = null, string? digest = "sha256:" + Digest, bool draft = false,
        bool prerelease = false, string host = "https://github.com", long size = 1234)
    {
        var name = assetName ?? $"Clawd-{tag["windows-v".Length..]}-Windows-x64.msi";
        var asset = new JsonObject
        {
            ["name"] = name,
            ["size"] = size,
            ["browser_download_url"] = $"{host}/joonseo1227/clawd-for-orca/releases/download/{tag}/{name}",
        };
        if (digest is not null) asset["digest"] = digest;
        return new JsonObject
        {
            ["tag_name"] = tag,
            ["draft"] = draft,
            ["prerelease"] = prerelease,
            ["html_url"] = $"{host}/joonseo1227/clawd-for-orca/releases/tag/{tag}",
            ["assets"] = new JsonArray(asset),
        };
    }

    internal static string Feed(params JsonObject[] releases) => new JsonArray(releases.Select(r => (JsonNode)r).ToArray()).ToJsonString();

    private static readonly AppVersion Current = new(1, 0, 0);

    [Fact]
    public void PicksTheNewestWindowsRelease()
    {
        var feed = Feed(
            Release("macos-v9.0.0", "Clawd-9.0.0-Windows-x64.msi"),   // the Mac app's tag, even with a matching file
            Release("windows-v1.2.0", draft: true),
            Release("windows-v1.3.0", prerelease: true),
            Release("windows-v1.0.1"),
            Release("windows-v1.1.0"),
            Release("windows-v1.0.0"),
            Release("windows-v0.9.0"),
            Release("windows-vnext"));
        var update = UpdateFeed.Newest(feed, Current, "x64");
        Assert.NotNull(update);
        Assert.Equal(new AppVersion(1, 1, 0), update.Version);
        Assert.Equal("Clawd-1.1.0-Windows-x64.msi", update.AssetName);
        Assert.Equal(new Uri("https://github.com/joonseo1227/clawd-for-orca/releases/download/windows-v1.1.0/Clawd-1.1.0-Windows-x64.msi"), update.Download);
        Assert.Equal(new Uri("https://github.com/joonseo1227/clawd-for-orca/releases/tag/windows-v1.1.0"), update.Page);
        Assert.Equal(1234, update.Size);
        Assert.Equal(Digest, update.Sha256);
    }

    [Fact]
    public void NothingNewerIsNoUpdate()
    {
        Assert.Null(UpdateFeed.Newest(Feed(Release("windows-v1.0.0"), Release("windows-v0.9.0")), Current, "x64"));
        Assert.Null(UpdateFeed.Newest("[]", Current, "x64"));
        // A pre-release of the running version ranks below it.
        Assert.Null(UpdateFeed.Newest(Feed(Release("windows-v1.0.0-beta.2")), Current, "x64"));
        Assert.Equal(new AppVersion(1, 0, 0), UpdateFeed.Newest(Feed(Release("windows-v1.0.0")), new AppVersion(1, 0, 0, "beta.1"), "x64")?.Version);
    }

    [Fact]
    public void AReleaseWithoutAUsableInstallerFallsBackToAnOlderOne()
    {
        var feed = Feed(
            Release("windows-v1.4.0", digest: null),                         // no digest to check against
            Release("windows-v1.3.0", digest: "sha256:abc"),                 // not a SHA-256
            Release("windows-v1.2.0", digest: "md5:" + Digest),
            Release("windows-v1.1.5", host: "https://example.com"),          // links off github.com
            Release("windows-v1.1.2", host: "http://github.com"),            // not HTTPS
            Release("windows-v1.1.1", "Clawd-1.1.1-Windows-x64.zip"),        // no MSI
            Release("windows-v1.1.0", size: 0),
            Release("windows-v1.0.5"));
        Assert.Equal(new AppVersion(1, 0, 5), UpdateFeed.Newest(feed, Current, "x64")?.Version);
    }

    [Fact]
    public void TheInstallerMatchesTheArchitecture()
    {
        var feed = Feed(Release("windows-v1.1.0"));
        Assert.Null(UpdateFeed.Newest(feed, Current, "arm64"));
        Assert.NotNull(UpdateFeed.Newest(Feed(Release("windows-v1.1.0", "Clawd-1.1.0-Windows-arm64.msi")), Current, "arm64"));
    }

    [Fact]
    public void ATestFeedMayLinkAnywhere()
    {
        var feed = Feed(Release("windows-v1.1.0", host: "file:///C:/feed"));
        Assert.Null(UpdateFeed.Newest(feed, Current, "x64"));
        Assert.NotNull(UpdateFeed.Newest(feed, Current, "x64", anyHost: true));
    }

    [Fact]
    public void AnUnreadableListIsAnError()
    {
        Assert.Throws<FormatException>(() => UpdateFeed.Newest("{\"message\":\"API rate limit exceeded\"}", Current, "x64"));
        Assert.Throws<FormatException>(() => UpdateFeed.Newest("<html>", Current, "x64"));
    }

    [Fact]
    public void OnlyTheInstalledCopyUpdates()
    {
        var install = OperatingSystem.IsWindows() ? @"C:\Users\me\AppData\Local\Programs\Clawd" : "/home/me/Programs/Clawd";
        Assert.True(Updater.IsInstalledCopy(install + Path.DirectorySeparatorChar, install));   // AppContext.BaseDirectory ends in a separator
        Assert.False(Updater.IsInstalledCopy(Path.Combine(install, "publish"), install));
        Assert.False(Updater.IsInstalledCopy(OperatingSystem.IsWindows() ? @"C:\src\Clawd\bin" : "/src/Clawd/bin", install));
    }
}

public sealed class UpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clawd-update-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Msi = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 7)).ToArray();
    private static readonly string MsiDigest = Convert.ToHexStringLower(SHA256.HashData(Msi));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>Answers the release list and the installer download; counts the downloads.</summary>
    private sealed class FakeGitHub(string feed, byte[] msi, HttpStatusCode listStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Downloads;
        public string? Accept;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                Accept = request.Headers.Accept.ToString();
                return Task.FromResult(new HttpResponseMessage(listStatus) { Content = new StringContent(feed) });
            }
            Interlocked.Increment(ref Downloads);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(msi) });
        }
    }

    private static string Feed(string digest, long? size = null) =>
        UpdateFeedTests.Feed(UpdateFeedTests.Release("windows-v1.1.0", digest: "sha256:" + digest, size: size ?? Msi.Length));

    private Updater Make(FakeGitHub github) => new(new HttpClient(github), new AppVersion(1, 0, 0), _dir, arch: "x64");

    [Fact]
    public async Task DownloadsAndVerifiesTheInstaller()
    {
        var github = new FakeGitHub(Feed(MsiDigest), Msi);
        var updater = Make(github);
        var status = await updater.Check();
        Assert.Equal(UpdateStage.Ready, status.Stage);
        Assert.Equal(new AppVersion(1, 1, 0), status.Release?.Version);
        Assert.Equal(Path.Combine(_dir, "Clawd-1.1.0-Windows-x64.msi"), status.Installer);
        Assert.Equal(Msi, File.ReadAllBytes(status.Installer!));
        Assert.Contains("application/vnd.github+json", github.Accept);
        Assert.Single(Directory.GetFiles(_dir));   // no .part left behind

        // Checked again (the next day, or after a restart): the verified file is reused.
        Assert.Equal(UpdateStage.Ready, (await updater.Check()).Stage);
        Assert.Equal(1, github.Downloads);
    }

    [Fact]
    public async Task ACorruptedDownloadIsRejectedAndRemoved()
    {
        var corrupted = (byte[])Msi.Clone();
        corrupted[1000] ^= 0xFF;
        var status = await Make(new FakeGitHub(Feed(MsiDigest), corrupted)).Check();
        Assert.Equal(UpdateStage.Failed, status.Stage);
        Assert.Equal(UpdateStage.Downloading, status.Failure);
        Assert.Equal(Strings.Get("Update_DigestMismatch"), status.Error);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task ADownloadOfTheWrongSizeIsRejected()
    {
        var shorter = await Make(new FakeGitHub(Feed(MsiDigest, Msi.Length + 10), Msi)).Check();
        Assert.Equal(UpdateStage.Failed, shorter.Stage);
        Assert.Equal(Strings.Get("Update_Incomplete"), shorter.Error);
        var longer = await Make(new FakeGitHub(Feed(MsiDigest, Msi.Length - 10), Msi)).Check();
        Assert.Equal(UpdateStage.Failed, longer.Stage);
        Assert.Equal(Strings.Get("Update_DigestMismatch"), longer.Error);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task AFileWithTheRightNameButOtherContentIsReplaced()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "Clawd-1.1.0-Windows-x64.msi"), [1, 2, 3]);
        var github = new FakeGitHub(Feed(MsiDigest), Msi);
        var status = await Make(github).Check();
        Assert.Equal(UpdateStage.Ready, status.Stage);
        Assert.Equal(1, github.Downloads);
        Assert.Equal(Msi, File.ReadAllBytes(status.Installer!));
    }

    [Fact]
    public async Task UpToDateAndFailedChecks()
    {
        var current = UpdateFeedTests.Feed(UpdateFeedTests.Release("windows-v1.0.0"));
        Assert.Equal(UpdateStage.UpToDate, (await Make(new FakeGitHub(current, Msi)).Check()).Stage);
        var limited = await Make(new FakeGitHub("{\"message\":\"API rate limit exceeded\"}", Msi, HttpStatusCode.Forbidden)).Check();
        Assert.Equal(UpdateStage.Failed, limited.Stage);
        Assert.Equal(UpdateStage.Checking, limited.Failure);
        Assert.Contains("403", limited.Error);
    }

    [Fact]
    public async Task ChecksRunOneAtATime()
    {
        var github = new FakeGitHub(Feed(MsiDigest), Msi);
        var updater = Make(github);
        var statuses = await Task.WhenAll(updater.Check(), updater.Check(), updater.Check());
        Assert.All(statuses, s => Assert.Equal(UpdateStage.Ready, s.Stage));
        Assert.Equal(1, github.Downloads);
    }

    [Fact]
    public async Task AFailedInstallLeavesTheUpdateReady()
    {
        var updater = Make(new FakeGitHub(Feed(MsiDigest), Msi));
        await updater.Check();
        updater.Installing();
        Assert.Equal(UpdateStage.Installing, updater.Status.Stage);
        Assert.Equal(UpdateStage.Installing, (await updater.Check()).Stage);   // no check while msiexec runs
        updater.InstallFailed("1602");
        Assert.Equal(UpdateStage.Ready, updater.Status.Stage);
        Assert.Equal(UpdateStage.Installing, updater.Status.Failure);
        Assert.NotNull(updater.Status.Installer);
    }

    [Fact]
    public async Task ATestFeedCanBeAFileWithFileLinks()
    {
        Directory.CreateDirectory(_dir);
        var source = Path.Combine(_dir, "source.msi");
        File.WriteAllBytes(source, Msi);
        var release = UpdateFeedTests.Release("windows-v1.1.0", digest: "sha256:" + MsiDigest, size: Msi.Length);
        release["assets"]![0]!["browser_download_url"] = new Uri(source).AbsoluteUri;
        var feed = Path.Combine(_dir, "feed.json");
        File.WriteAllText(feed, UpdateFeedTests.Feed(release));
        var updater = new Updater(new HttpClient(new FakeGitHub("[]", [])), new AppVersion(1, 0, 0), Path.Combine(_dir, "Updates"), feed, "x64");
        var status = await updater.Check();
        Assert.Equal(UpdateStage.Ready, status.Stage);
        Assert.Equal(Msi, File.ReadAllBytes(status.Installer!));
    }

    [Fact]
    public void StaleInstallersAreRemovedAndNewerOnesKept()
    {
        Directory.CreateDirectory(_dir);
        foreach (var name in new[] { "Clawd-0.9.0-Windows-x64.msi", "Clawd-1.0.0-Windows-x64.msi", "Clawd-1.1.0-Windows-x64.msi", "Clawd-1.2.0-Windows-x64.msi.part", "notes.txt" })
            File.WriteAllText(Path.Combine(_dir, name), "x");
        var current = new AppVersion(1, 0, 0);
        Assert.True(Updater.HasPending(_dir, current));
        Updater.RemoveStale(_dir, current);
        Assert.Equal(["Clawd-1.1.0-Windows-x64.msi"], Directory.GetFiles(_dir).Select(Path.GetFileName));
        Assert.False(Updater.HasPending(_dir, new AppVersion(1, 1, 0)));
        Updater.RemoveStale(Path.Combine(_dir, "missing"), current);   // no folder yet
    }
}
