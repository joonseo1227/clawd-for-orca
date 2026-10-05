using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Clawd;

public static class Program
{
    /// <summary>Asks the running Clawd to quit, as its Quit menu item does. The installer runs
    /// <c>Clawd.exe --quit</c> before it replaces or removes files.</summary>
    public const string QuitArgument = "--quit";

    public static bool IsQuit(string? arguments) =>
        arguments?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(a => a.Trim('"') == QuitArgument) == true;

    /// <summary>One Clawd per user: a second launch (from the Start menu, or a toast clicked while
    /// Clawd runs) hands its activation to the running copy and exits.</summary>
    [STAThread]
    private static int Main(string[] arguments)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var quit = arguments.Contains(QuitArgument);
        var instance = AppInstance.FindOrRegisterForKey("clawd-for-orca");
        if (!instance.IsCurrent)
        {
            var args = AppInstance.GetCurrent().GetActivatedEventArgs();
            instance.RedirectActivationToAsync(args).AsTask().Wait(TimeSpan.FromSeconds(5));
            if (quit) WaitForExit(instance.ProcessId);
            return 0;
        }
        if (quit) return 0;   // no Clawd running
        if (Clawd.Core.TestHooks.UiLanguage is { } lang)
        {
            // Every thread, including the thread pool's, so Core and the WinUI half agree.
            var culture = System.Globalization.CultureInfo.GetCultureInfo(lang == "ko" ? "ko-KR" : "en-US");
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
            System.Globalization.CultureInfo.CurrentUICulture = culture;
        }
        L.Initialize();
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    /// <summary>Returns once the running Clawd has exited, so the installer doesn't find its files in use.</summary>
    private static void WaitForExit(uint processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }
}
