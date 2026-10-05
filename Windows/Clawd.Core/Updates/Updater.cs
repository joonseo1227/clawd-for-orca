using System.Security.Cryptography;
using Clawd.Core.Platform;

namespace Clawd.Core.Updates;

public enum UpdateStage { Idle, Checking, UpToDate, Downloading, Ready, Installing, Failed }

/// <summary>Where the updater is. <see cref="Release"/> is set from Downloading on; <see cref="Installer"/>
/// once Ready. <see cref="Failure"/> says which step failed, <see cref="Error"/> why.</summary>
public sealed record UpdateStatus(UpdateStage Stage, UpdateRelease? Release = null, string? Installer = null,
    UpdateStage Failure = UpdateStage.Idle, string? Error = null)
{
    public static readonly UpdateStatus Idle = new(UpdateStage.Idle);
}

/// <summary>
/// Finds a newer Windows release and downloads its installer into <see cref="Directory"/>, checked
/// against the size and SHA-256 digest from the release list before it is used. Installing is the
/// app's job (msiexec); this part has no UI and runs on any OS, so it is unit-tested.
/// </summary>
public sealed class Updater
{
    private readonly HttpClient _http;
    private readonly AppVersion _current;
    private readonly string _arch;
    private readonly string? _feed;
    private readonly object _gate = new();
    private Task<UpdateStatus>? _running;
    private volatile UpdateStatus _status = UpdateStatus.Idle;

    /// <param name="feed">A file path or URL to read the release list from instead of GitHub's API;
    /// links in it may then point anywhere, file: URLs included (CLAWD_UPDATE_FEED).</param>
    public Updater(HttpClient http, AppVersion current, string directory, string? feed = null, string? arch = null)
    {
        _http = http;
        _current = current;
        Directory = directory;
        _feed = feed;
        _arch = arch ?? UpdateFeed.Arch;
    }

    public static string DefaultDirectory => Path.Combine(AppPaths.DataDirectory, "Updates");

    /// <summary>Where the per-user MSI installs Clawd (Installer/Package.wxs).</summary>
    public static string InstallDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Programs", "Clawd");

    /// <summary>Only the copy the MSI installed updates itself: an installer run from a development
    /// build or a copied folder would put a second Clawd in Programs instead of replacing this one.</summary>
    public static bool IsInstalledCopy(string appDirectory, string installDirectory) =>
        string.Equals(Paths.Normalize(appDirectory), Paths.Normalize(installDirectory), Paths.Comparison);

    public string Directory { get; }

    /// <summary>Read from any thread: replaced whole, never changed in place.</summary>
    public UpdateStatus Status => _status;

