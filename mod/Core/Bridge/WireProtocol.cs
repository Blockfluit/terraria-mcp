using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlmPlayer.Core.Bridge;

/// <summary>A command failure reported to the client as {"error":{"code","message"}}.</summary>
public sealed class CommandError(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Result of parsing one request line. When !Ok, ErrorLine is a ready-to-send error response.</summary>
public readonly record struct ParsedRequest(bool Ok, int? Id, string Method, JsonElement Params, string? ErrorLine);

/// <summary>Newline-delimited JSON wire format (see plan "Wire Protocol"). Pure: no game references.</summary>
public static class WireProtocol
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    public static ParsedRequest ParseRequest(string line)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(line.Trim());
            root = doc.RootElement.Clone();
        }
        catch (JsonException e)
        {
            return Fail(null, $"invalid JSON: {e.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object)
            return Fail(null, "request must be a JSON object");

        if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number || !idEl.TryGetInt32(out var id))
            return Fail(null, "request needs an integer 'id'");

        if (!root.TryGetProperty("method", out var mEl) || mEl.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(mEl.GetString()))
            return Fail(id, "request needs a string 'method'");

        var prms = EmptyObject;
        if (root.TryGetProperty("params", out var pEl) && pEl.ValueKind != JsonValueKind.Null)
        {
            if (pEl.ValueKind != JsonValueKind.Object)
                return Fail(id, "'params' must be an object");
            prms = pEl;
        }

        return new ParsedRequest(true, id, mEl.GetString()!, prms, null);
    }

    private static ParsedRequest Fail(int? id, string message) =>
        new(false, id, "", EmptyObject, Error(id, "bad_params", message));

    public static string Result(int id, object? result)
    {
        var node = new JsonObject
        {
            ["id"] = id,
            ["result"] = ToNode(result) ?? new JsonObject(),
        };
        return node.ToJsonString(Json);
    }

    public static string Error(int? id, string code, string message)
    {
        var node = new JsonObject
        {
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
        return node.ToJsonString(Json);
    }

    /// <summary>{"event":evt, ...payload's properties}. Payload must serialize to an object (or be null).</summary>
    public static string Event(string evt, object? payload)
    {
        var node = new JsonObject { ["event"] = evt };
        if (ToNode(payload) is JsonObject obj)
        {
            foreach (var (k, v) in obj.ToList())
            {
                obj.Remove(k);
                node[k] = v;
            }
        }
        return node.ToJsonString(Json);
    }

    /// <summary>Serialize any message object to a single camelCase JSON line (no trailing newline).</summary>
    public static string Serialize(object message) => JsonSerializer.Serialize(message, message.GetType(), Json);

    private static JsonNode? ToNode(object? value) =>
        value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType(), Json);
}
