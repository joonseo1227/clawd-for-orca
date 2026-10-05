using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Clawd.Core.Orca;

/// <summary>How a runtime request ended.</summary>
public enum RuntimeStatus
{
    Ok,
    /// <summary>Orca isn't running or the request never left: safe to retry another way.</summary>
    Unreachable,
    /// <summary>Orca got the request and refused it, or the answer never came. Not safe to repeat:
    /// a keystroke may already have been typed.</summary>
    Failed,
}

public sealed record RuntimeOutcome(RuntimeStatus Status, JsonObject? Frame = null, string? Code = null, string? Message = null)
{
    public static readonly RuntimeOutcome Unreachable = new(RuntimeStatus.Unreachable);
}

/// <summary>
/// A direct line to the running Orca app: the named pipe (Windows) or unix socket (macOS, Linux)
/// and newline-delimited JSON frames that the orca CLI itself uses (out/cli/runtime/transport.js),
/// minus the Node process the CLI starts for every request. About a millisecond per call instead
/// of a few hundred, which is what lets the terminal view refresh several times a second.
/// </summary>
public sealed class OrcaRuntime(string metadataPath)
{
    public string MetadataPath { get; } = metadataPath;

    /// <summary>Electron's userData for Orca, as the CLI resolves it (cli/runtime/metadata.js).</summary>
    public static string DefaultMetadataPath
    {
        get
        {
            var userData = Environment.GetEnvironmentVariable("ORCA_USER_DATA_PATH");
            if (string.IsNullOrEmpty(userData))
            {
                if (OperatingSystem.IsWindows())
                    userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "orca");
                else if (OperatingSystem.IsMacOS())
                    userData = Path.Combine(AppPaths.Home, "Library", "Application Support", "orca");
                else
                    userData = Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(AppPaths.Home, ".config"), "orca");
            }
            return Path.Combine(userData, "orca-runtime.json");
        }
    }

    public sealed record Transport(string Kind, string Endpoint);
    public sealed record Metadata(Transport Transport, string Token);

    /// <summary>Re-read on every call: the pipe name and token change whenever Orca restarts.</summary>
    public Metadata? ReadMetadata()
    {
        byte[] data;
        try
        {
            using var stream = new FileStream(MetadataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            data = buffer.ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var json = Json.ParseObject(data);
        if (json.Str("authToken") is not { } token) return null;
        // Older Orca builds wrote a single `transport` object instead of the array.
        var transports = json.Objects("transports").ToList();
        if (json.Obj("transport") is { } legacy) transports.Add(legacy);
        foreach (var t in transports)
        {
            if (t.Str("kind") is "named-pipe" or "unix" && t.Str("endpoint") is { } endpoint)
                return new Metadata(new Transport(t.Str("kind")!, endpoint), token);
        }
        return null;
    }

    public async Task<RuntimeOutcome> RequestAsync(string method, JsonObject parameters, TimeSpan? timeout = null, CancellationToken cancel = default)
    {
        if (ReadMetadata() is not { } meta) return RuntimeOutcome.Unreachable;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(2));

        Stream stream;
        try { stream = await ConnectAsync(meta.Transport, deadline.Token).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or SocketException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or PlatformNotSupportedException or ArgumentException)
        {
            cancel.ThrowIfCancellationRequested();
            return RuntimeOutcome.Unreachable;
        }

        await using (stream.ConfigureAwait(false))
        {
            var id = Guid.NewGuid().ToString("D");
            var request = new JsonObject { ["id"] = id, ["authToken"] = meta.Token, ["method"] = method, ["params"] = parameters };
            try
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(request.ToJsonString() + "\n"), deadline.Token).ConfigureAwait(false);
                await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                cancel.ThrowIfCancellationRequested();
                return RuntimeOutcome.Unreachable;
            }

            // Frames are newline-terminated; keepalive frames may precede the answer.
            var buffer = new MemoryStream();
            var chunk = new byte[65536];
            try
            {
                while (true)
                {
                    var n = await stream.ReadAsync(chunk, deadline.Token).ConfigureAwait(false);
                    if (n <= 0) return new RuntimeOutcome(RuntimeStatus.Failed, Code: "no_response");
                    buffer.Write(chunk, 0, n);
                    while (TakeLine(buffer) is { } line)
                    {
                        if (Json.ParseObject(line) is not { } frame || frame.ContainsKey("_keepalive")) continue;
                        if (frame.Str("id") != id) continue;
                        if (frame.Bool("ok") == true) return new RuntimeOutcome(RuntimeStatus.Ok, frame);
                        var error = frame.Obj("error");
                        return new RuntimeOutcome(RuntimeStatus.Failed, frame, error.Str("code"), error.Str("message"));
                    }
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                cancel.ThrowIfCancellationRequested();
                return new RuntimeOutcome(RuntimeStatus.Failed, Code: "no_response");
            }
        }
    }

    private static async Task<Stream> ConnectAsync(Transport transport, CancellationToken cancel)
    {
        if (transport.Kind == "named-pipe")
        {
            // "\\.\pipe\orca-<pid>-<id>": the client wants just the name after the prefix.
            const string prefix = @"\\.\pipe\";
            var name = transport.Endpoint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? transport.Endpoint[prefix.Length..] : transport.Endpoint;
            // A pipe that is gone (Orca crashed and left its runtime file behind) would be retried
            // until the timeout, two seconds on every request; a socket that is gone fails at once.
            if (OperatingSystem.IsWindows() && !PipeExists(name)) throw new IOException($"no pipe named {name}");
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(cancel).ConfigureAwait(false);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(transport.Endpoint), cancel).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Whether a named pipe is open, from the pipe namespace's listing; looking it up by
    /// name would connect to it. True when the listing can't be read: the connect decides.</summary>
    private static bool PipeExists(string name)
    {
        try { return Directory.EnumerateFiles(@"\\.\pipe\", name).Any(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return true; }
    }

    /// <summary>Removes and returns the first complete line, or null when none has arrived yet.</summary>
    private static byte[]? TakeLine(MemoryStream buffer)
    {
        var bytes = buffer.GetBuffer();
        var length = (int)buffer.Length;
        var newline = Array.IndexOf(bytes, (byte)'\n', 0, length);
        if (newline < 0) return null;
        var line = bytes[..newline];
        var rest = bytes[(newline + 1)..length];
        buffer.SetLength(0);
        buffer.Write(rest);
        return line;
    }

    /// <summary>The `result` object of a successful answer.</summary>
    public static JsonObject? Result(JsonObject? frame) => frame.Obj("result");
}
