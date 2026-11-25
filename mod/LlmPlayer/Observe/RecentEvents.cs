using LlmPlayer.Core.Observe;
using Terraria;
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

    public override void OnHurt(Player.HurtInfo info)
    {
        if (Player.whoAmI == Main.myPlayer)
            RecentEvents.Add($"took {info.Damage} damage ({Player.statLife}/{Player.statLifeMax2} life)");
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
