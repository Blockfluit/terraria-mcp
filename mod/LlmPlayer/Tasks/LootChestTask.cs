using LlmPlayer.Core.Interact;
using LlmPlayer.Core.Tasks;
using LlmPlayer.Interact;

namespace LlmPlayer.Tasks;

/// <summary>What loot_chest reports: what moved into the inventory and what stayed in the chest (no room).</summary>
public sealed record LootResult(int X, int Y, string Name, List<ItemDelta> Looted, List<SlotItem> Left);

/// <summary>
/// open_chest + chest_withdraw all + close_chest in one task: walks to the container and opens it like a right-click
/// (<see cref="OpenChestTask"/>: locked, rigged and in-use chests are refused, keys never used), presses vanilla's
/// <i>Loot All</i> (<see cref="ChestAccess.LootAll"/>: whatever doesn't fit stays in the chest, nothing is dropped),
/// then closes it. Result: <see cref="LootResult"/>.
/// </summary>
public sealed class LootChestTask : IGameTask<GameCtx>
{
    private readonly OpenChestTask _open;
    private readonly ContainerRef _c;

    public string Name => "loot_chest";
    public string? Reason { get; private set; }
    public string? Progress => _open.Progress;
    public object? Result { get; private set; }

    public LootChestTask(ContainerRef c)
    {
        _c = c;
        _open = new OpenChestTask(c);
    }

    public GameTaskStatus Tick(GameCtx ctx)
    {
        var s = _open.Tick(ctx);
        if (s == GameTaskStatus.Running) return s;
        if (s != GameTaskStatus.Succeeded)
        {
            Reason = _open.Reason;
            return s;
        }
        var p = ctx.Player;
        var items = ChestAccess.OpenItems(p);
        if (items == null)
        {
            Reason = "the chest closed before it could be looted";
            return GameTaskStatus.Failed;
        }
        var before = ChestAccess.Snapshot(items);
        ChestAccess.LootAll();
        var after = ChestAccess.Snapshot(ChestAccess.OpenItems(p) ?? items);
        string name = ChestAccess.NameOf(_c);
        ChestAccess.Close(p);
        var looted = InteractMath.Diff(after, before).Where(d => d.Count > 0).ToList();
        Result = new LootResult(_c.X, _c.Y, name, looted, after);
        Reason = before.Count == 0 ? $"{name} was empty"
            : after.Count > 0 ? $"looted {name}; {after.Count} stacks didn't fit and stayed in it"
            : $"looted {name}";
        return GameTaskStatus.Succeeded;
    }

    public void OnCancel(GameCtx ctx) => _open.OnCancel(ctx);
}
