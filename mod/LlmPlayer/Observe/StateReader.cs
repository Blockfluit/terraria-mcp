using LlmPlayer.Core.Control;
using LlmPlayer.Core.Observe;
using Terraria;
using Terraria.ID;

namespace LlmPlayer.Observe;

/// <summary>Full get_state. Kept on its own so other tasks can add fields (driver, task) in one place.</summary>
public static class StateReader
{
    /// <summary>The tile the player stands at: left column of the body, row of the feet
    /// (the floor is the row below). See Global Constraints.</summary>
    public static (int X, int Y) StandingTile(Player p) =>
        ((int)Math.Floor(p.position.X / 16f), (int)Math.Floor((p.Bottom.Y - 1f) / 16f));

    public static object Read()
    {
        var p = Main.LocalPlayer;
        var (x, y) = StandingTile(p);
        var buffs = new List<string>();
        for (int i = 0; i < p.buffType.Length; i++)
            if (p.buffType[i] > 0 && p.buffTime[i] > 0)
                buffs.Add(Lang.GetBuffName(p.buffType[i]));

        bool bedSpawn = p.SpawnX >= 0 && p.SpawnY >= 0;
        var held = p.HeldItem;
        // Dictionary (keys camelCased by the wire options) so optional fields can be left out entirely.
        var state = new Dictionary<string, object?>
        {
            ["X"] = x,
            ["Y"] = y,
            ["Life"] = p.statLife,
            ["LifeMax"] = p.statLifeMax2,
            ["Mana"] = p.statMana,
            ["ManaMax"] = p.statManaMax2,
            ["Defense"] = (int)p.statDefense,
            ["Buffs"] = buffs,
            ["HeldItem"] = held == null || held.IsAir ? null : held.Name,
            ["FreeSlots"] = LlmPlayer.Inventory.InventorySpace.FreeSlots(p),
            ["TimeOfDay"] = GameClock.Format(Main.dayTime, Main.time),
            ["Biome"] = Biome(p),
            ["Layer"] = DepthLayer.Classify((int)(p.Center.Y / 16f), Main.worldSurface, Main.rockLayer, Main.UnderworldLayer),
            ["Spawn"] = new { X = bedSpawn ? p.SpawnX : Main.spawnTileX, Y = bedSpawn ? p.SpawnY : Main.spawnTileY },
        };
        // Vanilla shows the breath bubbles only while breath is below max: same here.
        if (p.breath < p.breathMax) state["Breath"] = new { Now = Math.Max(0, p.breath), Max = p.breathMax };
        var threats = Threats.Summarize(EntityReader.ReadAll(p, Threats.Radius)
            .Where(e => e.Kind == EntityKind.Hostile).Select(e => e.Distance));
        if (threats != null) state["NearbyHostiles"] = threats;
        var bosses = Bosses(p, x, y);
        if (bosses.Count > 0) state["Boss"] = bosses;
        if (LlmPlayer.Interact.ChestAccess.OpenInfo(p) is { } chest) state["OpenChest"] = chest;
        var events = WorldEvents();
        if (events.Count > 0) state["WorldEvents"] = events;
        if (LlmPlayer.Interact.Housing.StateSummary() is { } housing) state["Housing"] = housing;
        state["Driver"] = LlmPlayer.Control.ControlState.Driver.WireName();
        state["Task"] = LlmPlayer.Tasks.TaskHost.Runner.Current;
        state["RecentEvents"] = RecentEvents.Log.Drain();
        var deaths = RecentEvents.DeathsView();
        if (deaths.Count > 0) state["LastDeaths"] = deaths;
        return state;
    }

    /// <summary>
    /// Every active boss in the world: vanilla shows a boss's health bar wherever it is, so its name and life are fair to
    /// know. Where it is only while the player can see it (fair vision). Eater of Worlds segments merge into one entry.
    /// </summary>
    private static List<BossStatus> Bosses(Player p, int px, int py)
    {
        var sightings = new List<BossSighting>();
        foreach (var npc in Main.npc)
        {
            if (!npc.active || npc.life <= 0 || !EntityReader.IsBoss(npc)) continue;
            var (bx, by) = EntityReader.TileOf(npc);
            double d = Math.Round(Math.Sqrt((double)(bx - px) * (bx - px) + (double)(by - py) * (by - py)), 1);
            sightings.Add(new BossSighting(npc.whoAmI, npc.GivenOrTypeName, npc.life, npc.lifeMax, bx, by, d, Segment: !npc.boss,
                Visible: Visibility.IsEntityVisible(npc)));
        }
        return BossSummary.Collapse(sightings);
    }

    /// <summary>Ongoing world events the player would notice (sky colour, event messages, progress bars).</summary>
    private static List<string> WorldEvents()
    {
        var list = new List<string>();
        if (Main.bloodMoon) list.Add("blood moon");
        if (Main.eclipse) list.Add("solar eclipse");
        if (Main.pumpkinMoon) list.Add("pumpkin moon");
        if (Main.snowMoon) list.Add("frost moon");
        if (Main.slimeRain) list.Add("slime rain");
        if (Main.invasionType > 0 && Main.invasionSize > 0)
        {
            list.Add(Main.invasionType switch
            {
                InvasionID.GoblinArmy => "goblin army",
                InvasionID.SnowLegion => "frost legion",
                InvasionID.PirateInvasion => "pirate invasion",
                InvasionID.MartianMadness => "martian madness",
                _ => "invasion",
            });
        }
        if (Terraria.GameContent.Events.Sandstorm.Happening) list.Add("sandstorm");
        if (Main.raining) list.Add("rain");
        return list;
    }

    /// <summary>The most specific biome the player is in.</summary>
    public static string Biome(Player p)
    {
        if (p.ZoneUnderworldHeight) return "underworld";
        if (p.ZoneDungeon) return "dungeon";
        if (p.ZoneShimmer) return "shimmer";
        if (p.ZoneMeteor) return "meteorite";
        if (p.ZoneHive) return "hive";
        if (p.ZoneGlowshroom) return "glowing mushroom";
        if (p.ZoneCorrupt) return "corruption";
        if (p.ZoneCrimson) return "crimson";
        if (p.ZoneHallow) return "hallow";
        if (p.ZoneJungle) return "jungle";
        if (p.ZoneSnow) return "snow";
        if (p.ZoneUndergroundDesert) return "underground desert";
        if (p.ZoneDesert) return "desert";
        if (p.ZoneBeach) return "ocean";
        if (p.ZoneGraveyard) return "graveyard";
        if (p.ZoneSkyHeight) return "space";
        return p.ZoneOverworldHeight ? "forest" : "caverns";
    }
}
