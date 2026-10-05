using System.Text;
using System.Text.Json.Nodes;

namespace Clawd.Core.Orca;

/// <summary>
/// A live terminal stream needs Orca's paired WebSocket transport. The user creates a pairing
/// code once in Orca ("Orca Mobile" → Copy pairing code); Clawd keeps it in the Windows
/// Credential Manager since its device token can drive the user's terminals.
/// </summary>
public sealed record OrcaPairing(string Endpoint, string DeviceToken, string PublicKeyB64)
{
    /// <summary>Accepts `orca://pair?code=…` or the bare base64url payload.</summary>
    public static OrcaPairing? FromCode(string raw)
    {
        var code = raw.Trim();
        if (code.StartsWith("orca:", StringComparison.OrdinalIgnoreCase) && QueryValue(code, "code") is { } value) code = value;
        var b64 = code.Replace('-', '+').Replace('_', '/');
        while (b64.Length % 4 != 0) b64 += "=";
        // Convert.TryFromBase64String skips whitespace; the code never contains any, so reject it.
        if (b64.Any(char.IsWhiteSpace)) return null;
        var buffer = new byte[b64.Length];
        return Convert.TryFromBase64String(b64, buffer, out var written) ? FromJson(buffer.AsSpan(0, written)) : null;
    }

    public static OrcaPairing? FromJson(ReadOnlySpan<byte> utf8)
    {
        var o = Json.ParseObject(utf8);
        return o.Str("endpoint") is { } endpoint && o.Str("deviceToken") is { } token && o.Str("publicKeyB64") is { } key
            ? new OrcaPairing(endpoint, token, key)
            : null;
    }

    public byte[] ToJson() => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["endpoint"] = Endpoint,
        ["deviceToken"] = DeviceToken,
        ["publicKeyB64"] = PublicKeyB64,
    }.ToJsonString());

    /// <summary>Orca runs on this PC, so talk to it over loopback: the LAN address in the code changes
    /// whenever the network does. Port, path and query stay as they are.</summary>
    public string LocalEndpoint
    {
        get
        {
            var sep = Endpoint.IndexOf("://", StringComparison.Ordinal);
            if (sep < 0) return Endpoint;
            var scheme = Endpoint[..sep].ToLowerInvariant();
            if (scheme is not ("ws" or "wss")) return Endpoint;
            var start = sep + 3;
            var end = Endpoint.IndexOfAny(['/', '?', '#'], start);
            if (end < 0) end = Endpoint.Length;
            var authority = Endpoint[start..end];
            var at = authority.LastIndexOf('@');
            var hostPort = authority[(at + 1)..];
            // An IPv6 literal keeps its colons inside brackets; the port follows the bracket.
            var portColon = hostPort.StartsWith('[') ? hostPort.IndexOf("]:", StringComparison.Ordinal) + 1 : hostPort.LastIndexOf(':');
            var port = portColon > 0 ? hostPort[portColon..] : "";
            return Endpoint[..start] + authority[..(at + 1)] + "127.0.0.1" + port + Endpoint[end..];
        }
    }

    private static string? QueryValue(string url, string name)
    {
        var q = url.IndexOf('?');
        if (q < 0) return null;
        foreach (var pair in url[(q + 1)..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == name) return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }
}

/// <summary>Where the pairing is kept. The app uses the Windows Credential Manager; tests use memory.</summary>
public interface IPairingStore
{
    /// <summary>Whether a pairing is stored, without decoding it.</summary>
    bool Exists();
    OrcaPairing? Load();
    /// <summary>Updates in place, so a failed write never leaves the user with no pairing at all.</summary>
    bool Save(OrcaPairing pairing);
    void Clear();
}

/// <summary>
/// The pairing JSON ({endpoint, deviceToken, publicKeyB64}) kept as a secret: the Windows
/// Credential Manager in the app. The live-terminal bridge reads it only when it starts.
/// </summary>
public interface ICredentialStore
{
    bool Exists();
    /// <summary>The stored pairing JSON, or null.</summary>
    string? Load();
    bool Save(string json);
    void Clear();
}
