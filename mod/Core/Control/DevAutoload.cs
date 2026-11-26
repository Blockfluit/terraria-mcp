using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlmPlayer.Core.Control;

/// <summary>
/// One-shot "enter this player and world on the next launch" request for the developer reload loop
/// (<c>scripts/dev-reload.sh</c>). Stored as <c>LlmPlayerAutoload.json</c> in tModLoader's save folder:
/// <code>{"player":"Name","world":"Name","playerFile":"Name.plr","worldFile":"Name.wld","createdUtc":"2026-11-26T10:00:00Z"}</code>
/// <c>player</c> and <c>world</c> are required; the file names disambiguate duplicate names; <c>createdUtc</c>, when
/// present, makes the request expire (<see cref="MaxAge"/>) so a forgotten file never enters a world days later.
/// Pure logic only: the mod does the file IO and the menu calls.
/// </summary>
public sealed record AutoloadRequest(string Player, string World, string? PlayerFile = null, string? WorldFile = null,
    DateTime? CreatedUtc = null);

/// <summary>A save-list entry: its display name and its file name (e.g. "Hero.plr").</summary>
public readonly record struct SaveEntry(string Name, string FileName);

public static class DevAutoload
{
    public const string FileName = "LlmPlayerAutoload.json";

    /// <summary>Requests older than this are ignored (the script writes/refreshes the file right before launching).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    /// <summary>Parse the file's text. Returns null and sets <paramref name="error"/> when it isn't a valid request.</summary>
    public static AutoloadRequest? Parse(string json, out string error)
    {
        error = "";
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException e)
        {
            error = $"not valid JSON: {e.Message}";
            return null;
        }
        if (root is not JsonObject obj)
        {
            error = "expected a JSON object {\"player\": ..., \"world\": ...}";
            return null;
        }

        string? Str(string key, bool required, ref string err)
        {
            if (!obj.TryGetPropertyValue(key, out var n) || n == null)
            {
                if (required && err == "") err = $"missing \"{key}\"";
                return null;
            }
            if (n is not JsonValue v || !v.TryGetValue<string>(out var s))
            {
                if (err == "") err = $"\"{key}\" must be a string";
                return null;
            }
            s = s.Trim();
            if (s.Length == 0)
            {
                if (required && err == "") err = $"\"{key}\" is empty";
                return null;
            }
            return s;
        }

        var player = Str("player", true, ref error);
        var world = Str("world", true, ref error);
        var playerFile = Str("playerFile", false, ref error);
        var worldFile = Str("worldFile", false, ref error);
        var created = Str("createdUtc", false, ref error);
        if (error != "") return null;

        DateTime? createdUtc = null;
        if (created != null)
        {
            if (!DateTime.TryParse(created, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t))
            {
                error = $"\"createdUtc\" is not a date: {created}";
                return null;
            }
            createdUtc = DateTime.SpecifyKind(t, DateTimeKind.Utc);
        }
        return new AutoloadRequest(player!, world!, playerFile, worldFile, createdUtc);
    }

    public static string Serialize(AutoloadRequest r)
    {
        var obj = new JsonObject { ["player"] = r.Player, ["world"] = r.World };
        if (r.PlayerFile != null) obj["playerFile"] = r.PlayerFile;
        if (r.WorldFile != null) obj["worldFile"] = r.WorldFile;
        if (r.CreatedUtc is { } t)
            obj["createdUtc"] = t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return obj.ToJsonString();
    }

    /// <summary>False when the request carries a timestamp older than <see cref="MaxAge"/> (or more than a minute in the future).</summary>
    public static bool IsFresh(AutoloadRequest r, DateTime nowUtc) =>
        r.CreatedUtc is not { } t || (nowUtc - t <= MaxAge && t - nowUtc <= TimeSpan.FromMinutes(1));

    /// <summary>
    /// Pick the save to load. Order: the exact file name when given (case-insensitive: Windows paths), else the
    /// exact display name, else a case-insensitive display name. Returns the index, or -1 with <paramref name="error"/>
    /// set when nothing matches or the match is ambiguous (two saves with the same name and no file name to tell them apart).
    /// </summary>
    public static int Pick(IReadOnlyList<SaveEntry> saves, string name, string? fileName, string kind, out string error)
    {
        error = "";
        if (fileName != null)
        {
            var byFile = Indexes(saves, s => string.Equals(s.FileName, fileName, StringComparison.OrdinalIgnoreCase));
            if (byFile.Count == 1) return byFile[0];
        }

        var exact = Indexes(saves, s => string.Equals(s.Name, name, StringComparison.Ordinal));
        var matches = exact.Count > 0 ? exact
            : Indexes(saves, s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (matches.Count == 1) return matches[0];

        error = matches.Count == 0
            ? $"no {kind} named \"{name}\"" + (saves.Count == 0 ? "" : $" (have: {string.Join(", ", saves.Select(s => s.Name))})")
            : $"{matches.Count} {kind}s are named \"{name}\"; add \"{kind}File\" to pick one";
        return -1;
    }

    /// <summary>The menus refuse a Journey character in a non-Journey world and vice versa. Null when allowed.</summary>
    public static string? GameModeMismatch(bool playerIsJourney, bool worldIsJourney) =>
        playerIsJourney == worldIsJourney ? null
        : playerIsJourney ? "a Journey mode character can't enter a non-Journey world"
        : "only Journey mode characters can enter a Journey world";

    private static List<int> Indexes(IReadOnlyList<SaveEntry> saves, Func<SaveEntry, bool> pred)
    {
        var list = new List<int>();
        for (var i = 0; i < saves.Count; i++)
            if (pred(saves[i])) list.Add(i);
        return list;
    }
}
