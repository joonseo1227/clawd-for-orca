using System.Diagnostics;
using System.Net;
using Clawd.Core;
using Clawd.Core.Chat;
using Clawd.Core.Updates;
using Clawd.Services;

namespace Clawd;

// Updates: a newer release is found on GitHub and its installer downloaded and checked in the
// background; the MSI installs it when the user restarts to update.
internal sealed partial class AppController
{
    private static readonly HttpClient UpdateHttp = CreateUpdateHttp();
    private bool _installWhenReady;

    /// <summary>Null in a copy that doesn't update itself: a development build or a copied folder,
    /// where the MSI would install a second Clawd rather than replace this one.</summary>
    public Updater? Updates { get; private set; }

    private static HttpClient CreateUpdateHttp()
    {
        var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = Updater.FeedTimeout,   // until the response headers; the download has its own limit
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Clawd/{AppVersion.Current?.ToString() ?? "dev"}");
        return http;
    }

    private void StartUpdates()
    {
        Notifier.UpdateResponded += install => { if (install) InstallUpdate(); else OpenSettings(); };
        if (AppVersion.Current is not { } current || !Updater.IsInstalledCopy(AppContext.BaseDirectory, Updater.InstallDirectory)) return;
        Updater.RemoveStale(Updater.DefaultDirectory, current);
        Updates = new Updater(UpdateHttp, current, Updater.DefaultDirectory, TestHooks.UpdateFeed);
        // A ready-to-install toast left from before this launch is stale.
        Notifier.ClearUpdate();
        // The clock is looked at hourly: a timer due in a day doesn't keep time across sleep.
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromHours(1);
        timer.Tick += (_, _) => AutoCheck();
        timer.Start();
        // Not straight after a failed update, which would offer the same installer again at once.
        if (!ReportFailedUpdate()) After(10, AutoCheck);
    }

    /// <summary>At most once a day, which keeps well inside GitHub's 60 unauthenticated requests an
    /// hour. A failed check isn't counted, so it is tried again an hour later.</summary>
    private void AutoCheck()
    {
        if (Updates is not { } updates || !Settings.CheckForUpdates || AppVersion.Current is not { } current) return;
        var due = TestHooks.UpdateFeed is not null
            || Settings.LastUpdateCheck is not { } last || DateTimeOffset.Now - last >= TimeSpan.FromDays(1)
            // An installer downloaded before a restart: find out whether it is still the newest.
            || (updates.Status.Stage == UpdateStage.Idle && Updater.HasPending(updates.Directory, current));
        if (due) _ = CheckForUpdates(manual: false);
    }

    /// <summary>A check and, if there is a newer release, its download. Only a check Clawd started on
    /// its own announces the result; Settings shows what a manual one found.</summary>
    public async Task CheckForUpdates(bool manual)
    {
        if (Updates is not { } updates) return;
        try
        {
            var status = await updates.Check();
            if (status is not { Stage: UpdateStage.Failed, Failure: UpdateStage.Checking })
            {
                Settings.LastUpdateCheck = DateTimeOffset.Now;
                Settings.Save();
            }
            if (status is { Stage: UpdateStage.Ready, Release: { } release })
            {
                if (_installWhenReady) InstallUpdate();
                else if (!manual && Settings.Notifications != NotificationPolicy.Never) Notifier.PostUpdate(release.Version.ToString(), release.Page, Settings.Sound);
            }
            else if (_installWhenReady)
            {
                _installWhenReady = false;
                if (status.Stage == UpdateStage.UpToDate) Say(L.Get("Update_UpToDate"), SignSymbol.CheckCircle, 3);
                else Say(L.Get(status.Failure == UpdateStage.Downloading ? "Update_DownloadFailed" : "Update_CheckFailed"), SignSymbol.Warning, 4);
            }
        }
        catch (Exception e)
        {
            Log.Error($"update check: {e}");
        }
    }

    public void ToggleAutoUpdate()
    {
        Settings.CheckForUpdates = !Settings.CheckForUpdates;
        Settings.Save();
        AutoCheck();
    }

    /// <summary>The menu item that installs a ready update, or null.</summary>
    private string? UpdateMenuTitle => Updates?.Status is { Stage: UpdateStage.Ready, Release: { } r } ? L.Format("Menu_RestartToUpdate", r.Version) : null;

