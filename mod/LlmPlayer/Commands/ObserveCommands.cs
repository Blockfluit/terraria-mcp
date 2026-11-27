using LlmPlayer.Bridge;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Map;
using LlmPlayer.Observe;
using Terraria;

namespace LlmPlayer.Commands;

/// <summary>Observation commands (always allowed while in a world; never need control).</summary>
[CommandGroup]
public static class ObserveCommands
{
    private static readonly Dictionary<char, string> MarkerNames = new()
    {
        ['E'] = "hostile npc",
        ['N'] = "town npc",
        ['i'] = "dropped item",
    };

    public static void Register()
    {
        CommandRouter.Register("get_state", _ => StateReader.Read(), isAction: false);

        // Open or close a door in interaction reach, like a right-click (vanilla WorldGen.OpenDoor/CloseDoor path).
        CommandRouter.Register("door", p =>
        {
            int x = Params.Int(p, "x"), y = Params.Int(p, "y");
            string action = (Params.Has(p, "action") ? p.GetProperty("action").GetString() : "close")?.Trim().ToLowerInvariant() ?? "close";
            var player = Main.LocalPlayer;
            if (!WorldGen.InWorld(x, y) || !Visibility.IsTileVisible(x, y)) throw new CommandError(ErrorCodes.NotVisible, $"({x},{y}) is not visible");
            if (!TileClass.IsDoor(Main.tile[x, y].TileType) || !Main.tile[x, y].HasTile)
                throw new CommandError(ErrorCodes.BadParams, $"no door at ({x},{y}) (inspect shows what is there)");
            if (!LlmPlayer.Interact.DoorActions.InReach(player, x, y))
                throw new CommandError(ErrorCodes.BadParams, $"door at ({x},{y}) is out of reach: move_to next to it first");
            bool ok = action switch
            {
                "open" => LlmPlayer.Interact.DoorActions.TryOpen(player, x, y),
                "close" => LlmPlayer.Interact.DoorActions.TryClose(player, x, y),
                _ => throw new CommandError(ErrorCodes.BadParams, "action must be open or close"),
            };
            return new { Door = new { X = x, Y = y }, Action = action, Done = ok,
                Note = ok ? null : (action == "close" ? "it didn't close (already closed, or something stands in the doorway)" : "it didn't open (already open, locked or blocked)") };
        }, isAction: true);

        // What exactly is on one visible tile: get_map draws small objects (saplings, rocks, herbs) as air.
        CommandRouter.Register("inspect", p =>
        {
            int x = Params.Int(p, "x"), y = Params.Int(p, "y");
            if (!WorldGen.InWorld(x, y)) throw new CommandError(ErrorCodes.BadParams, $"({x},{y}) is outside the world");
            if (!Visibility.IsTileVisible(x, y)) throw new CommandError(ErrorCodes.NotVisible, $"({x},{y}) is not visible");
            var t = Main.tile[x, y];
            var info = new GameTileGrid(Main.LocalPlayer).Get(x, y);
            return new
            {
                X = x, Y = y,
                Tile = t.HasTile ? TileClass.Name(t.TileType) : null,
                Solid = t.HasTile && Main.tileSolid[t.TileType] && !t.IsActuated,
                Cuttable = t.HasTile && Main.tileCut[t.TileType],
                Wall = t.WallType == 0 ? null
                    : Terraria.ID.WallID.Search.TryGetName(t.WallType, out var wn) ? wn : $"wall {t.WallType}",
                Liquid = t.LiquidAmount > 0 ? (t.LiquidType == Terraria.ID.LiquidID.Lava ? "lava" : t.LiquidType == Terraria.ID.LiquidID.Honey ? "honey" : "water") : null,
                Protected = Safety.Protection.IsProtected(x, y),
                PickPower = t.HasTile && Main.tileSolid[t.TileType] ? info.PickPowerRequired : (int?)null,
                // Sloped / half blocks: the player walks over them without jumping.
                Shape = info.ShapeName,
            };
        }, isAction: false);

        CommandRouter.Register("get_map", p =>
        {
            int radius = Params.Int(p, "radius", 20);
            var player = Main.LocalPlayer;
            var (x, y) = StateReader.StandingTile(player);
            var me = new TilePoint(x, y);
            return new { Map = MapRenderer.Render(new GameTileGrid(player), me, radius, me, Markers(player), MarkerNames) };
        }, isAction: false);

        CommandRouter.Register("get_entities", p =>
        {
            int radius = Params.Int(p, "radius", EntityReader.DefaultRadius);
            return new { Entities = EntityReader.Read(Main.LocalPlayer, radius) };
        }, isAction: false);

        CommandRouter.Register("find", p => new
        {
            Results = Finder.Find(Main.LocalPlayer, Params.String(p, "category"), Params.StringOrNull(p, "name"),
                Params.Int(p, "radius", Finder.DefaultRadius)),
        }, isAction: false);

        CommandRouter.Register("get_inventory", _ => InventoryReader.Read(Main.LocalPlayer), isAction: false);
    }

    /// <summary>Visible hostiles 'E', town NPCs 'N', dropped items 'i'. Critters aren't marked.
    /// Hostiles win when several share a tile.</summary>
    private static Dictionary<TilePoint, char> Markers(Player player)
    {
        var markers = new Dictionary<TilePoint, char>();
        foreach (var e in EntityReader.ReadAll(player, MapRenderer.MaxRadius * 2))
        {
            char c = e.Kind switch
            {
                EntityKind.Hostile => 'E',
                EntityKind.Town => 'N',
                EntityKind.Item => 'i',
                _ => '\0',
            };
            if (c == '\0') continue;
            var pt = new TilePoint(e.X, e.Y);
            if (c == 'E' || !markers.ContainsKey(pt)) markers[pt] = c;
        }
        return markers;
    }
}
