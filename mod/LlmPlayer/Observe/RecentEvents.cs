using LlmPlayer.Bridge;
using LlmPlayer.Core.Observe;
using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;

namespace LlmPlayer.Observe;

/// <summary>Damage taken, kills and pickups since the last get_state (drained on read). Main thread only.</summary>
public static class RecentEvents
{
    public static readonly EventLog Log = new(capacity: 20);

    public static void Add(string message) => Log.Add(message);
}

public sealed class RecentEventsPlayer : ModPlayer
{
    private static int _worldId = int.MinValue;

    /// <summary>Clear only when a different world is entered: OnEnterWorld also runs on respawn, and wiping the log
    /// then hid what killed the player (seen in game).</summary>
    public override void OnEnterWorld()
    {
        if (Main.worldID == _worldId) return;
        _worldId = Main.worldID;
        RecentEvents.Log.Clear();
    }

    /// <summary>Vanilla's death message ("Bot was slain by a Zombie") as a recent event and a player_died event.</summary>
    public override void Kill(double damage, int hitDirection, bool pvp, PlayerDeathReason damageSource)
    {
        if (Player.whoAmI != Main.myPlayer) return;
        string text = damageSource.GetDeathText(Player.name).ToString();
        RecentEvents.Add($"died: {text}");
        BridgeSystem.Emit("player_died", new { Text = text, X = (int)(Player.position.X / 16f), Y = (int)(Player.Bottom.Y / 16f) });
    }

    /// <summary>One call per hit: feeds both get_state's recent events and the <c>took_damage</c> wire event.
    /// PostHurt, not OnHurt: OnHurt runs before vanilla subtracts the damage, so it reported the life before the
    /// hit (a 10-damage hit at full life read "100/100 life").</summary>
    public override void PostHurt(Player.HurtInfo info)
    {
        if (Player.whoAmI != Main.myPlayer) return;
        string source = DescribeSource(info.DamageSource);
        RecentEvents.Add($"took {info.Damage} damage from {source} ({Player.statLife}/{Player.statLifeMax2} life)");
        BridgeSystem.Emit(EventNames.TookDamage,
            new { Amount = info.Damage, Source = source, Life = Player.statLife, LifeMax = Player.statLifeMax2 });
    }

    /// <summary>NPC name, projectile name, or environment (vanilla PlayerDeathReason.ByOther: 0 fall, 1 drowning,
    /// 2 lava).</summary>
    private static string DescribeSource(PlayerDeathReason r)
    {
        if (r.SourceNPCIndex >= 0 && r.SourceNPCIndex < Main.maxNPCs && Main.npc[r.SourceNPCIndex].active)
            return Main.npc[r.SourceNPCIndex].GivenOrTypeName;
        if (r.SourceProjectileType > 0) return Lang.GetProjectileName(r.SourceProjectileType).Value;
        if (r.SourcePlayerIndex >= 0) return "player";
        return r.SourceOtherIndex switch
        {
            0 => "fall",
            1 => "drowning",
            2 => "lava",
            _ => "environment",
        };
    }
}

public sealed class RecentEventsNpc : GlobalNPC
{
    public override void OnKill(NPC npc)
    {
        if (npc.playerInteraction[Main.myPlayer] && !npc.townNPC && !npc.CountsAsACritter)
            RecentEvents.Add($"killed {npc.GivenOrTypeName}");
    }
}

public sealed class RecentEventsItem : GlobalItem
{
    public override bool OnPickup(Item item, Player player)
    {
        if (player.whoAmI == Main.myPlayer)
            RecentEvents.Add($"picked up {item.Name} x{item.stack}");
        return true;
    }
}
