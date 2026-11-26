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
    public override void OnEnterWorld() => RecentEvents.Log.Clear();

    /// <summary>One call per hit: feeds both get_state's recent events and the <c>took_damage</c> wire event.</summary>
    public override void OnHurt(Player.HurtInfo info)
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
