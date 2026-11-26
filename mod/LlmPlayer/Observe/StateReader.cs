using LlmPlayer.Core.Control;
using LlmPlayer.Core.Observe;
using Terraria;

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
        return new
        {
            X = x,
            Y = y,
            Life = p.statLife,
            LifeMax = p.statLifeMax2,
            Mana = p.statMana,
            ManaMax = p.statManaMax2,
            Defense = (int)p.statDefense,
            Buffs = buffs,
            HeldItem = held == null || held.IsAir ? null : held.Name,
            TimeOfDay = GameClock.Format(Main.dayTime, Main.time),
            Biome = Biome(p),
            Layer = DepthLayer.Classify((int)(p.Center.Y / 16f), Main.worldSurface, Main.rockLayer, Main.UnderworldLayer),
            Spawn = new { X = bedSpawn ? p.SpawnX : Main.spawnTileX, Y = bedSpawn ? p.SpawnY : Main.spawnTileY },
            Driver = LlmPlayer.Control.ControlState.Driver.WireName(),
            Task = LlmPlayer.Tasks.TaskHost.Runner.Current,
            RecentEvents = RecentEvents.Log.Drain(),
        };
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
