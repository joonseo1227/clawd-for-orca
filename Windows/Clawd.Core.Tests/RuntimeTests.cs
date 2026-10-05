using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Clawd.Core.Orca;

namespace Clawd.Core.Tests;

/// <summary>A stand-in for Orca's runtime: answers one connection with the frames a test scripts.</summary>
internal sealed class FakeRuntime : IAsyncDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "clawd-rt-" + Guid.NewGuid().ToString("N")[..8]);
    public string MetadataPath => Path.Combine(Dir, "orca-runtime.json");
    public JsonObject? LastRequest { get; private set; }
    private readonly Task _serve;
    private readonly CancellationTokenSource _stop = new();

    /// <param name="pipe">A named pipe, as on Windows; otherwise a unix socket, as on macOS.</param>
    /// <param name="answer">Frames to send for a request; null closes the connection without answering.</param>
    public FakeRuntime(bool pipe, Func<JsonObject, IEnumerable<string>?> answer)
    {
        Directory.CreateDirectory(Dir);
        var name = "clawd-test-" + Guid.NewGuid().ToString("N")[..8];
        var endpoint = pipe ? @"\\.\pipe\" + name : Path.Combine(Dir, "o.sock");
        File.WriteAllText(MetadataPath, new JsonObject
        {
            ["authToken"] = "secret",
            ["transports"] = new JsonArray(new JsonObject { ["kind"] = pipe ? "named-pipe" : "unix", ["endpoint"] = endpoint }),
        }.ToJsonString());
        _serve = pipe ? ServePipe(name, answer) : ServeSocket(endpoint, answer);
    }

    private async Task ServePipe(string name, Func<JsonObject, IEnumerable<string>?> answer)
    {
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync(_stop.Token);
        await Converse(server, answer);
    }

    private async Task ServeSocket(string path, Func<JsonObject, IEnumerable<string>?> answer)
    {
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        using var client = await listener.AcceptAsync(_stop.Token);
        await using var stream = new NetworkStream(client, ownsSocket: false);
        await Converse(stream, answer);
    }

    private async Task Converse(Stream stream, Func<JsonObject, IEnumerable<string>?> answer)
    {
        var reader = new StreamReader(stream, Encoding.UTF8);
        var line = await reader.ReadLineAsync(_stop.Token);
        LastRequest = Json.ParseObject(line);
        if (answer(LastRequest!) is not { } frames) return;
        foreach (var frame in frames)
        {
            // Split each frame in two writes: the client must reassemble lines across reads.
            var bytes = Encoding.UTF8.GetBytes(frame + "\n");
            await stream.WriteAsync(bytes.AsMemory(0, bytes.Length / 2));
            await stream.FlushAsync();
            await stream.WriteAsync(bytes.AsMemory(bytes.Length / 2));
            await stream.FlushAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _serve; } catch (Exception e) when (e is OperationCanceledException or IOException) { }
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}

public class RuntimeTests
{
    private static string Ok(JsonObject request, JsonObject result) =>
        new JsonObject { ["id"] = request.Str("id"), ["ok"] = true, ["result"] = result }.ToJsonString();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnswersAfterKeepalivesAndOtherIds(bool pipe)
    {
        await using var fake = new FakeRuntime(pipe, req =>
        [
            "{\"_keepalive\":true}",
            new JsonObject { ["id"] = "someone-else", ["ok"] = true }.ToJsonString(),
            Ok(req, new JsonObject { ["worktrees"] = new JsonArray() }),
        ]);
        var outcome = await new OrcaRuntime(fake.MetadataPath).RequestAsync("worktree.ps", new JsonObject { ["limit"] = 5 });
        Assert.Equal(RuntimeStatus.Ok, outcome.Status);
        Assert.IsType<JsonArray>(OrcaRuntime.Result(outcome.Frame)?["worktrees"]);
        Assert.Equal("secret", fake.LastRequest.Str("authToken"));
        Assert.Equal("worktree.ps", fake.LastRequest.Str("method"));
        Assert.Equal(5, fake.LastRequest.Obj("params").Int("limit"));
    }

    [Fact]
    public async Task RefusalIsFailedWithItsCode()
    {
        await using var fake = new FakeRuntime(pipe: true, req =>
            [new JsonObject { ["id"] = req.Str("id"), ["ok"] = false, ["error"] = new JsonObject { ["code"] = "no_connected_pty", ["message"] = "gone" } }.ToJsonString()]);
        var outcome = await new OrcaRuntime(fake.MetadataPath).RequestAsync("terminal.send", new JsonObject());
        Assert.Equal(RuntimeStatus.Failed, outcome.Status);
        Assert.Equal("no_connected_pty", outcome.Code);
        Assert.Equal("gone", outcome.Message);
    }

    [Fact]
    public async Task HangUpAfterTheRequestIsFailedNotUnreachable()
    {
        // Orca may already have acted on it: retrying elsewhere could type a key twice.
        await using var fake = new FakeRuntime(pipe: true, _ => null);
        var outcome = await new OrcaRuntime(fake.MetadataPath).RequestAsync("terminal.send", new JsonObject());
        Assert.Equal(RuntimeStatus.Failed, outcome.Status);
        Assert.Equal("no_response", outcome.Code);
    }

