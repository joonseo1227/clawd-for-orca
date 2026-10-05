using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clawd.Core;

/// <summary>Loose reads from JSON the way the Mac app's `as? String` casts behave: a missing key or
/// a value of another type is simply absent.</summary>
public static class Json
{
    public static JsonObject? ParseObject(ReadOnlySpan<byte> utf8)
    {
        try { return JsonNode.Parse(utf8) as JsonObject; }
        catch (JsonException) { return null; }
    }

    public static JsonObject? ParseObject(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return null; }
    }

    public static string? Str(this JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    public static double? Num(this JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;

    public static int? Int(this JsonObject? o, string key) => o.Num(key) is { } d && d == Math.Floor(d) ? (int)d : null;

    public static bool? Bool(this JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValue<bool>() : null;

    public static JsonObject? Obj(this JsonObject? o, string key) => o?[key] as JsonObject;

    public static IEnumerable<JsonObject> Objects(this JsonObject? o, string key) =>
        (o?[key] as JsonArray)?.OfType<JsonObject>() ?? [];

    public static IEnumerable<string> Strings(this JsonObject? o, string key) =>
        (o?[key] as JsonArray)?.OfType<JsonValue>().Where(v => v.GetValueKind() == JsonValueKind.String)
            .Select(v => v.GetValue<string>()) ?? [];
}
