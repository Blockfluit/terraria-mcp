using System.Globalization;
using System.Text.Json;
using LlmPlayer.Core.Bridge;

namespace LlmPlayer.Core.Control;

public enum TileOpKind { Clear, Tile, Liquid }

/// <summary>One <c>place_tiles</c> entry of dev_setup, normalized (X1 ≤ X2, Y1 ≤ Y2). <see cref="Name"/>: the tile
/// name (Tile) or water/lava/honey (Liquid).</summary>
public sealed record TileOp(TileOpKind Kind, int X1, int Y1, int X2, int Y2, string? Name)
{
    public long Count => (long)(X2 - X1 + 1) * (Y2 - Y1 + 1);
}

public sealed record ItemSpec(string Name, int Count);

public sealed record SpawnSpec(string Npc, int X, int Y);

/// <summary>
/// Parsing and validation for the test-only <c>dev_setup</c> command (docs/FAST-DEV.md). Pure: the mod applies it.
/// <code>
/// {"x":..,"y":..,                      teleport (feet-left standing tile, like get_state)
///  "clear_inventory":true, "items":[{"name":"Wooden Bow","count":1}],
///  "life":100, "time":"noon"|"dusk"|"midnight"|"dawn"|"HH:MM", "clear_hostiles":true, "clear_items":true,
///  "place_tiles":[{"x1":..,"y1":..,"x2":..,"y2":..,"clear":true} | {..,"tile":"Stone"} | {..,"liquid":"water"}],
///  "spawn":[{"npc":"King Slime","x":..,"y":..}]}
/// </code>
/// </summary>
public static class DevSetup
{
    public const long MaxTiles = 40_000;
    public const int MaxItems = 40, MaxSpawns = 20, MaxStack = 9999;
    public static readonly string[] Liquids = { "water", "lava", "honey", "shimmer" };

    public static List<TileOp> ParseTiles(JsonElement p)
    {
        var ops = new List<TileOp>();
        if (!Params.Has(p, "place_tiles")) return ops;
        var arr = p.GetProperty("place_tiles");
        if (arr.ValueKind != JsonValueKind.Array) throw Bad("place_tiles must be an array");
        long total = 0;
        int i = 0;
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) throw Bad($"place_tiles[{i}] must be an object");
            int x1 = Params.Int(e, "x1"), y1 = Params.Int(e, "y1");
            int x2 = Params.Int(e, "x2", x1), y2 = Params.Int(e, "y2", y1);
            if (x2 < x1) (x1, x2) = (x2, x1);
            if (y2 < y1) (y1, y2) = (y2, y1);
            bool clear = Params.Bool(e, "clear", false);
            string? tile = Params.Has(e, "tile") ? Params.String(e, "tile") : null;
            string? liquid = Params.Has(e, "liquid") ? Params.String(e, "liquid").Trim().ToLowerInvariant() : null;
            int kinds = (clear ? 1 : 0) + (tile != null ? 1 : 0) + (liquid != null ? 1 : 0);
            if (kinds != 1) throw Bad($"place_tiles[{i}] needs exactly one of clear: true, tile or liquid");
            TileOp op;
            if (clear) op = new TileOp(TileOpKind.Clear, x1, y1, x2, y2, null);
            else if (tile != null)
            {
                if (tile.Trim().Length == 0) throw Bad($"place_tiles[{i}].tile is empty");
                op = new TileOp(TileOpKind.Tile, x1, y1, x2, y2, tile.Trim());
            }
            else
            {
                if (Array.IndexOf(Liquids, liquid) < 0) throw Bad($"place_tiles[{i}].liquid must be one of {string.Join(", ", Liquids)}");
                op = new TileOp(TileOpKind.Liquid, x1, y1, x2, y2, liquid);
            }
            total += op.Count;
            if (total > MaxTiles) throw Bad($"place_tiles covers more than {MaxTiles} tiles");
            ops.Add(op);
            i++;
        }
        return ops;
    }

    public static List<ItemSpec> ParseItems(JsonElement p)
    {
        var list = new List<ItemSpec>();
        if (!Params.Has(p, "items")) return list;
        var arr = p.GetProperty("items");
        if (arr.ValueKind != JsonValueKind.Array) throw Bad("items must be an array");
        foreach (var e in arr.EnumerateArray())
        {
            var name = Params.String(e, "name").Trim();
            int count = Params.Int(e, "count", 1);
            if (name.Length == 0) throw Bad("items: empty name");
            if (count < 1 || count > MaxStack) throw Bad($"items: count must be 1..{MaxStack}");
            list.Add(new ItemSpec(name, count));
        }
        if (list.Count > MaxItems) throw Bad($"at most {MaxItems} items");
        return list;
    }

    public static List<SpawnSpec> ParseSpawns(JsonElement p)
    {
        var list = new List<SpawnSpec>();
        if (!Params.Has(p, "spawn")) return list;
        var arr = p.GetProperty("spawn");
        if (arr.ValueKind != JsonValueKind.Array) throw Bad("spawn must be an array");
        foreach (var e in arr.EnumerateArray())
            list.Add(new SpawnSpec(Params.String(e, "npc").Trim(), Params.Int(e, "x"), Params.Int(e, "y")));
        if (list.Count > MaxSpawns) throw Bad($"at most {MaxSpawns} spawns");
        return list;
    }

    /// <summary>"dawn" (4:30 AM), "noon", "dusk" (7:30 PM), "midnight" or "H:MM" (24 h) → Main.dayTime / Main.time.</summary>
    public static bool TryParseTime(string s, out bool dayTime, out double time)
    {
        dayTime = true;
        time = 0;
        int minutes;
        switch (s.Trim().ToLowerInvariant())
        {
            case "dawn": minutes = 4 * 60 + 30; break;
            case "noon": minutes = 12 * 60; break;
            case "dusk": minutes = 19 * 60 + 30; break;
            case "midnight": minutes = 0; break;
            default:
                var parts = s.Trim().Split(':');
                if (parts.Length != 2
                    || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
                    || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m)
                    || h > 23 || m > 59)
                    return false;
                minutes = h * 60 + m;
                break;
        }
        // Game clock: day starts 4:30 AM and lasts 54000 ticks (until 7:30 PM); 1 game minute = 60 ticks.
        int sinceDawn = ((minutes - (4 * 60 + 30)) % 1440 + 1440) % 1440;
        double ticks = sinceDawn * 60.0;
        if (ticks < 54000) { dayTime = true; time = ticks; }
        else { dayTime = false; time = ticks - 54000; }
        return true;
    }

    private static CommandError Bad(string msg) => new("bad_params", msg);
}
