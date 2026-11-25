using System.Text.Json;

namespace LlmPlayer.Core.Bridge;

public sealed record CommandSpec(string Method, Func<JsonElement, object?> Handler, bool IsAction, bool RequiresWorld);

/// <summary>
/// Method name → handler table. Pure dispatch logic; the mod's CommandRouter wraps one instance
/// and installs a Guard for world/control checks.
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, CommandSpec> _commands = new(StringComparer.Ordinal);

    /// <summary>Runs before every handler; throw CommandError to reject (e.g. not_in_world, user_has_control).</summary>
    public Action<CommandSpec>? Guard { get; set; }

    public void Register(string method, Func<JsonElement, object?> handler, bool isAction, bool requiresWorld = true)
    {
        if (_commands.ContainsKey(method))
            throw new ArgumentException($"command '{method}' already registered", nameof(method));
        _commands[method] = new CommandSpec(method, handler, isAction, requiresWorld);
    }

    public void Clear() => _commands.Clear();

    public IReadOnlyCollection<string> Methods => _commands.Keys;

    /// <summary>Runs the handler. Throws CommandError for unknown_method, guard rejections and handler errors.</summary>
    public object? Dispatch(string method, JsonElement @params)
    {
        if (!_commands.TryGetValue(method, out var spec))
            throw new CommandError("unknown_method", $"unknown method '{method}'");
        Guard?.Invoke(spec);
        return spec.Handler(@params);
    }

    /// <summary>Dispatch and render the response line. Never throws.</summary>
    public string Handle(int id, string method, JsonElement @params)
    {
        try
        {
            return WireProtocol.Result(id, Dispatch(method, @params));
        }
        catch (CommandError e)
        {
            return WireProtocol.Error(id, e.Code, e.Message);
        }
        catch (Exception e)
        {
            return WireProtocol.Error(id, "internal", $"{e.GetType().Name}: {e.Message}");
        }
    }
}

/// <summary>Typed param accessors that throw CommandError("bad_params") with a readable message.
/// A JSON null counts as missing.</summary>
public static class Params
{
    public static bool Has(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    private static bool TryGet(JsonElement p, string name, out JsonElement v)
    {
        v = default;
        return p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out v) && v.ValueKind != JsonValueKind.Null;
    }

    private static CommandError Missing(string name) => new("bad_params", $"missing required param '{name}'");
    private static CommandError Wrong(string name, string type) => new("bad_params", $"param '{name}' must be {type}");

    public static int Int(JsonElement p, string name) =>
        TryGet(p, name, out var v) ? AsInt(v, name) : throw Missing(name);

    public static int Int(JsonElement p, string name, int fallback) =>
        TryGet(p, name, out var v) ? AsInt(v, name) : fallback;

    private static int AsInt(JsonElement v, string name) =>
        v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : throw Wrong(name, "an integer");

    public static float Float(JsonElement p, string name, float fallback) =>
        TryGet(p, name, out var v) ? AsFloat(v, name) : fallback;

    public static float Float(JsonElement p, string name) =>
        TryGet(p, name, out var v) ? AsFloat(v, name) : throw Missing(name);

    private static float AsFloat(JsonElement v, string name) =>
        v.ValueKind == JsonValueKind.Number ? v.GetSingle() : throw Wrong(name, "a number");

    public static bool Bool(JsonElement p, string name, bool fallback) =>
        TryGet(p, name, out var v) ? AsBool(v, name) : fallback;

    public static bool Bool(JsonElement p, string name) =>
        TryGet(p, name, out var v) ? AsBool(v, name) : throw Missing(name);

    private static bool AsBool(JsonElement v, string name) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw Wrong(name, "a boolean"),
    };

    public static string String(JsonElement p, string name) =>
        StringOrNull(p, name) ?? throw Missing(name);

    public static string? StringOrNull(JsonElement p, string name) =>
        !TryGet(p, name, out var v) ? null
        : v.ValueKind == JsonValueKind.String ? v.GetString()
        : throw Wrong(name, "a string");
}
