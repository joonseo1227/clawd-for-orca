namespace Clawd.Core;

/// <summary>
/// Diagnostics. Quiet by default; with CLAWD_DEBUG set, messages go to stderr and to
/// %LOCALAPPDATA%\Clawd\clawd.log (a WinUI app has no console to read stderr from).
/// </summary>
public static class Log
{
    /// <summary>True with CLAWD_DEBUG set; guards diagnostics that cost something to gather.</summary>
    public static readonly bool Verbose = Environment.GetEnvironmentVariable("CLAWD_DEBUG") is not null;
    private static readonly object Gate = new();

    public static void Debug(Func<string> message)
    {
        if (!Verbose) return;
        Write("debug", message());
    }

    public static void Error(string message) => Write("error", message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level}: {message}";
        System.Diagnostics.Debug.WriteLine(line);
        if (!Verbose && level != "error") return;
        lock (Gate)
        {
            try
            {
                Console.Error.WriteLine(line);
                if (Verbose)
                {
                    Directory.CreateDirectory(AppPaths.DataDirectory);
                    File.AppendAllText(Path.Combine(AppPaths.DataDirectory, "clawd.log"), line + Environment.NewLine);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Where Clawd keeps its own files: settings, the WebView2 profile, the debug log.</summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "Clawd");

    public static string Home { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
