using LlmPlayer.Core.Grid;
using LlmPlayer.Tasks;
using Terraria;
using Terraria.ModLoader;

namespace LlmPlayer.Interact;

/// <summary>Notes every chest the local player has open (by the AI or by the user) in explore_caves' memory, so a
/// chest seen empty inside is later known as looted (<see cref="Core.Gather.ExploreMemory.ChestOpened"/>).</summary>
public sealed class OpenedChestTracker : ModPlayer
{
    private int _last = -1;

    public override void PostUpdate()
    {
        if (Player.whoAmI != Main.myPlayer) return;
        int idx = Player.chest;
        if (idx == _last) return;
        _last = idx;
        if (idx < 0 || idx >= Main.maxChests || Main.chest[idx] is not { } c) return;
        ExploreCavesTask.Memory.UseWorld(ExploreCavesTask.WorldKey);
        ExploreCavesTask.Memory.ChestOpened(new TilePoint(c.x, c.y));
    }
}