    /// <summary>
    /// Runs the downloaded MSI with a progress bar only. Clawd doesn't quit by itself: the installer's
    /// QuitClawd action runs <c>Clawd.exe --quit</c> before it replaces any file, and starts the new
    /// Clawd when it finishes (CLAWD_RELAUNCH, Package.wxs). So if msiexec gives up first (cancelled,
    /// another installation running), Clawd is still here to say so.
    /// </summary>
    public void InstallUpdate()
    {
        if (Updates is not { } updates || updates.Status.Stage == UpdateStage.Installing) return;
        if (updates.Status is not { Stage: UpdateStage.Ready, Installer: { } msi })
        {
            // A toast clicked after a restart, which started Clawd or outlived the check: look again first.
            _installWhenReady = true;
            _ = CheckForUpdates(manual: true);
            return;
        }
        _installWhenReady = false;
        Process? process;
        try
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", InstallCommand }) info.ArgumentList.Add(argument);
            // Paths go in the environment rather than the command, so no name needs quoting.
            info.Environment["CLAWD_UPDATE_MSI"] = msi;
            info.Environment["CLAWD_UPDATE_APP"] = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Clawd.exe");
            process = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            Log.Error($"update installer didn't start: {e.Message}");
            Say(L.Format("Update_InstallFailed", e.Message), SignSymbol.Warning, 5);
            return;
        }
        if (process is null) return;
        Log.Debug(() => $"update: installing {msi} (powershell {process.Id})");
        updates.Installing();
        Notifier.ClearUpdate();
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => Dispatcher.TryEnqueue(() => InstallerExited(process));
    }

    /// <summary>
    /// msiexec, waited for by Windows PowerShell rather than by Clawd: the installer quits Clawd before
    /// it replaces the files, and should it fail after that, nothing would start Clawd again. The
    /// command does, with <see cref="UpdateFailedArgument"/>, and exits with msiexec's code for a
    /// Clawd that is still running. A -Command rather than a script file, which execution policy
    /// could block.
    /// </summary>
    private const string InstallCommand =
        "$p = Start-Process -FilePath ($env:SystemRoot + '\\System32\\msiexec.exe') -Wait -PassThru " +
        "-ArgumentList ('/i ' + [char]34 + $env:CLAWD_UPDATE_MSI + [char]34 + ' /passive /norestart CLAWD_RELAUNCH=1'); " +
        "if (@(0, 1641, 3010) -notcontains $p.ExitCode -and -not (Get-Process -Name Clawd -ErrorAction SilentlyContinue)) " +
        "{ Start-Process -FilePath $env:CLAWD_UPDATE_APP -ArgumentList ('" + UpdateFailedArgument + " ' + $p.ExitCode) }; " +
        "exit $p.ExitCode";

    /// <summary>Clawd was started again after its update failed; msiexec's exit code follows.</summary>
    public const string UpdateFailedArgument = "--update-failed";

    /// <summary>What to tell the user about msiexec's exit code, or null for a success (3010 and
    /// 1641: a restart is needed or under way).</summary>
    private static string? InstallError(int code) => code switch
    {
        0 or 3010 or 1641 => null,
        1602 => L.Get("Update_Cancelled"),
        1618 => L.Get("Update_Busy"),
        _ => L.Format("Update_InstallFailed", $"msiexec {code}"),
    };

    /// <summary>Started by the install command after a failed update: say so once Clawd is up.</summary>
    private bool ReportFailedUpdate()
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, UpdateFailedArgument);
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out var code) || InstallError(code) is not { } message) return false;
        Log.Error($"update: msiexec exited with {code} after Clawd quit");
        After(2, () => Say(message, SignSymbol.Warning, 8));
        return true;
    }

    private void InstallerExited(Process process)
    {
        var code = process.ExitCode;
        process.Dispose();
        // Clawd has normally been quit long before a successful install ends.
        if (InstallError(code) is not { } message) { Log.Debug(() => $"update: msiexec finished ({code})"); return; }
        Log.Error($"update: msiexec exited with {code}");
        Updates?.InstallFailed(message);
        Say(message, SignSymbol.Warning, 6);
    }
}