    [Fact]
    public async Task MissingMetadataOrEndpointIsUnreachable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clawd-rt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "orca-runtime.json");
            Assert.Equal(RuntimeStatus.Unreachable, (await new OrcaRuntime(path).RequestAsync("worktree.ps", new JsonObject())).Status);
            File.WriteAllText(path, """{"authToken":"t","transports":[{"kind":"named-pipe","endpoint":"\\\\.\\pipe\\clawd-nobody-here"}]}""");
            var outcome = await new OrcaRuntime(path).RequestAsync("worktree.ps", new JsonObject(), TimeSpan.FromMilliseconds(300));
            Assert.Equal(RuntimeStatus.Unreachable, outcome.Status);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task LeftoverRuntimeFileFailsAtOnce()
    {
        // Orca crashed and left its runtime file: the pipe it names is gone. Every request would
        // otherwise wait out its timeout before the CLI fallback or the next poll.
        if (!OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "clawd-rt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "orca-runtime.json");
            File.WriteAllText(path, """{"authToken":"t","transports":[{"kind":"named-pipe","endpoint":"\\\\.\\pipe\\clawd-nobody-here"}]}""");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var outcome = await new OrcaRuntime(path).RequestAsync("worktree.ps", new JsonObject(), TimeSpan.FromSeconds(5));
            Assert.Equal(RuntimeStatus.Unreachable, outcome.Status);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.Elapsed}");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ReadsLegacySingleTransport()
    {
        var path = Path.Combine(Path.GetTempPath(), "clawd-meta-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(path, """{"authToken":"t","transport":{"kind":"unix","endpoint":"/tmp/o.sock"}}""");
        try
        {
            Assert.Equal(new OrcaRuntime.Transport("unix", "/tmp/o.sock"), new OrcaRuntime(path).ReadMetadata()?.Transport);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DefaultMetadataFollowsElectronUserData()
    {
        var path = OrcaRuntime.DefaultMetadataPath;
        Assert.EndsWith(Path.Combine("orca", "orca-runtime.json"), path);
    }
}

public class ClientFallbackTests
{
    /// <summary>A stand-in orca CLI that records its arguments, prints a terminal list for `terminal
    /// list` and, like the real one without --json, a line of text for anything else.</summary>
    private static string FakeCli(string dir)
    {
        var log = Path.Combine(dir, "calls.txt");
        var json = """{"ok":true,"result":{"terminals":[{"tabId":"t","leafId":"l","handle":"term_cli","title":"✳ Fix it"},{"tabId":"cli","leafId":"send","handle":"term_send","title":"Send"}]}}""";
        if (OperatingSystem.IsWindows())
        {
            // CreateProcess runs a batch file through cmd.exe; it prints in the console's code page.
            var batch = Path.Combine(dir, "orca.cmd");
            File.WriteAllText(batch, $"@echo off\r\nchcp 65001 >nul\r\necho %*>>\"{log}\"\r\nif \"%2\"==\"list\" (echo {json}) else (echo Sent input to the terminal.)\r\n");
            return batch;
        }
        var script = Path.Combine(dir, "orca");
        File.WriteAllText(script, $"#!/bin/sh\necho \"$@\" >> '{log}'\nif [ \"$2\" = list ]; then echo '{json}'; else echo 'Sent input to the terminal.'; fi\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    [Fact]
    public async Task CliSendCountsACleanExitAsSent()
    {
        var dir = Directory.CreateTempSubdirectory("clawd-cli-").FullName;
        try
        {
            var client = new OrcaClient(new OrcaRuntime(Path.Combine(dir, "none.json")), FakeCli(dir));
            var agent = new OrcaAgent("cli:send", "agent", "waiting", null, null, null, null, null, false);
            Assert.True(await client.SendAsync("yes", agent, enter: true));
            Assert.Contains("terminal send --terminal term_send --text=yes --enter", File.ReadAllText(Path.Combine(dir, "calls.txt")));
        }
        finally
        {
            OrcaClient.ForgetHandle(new OrcaAgent("cli:send", "", "", null, null, null, null, null, false));
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task UnreachableRuntimeFallsBackToTheCli()
    {
        var dir = Directory.CreateTempSubdirectory("clawd-cli-").FullName;
        try
        {
            var client = new OrcaClient(new OrcaRuntime(Path.Combine(dir, "none.json")), FakeCli(dir));
            var titles = await client.TitlesAsync();
            Assert.Equal("Fix it", titles["t:l"]);
            Assert.Contains("terminal list --json", File.ReadAllText(Path.Combine(dir, "calls.txt")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task RefusedRequestNeverFallsBack()
    {
        var dir = Directory.CreateTempSubdirectory("clawd-cli-").FullName;
        try
        {
            var calls = 0;
            await using var fake = new FakeRuntime(pipe: true, req =>
            {
                calls++;
                return [new JsonObject { ["id"] = req.Str("id"), ["ok"] = false, ["error"] = new JsonObject { ["code"] = "boom" } }.ToJsonString()];
            });
            var client = new OrcaClient(new OrcaRuntime(fake.MetadataPath), FakeCli(dir));
            Assert.Empty(await client.TerminalsAsync());
            Assert.Equal(1, calls);
            Assert.False(File.Exists(Path.Combine(dir, "calls.txt")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
