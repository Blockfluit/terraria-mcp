using LlmPlayer.Bridge;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Npc;
using LlmPlayer.Observe;
using LlmPlayer.Tasks;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace LlmPlayer.Commands;

/// <summary>talk_to_npc and buy (actions, tasks). The chat text and shop come back in the task report's <c>result</c>
/// (<see cref="TalkResult"/>): wait_for_task / get_task, or the <c>task_finished</c> event.</summary>
[CommandGroup]
public static class NpcCommands
{
    /// <summary>Non-town NPCs vanilla still lets you talk to (Main.HoverOverNPCs): bound/rescuable NPCs and the
    /// Skeleton Merchant.</summary>
    private static readonly HashSet<int> TalkableNonTown = new()
    {
        NPCID.BoundGoblin, NPCID.BoundWizard, NPCID.BoundMechanic, NPCID.WebbedStylist, NPCID.SleepingAngler,
        NPCID.BartenderUnconscious, NPCID.SkeletonMerchant, NPCID.GolferRescue,
    };

    public static void Register()
    {
        CommandRouter.Register("talk_to_npc", p =>
        {
            var npc = ResolveNpc(p);
            var task = new TalkToNpcTask(npc);
            TaskHost.Start(task);
            return new { Started = true, Task = task.Name, Npc = npc.GivenOrTypeName };
        }, isAction: true);

        CommandRouter.Register("buy", p =>
        {
            string item = Params.String(p, "item");
            int count = Params.Int(p, "count", 1);
            if (count < 1 || count > BuyTask.MaxCount) throw new CommandError(ErrorCodes.BadParams, $"count must be 1..{BuyTask.MaxCount}");
            if (string.IsNullOrWhiteSpace(item)) throw new CommandError(ErrorCodes.BadParams, "item is empty");
            var npc = ResolveNpc(p);
            if (!NPCShopDatabase.AllShops.Any(s => s.NpcType == npc.type))
                throw new CommandError(ErrorCodes.BadParams, $"{npc.GivenOrTypeName} has no shop");
            var task = new BuyTask(npc, item, count);
            TaskHost.Start(task);
            return new { Started = true, Task = task.Name, Npc = npc.GivenOrTypeName, Item = item.Trim(), Count = count };
        }, isAction: true);
    }

    /// <summary>The NPC from <c>entity_id</c> or <c>name</c> (exactly one): a visible NPC you can talk to. A name is a
    /// personal name or a profession, case-insensitive; the nearest visible match wins.</summary>
    private static NPC ResolveNpc(System.Text.Json.JsonElement p)
    {
        bool hasId = Params.Has(p, "entity_id"), hasName = Params.Has(p, "name");
        if (hasId == hasName) throw new CommandError(ErrorCodes.BadParams, "give exactly one of entity_id or name");
        NPC npc;
        if (hasName)
        {
            string name = Params.String(p, "name");
            var visible = VisibleTalkable();
            int found = NpcNames.Match(visible, name);
            if (found < 0)
                throw new CommandError(ErrorCodes.NotVisible, $"no visible town NPC named '{name.Trim()}' (visible: {NpcNames.List(visible)})");
            npc = Main.npc[found];
        }
        else
        {
            int id = Params.Int(p, "entity_id");
            if (!EntityReader.TryResolve(id, out var e) || e is not NPC n)
                throw new CommandError(ErrorCodes.BadParams, $"no NPC {id} (ids come from get_entities)");
            npc = n;
            if (!Visibility.IsEntityVisible(npc))
                throw new CommandError(ErrorCodes.NotVisible, $"entity {id} is not visible");
        }
        if (EntityReader.KindOf(npc) != EntityKind.Town || !CanChat(npc))
            throw new CommandError(ErrorCodes.BadParams, $"{npc.GivenOrTypeName} is not a town NPC you can talk to");
        return npc;
    }

    /// <summary>Visible NPCs you can talk to, with name, profession and distance (tiles).</summary>
    private static List<NpcCandidate> VisibleTalkable()
    {
        var pl = Main.LocalPlayer;
        var (px, py) = StateReader.StandingTile(pl);
        var list = new List<NpcCandidate>();
        foreach (var npc in Main.npc)
        {
            if (!npc.active || EntityReader.KindOf(npc) != EntityKind.Town || !CanChat(npc) || !Visibility.IsEntityVisible(npc)) continue;
            var (x, y) = EntityReader.TileOf(npc);
            list.Add(new NpcCandidate(npc.whoAmI, npc.GivenOrTypeName, npc.TypeName ?? npc.GivenOrTypeName, Math.Sqrt((double)(x - px) * (x - px) + (double)(y - py) * (y - py))));
        }
        return list;
    }

    /// <summary>Vanilla's rule: a mod's CanChat answer, else town NPCs plus <see cref="TalkableNonTown"/>.</summary>
    private static bool CanChat(NPC npc) => NPCLoader.CanChat(npc) ?? (npc.townNPC || TalkableNonTown.Contains(npc.type));
}
