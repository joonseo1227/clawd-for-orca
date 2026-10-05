using System.Diagnostics;

namespace Clawd.Core.Orca;

/// <summary>Runs the orca CLI, the fallback for when the runtime pipe can't be reached.</summary>
public static class CommandLine
{
    /// <summary>Runs a program and returns its standard output, or null when it fails, can't start or
    /// runs past <paramref name="timeout"/> (then it is stopped, so a hung CLI never piles up).</summary>
    public static async Task<byte[]?> RunAsync(string path, IEnumerable<string> args, TimeSpan? timeout = null, CancellationToken cancel = default)
    {
        var info = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            // orca.exe is a console program; without this every poll would flash a console window.
            CreateNoWindow = true,
        };
        foreach (var a in args) info.ArgumentList.Add(a);

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) return null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
        process.StandardInput.Close();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        try
        {
            using var output = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(output, deadline.Token);
            var drain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            await Task.WhenAll(copy, drain, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            return process.ExitCode == 0 ? output.ToArray() : null;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            Log.Error($"{Path.GetFileName(path)} {args.FirstOrDefault()} timed out");
            return null;
        }
    }
}
