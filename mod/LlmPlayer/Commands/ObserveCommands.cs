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