    public static readonly TimeSpan FeedTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    /// <summary>Checks and, when there is a newer release, downloads it. A check already under way is
    /// joined rather than started again. A ready installer that is still the newest is reused.</summary>
    public Task<UpdateStatus> Check()
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false } running) return running;
            if (_status.Stage == UpdateStage.Installing) return Task.FromResult(_status);
            return _running = Task.Run(Run);
        }
    }

    /// <summary>msiexec was started with the ready installer.</summary>
    public void Installing()
    {
        if (_status is { Stage: UpdateStage.Ready } s) _status = s with { Stage = UpdateStage.Installing };
    }

    /// <summary>msiexec gave up while Clawd was still running: the installer stays ready to try again.</summary>
    public void InstallFailed(string error)
    {
        if (_status is { Stage: UpdateStage.Installing } s) _status = s with { Stage = UpdateStage.Ready, Failure = UpdateStage.Installing, Error = error };
    }

    private async Task<UpdateStatus> Run()
    {
        var previous = _status;
        _status = new UpdateStatus(UpdateStage.Checking, previous.Release);
        UpdateRelease? release;
        try
        {
            release = UpdateFeed.Newest(await ReadFeed().ConfigureAwait(false), _current, _arch, anyHost: _feed is not null);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or FormatException or OperationCanceledException)
        {
            Log.Error($"update check failed: {e.Message}");
            return _status = new UpdateStatus(UpdateStage.Failed, Failure: UpdateStage.Checking, Error: Message(e));
        }
        if (release is null)
        {
            Log.Debug(() => $"update: {_current} is the newest");
            return _status = new UpdateStatus(UpdateStage.UpToDate);
        }

        _status = new UpdateStatus(UpdateStage.Downloading, release);
        try
        {
            var path = await Download(release).ConfigureAwait(false);
            Log.Debug(() => $"update: {release.Version} ready at {path}");
            return _status = new UpdateStatus(UpdateStage.Ready, release, path);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
        {
            Log.Error($"update download failed: {e.Message}");
            return _status = new UpdateStatus(UpdateStage.Failed, release, Failure: UpdateStage.Downloading, Error: Message(e));
        }
    }

    /// <summary>Shown in Settings. A timeout surfaces as a cancellation whose message says nothing useful.</summary>
    private static string Message(Exception e) => e is OperationCanceledException ? Strings.Get("Update_TimedOut") : e.Message;

    private async Task<string> ReadFeed()
    {
        if (_feed is not null && !_feed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return await File.ReadAllTextAsync(Uri.TryCreate(_feed, UriKind.Absolute, out var f) && f.IsFile ? f.LocalPath : _feed).ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(FeedTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, _feed ?? UpdateFeed.Url);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
    }

    /// <summary>The verified installer's path. Written under a temporary name and renamed only once
    /// its size and digest match, so a file with the final name is always a checked one.</summary>
    private async Task<string> Download(UpdateRelease release)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, release.AssetName);
        if (File.Exists(path))
        {
            if (await Matches(path, release).ConfigureAwait(false)) return path;
            File.Delete(path);
        }
        var part = path + ".part";
        try
        {
            using var timeout = new CancellationTokenSource(DownloadTimeout);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long size = 0;
            // A file: link only comes from a CLAWD_UPDATE_FEED test feed.
            using var response = release.Download.IsFile ? null
                : await _http.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response?.EnsureSuccessStatusCode();
            await using (var source = response is null ? File.OpenRead(release.Download.LocalPath) : await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    size += read;
                    if (size > release.Size)
                    {
                        Log.Error($"update: download larger than the {release.Size} bytes the release lists");
                        throw new InvalidDataException(Strings.Get("Update_DigestMismatch"));
                    }
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                }
            }
            if (size != release.Size)
            {
                Log.Error($"update: downloaded {size} of {release.Size} bytes");
                throw new InvalidDataException(Strings.Get("Update_Incomplete"));
            }
            var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (digest != release.Sha256)
            {
                Log.Error($"update: download's SHA-256 is {digest}, the release lists {release.Sha256}");
                throw new InvalidDataException(Strings.Get("Update_DigestMismatch"));
            }
            File.Move(part, path, overwrite: true);
            return path;
        }
        finally
        {
            try { File.Delete(part); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<bool> Matches(string path, UpdateRelease release)
    {
        try
        {
            await using var file = File.OpenRead(path);
            if (file.Length != release.Size) return false;
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(file).ConfigureAwait(false)) == release.Sha256;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Removes installers left from earlier updates: anything not newer than the running
    /// version (it was installed, or skipped), and unfinished downloads. A newer one is kept, to be
    /// reused once the release list confirms its digest.</summary>
    public static void RemoveStale(string directory, AppVersion current)
    {
        if (!System.IO.Directory.Exists(directory)) return;
        foreach (var file in System.IO.Directory.EnumerateFiles(directory))
        {
            if (Pending(Path.GetFileName(file)) is { } v && v > current) continue;
            try { File.Delete(file); }
            catch (IOException e) { Log.Debug(() => $"stale update not removed: {e.Message}"); }
            catch (UnauthorizedAccessException e) { Log.Debug(() => $"stale update not removed: {e.Message}"); }
        }
    }

    /// <summary>True when a downloaded installer newer than <paramref name="current"/> is waiting.</summary>
    public static bool HasPending(string directory, AppVersion current) =>
        System.IO.Directory.Exists(directory)
        && System.IO.Directory.EnumerateFiles(directory, "*.msi").Any(f => Pending(Path.GetFileName(f)) is { } v && v > current);

    /// <summary>The version in a downloaded installer's name, Clawd-&lt;version&gt;-Windows-&lt;arch&gt;.msi.</summary>
    private static AppVersion? Pending(string name)
    {
        const string prefix = "Clawd-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) return null;
        var windows = name.LastIndexOf("-Windows-", StringComparison.Ordinal);
        return windows > prefix.Length ? AppVersion.Parse(name[prefix.Length..windows]) : null;
    }
}
