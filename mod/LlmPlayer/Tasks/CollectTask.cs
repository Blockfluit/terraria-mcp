using LlmPlayer.Bridge;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Interact;
using LlmPlayer.Core.Observe;
using LlmPlayer.Core.Tasks;
using LlmPlayer.Observe;
using LlmPlayer.Safety;
using Terraria;

namespace LlmPlayer.Tasks;

/// <summary>A collectible to break: its top-left tile, tile type and name.</summary>
public readonly record struct CollectTarget(TilePoint At, ushort Type, ValuableKind Kind, string Name);

/// <summary>What collect reports in the task's result.</summary>
public sealed record CollectResult(string Broke, int X, int Y, List<ItemDelta> PickedUp);

/// <summary>
/// Break one piece of natural loot (pot, life crystal, life fruit, the Enchanted Sword shrine's sword in stone) with
/// the best pickaxe, through <see cref="TileWorker"/> (walk within vanilla reach, real swings: one hit breaks a pot),
/// then collect what dropped like chop_tree does (a <see cref="PickUpTask"/> limited to items that appeared since the
/// task started, radius 12, 6 s). Result: <see cref="CollectResult"/> with the inventory gain per item name.
/// Resolve with <see cref="Resolve"/> before starting.
/// </summary>
public sealed class CollectTask : IGameTask<GameCtx>
{
    private const int CollectRadius = 12, CollectTicks = 6 * 60;

    private readonly CollectTarget _t;
    private readonly HeldItemLease _lease = new();
    private readonly TileWorker _worker;
    private readonly bool[] _itemActiveAtStart = new bool[Main.maxItems];
    private List<SlotItem>? _invBefore;
    private PickUpTask? _collect;
    private bool _started;

    public string Name { get; }
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }
    public object? Result { get; private set; }

    public CollectTask(CollectTarget target, string name = "collect")
    {
        _t = target;
        Name = name;
        _worker = new TileWorker(name, HeldItemLease.BestPickaxe, "pickaxe", _lease);
        Progress = $"breaking {_t.Name} at {_t.At}";
    }

    public GameTaskStatus Tick(GameCtx ctx)
    {
        if (!_started)
        {
            _started = true;
            for (int i = 0; i < Main.maxItems; i++) _itemActiveAtStart[i] = Main.item[i].active;
            _invBefore = InventorySnapshot(ctx.Player);
            _worker.Begin(ctx, _t.At);
        }

        if (_collect != null)
        {
            var c = _collect.Tick(ctx);
            Progress = $"{_t.Name} broken; collecting drops: {_collect.Progress}";
            if (c == GameTaskStatus.Running) return GameTaskStatus.Running;
            _collect = null;
            var gained = InteractMath.Diff(_invBefore!, InventorySnapshot(ctx.Player)).Where(d => d.Count > 0).ToList();
            Result = new CollectResult(_t.Name, _t.At.X, _t.At.Y, gained);
            return Finish(ctx, GameTaskStatus.Succeeded, gained.Count == 0 ? $"broke {_t.Name}; nothing picked up" : $"broke {_t.Name}");
        }

        bool gone = !Present(_t);
        if (!gone && Protection.IsProtected(_t.At.X, _t.At.Y))
            return Finish(ctx, GameTaskStatus.Failed, $"{_t.Name} at {_t.At} became protected; stopped");

        var s = _worker.Tick(ctx, gone);
        switch (s)
        {
            case GameTaskStatus.Running:
                Progress = _worker.MoveProgress is { } mp ? $"walking to {_t.Name} ({mp})" : $"breaking {_t.Name} at {_t.At}";
                return GameTaskStatus.Running;
            case GameTaskStatus.Failed:
                return Finish(ctx, GameTaskStatus.Failed, _worker.Reason);
        }
        _lease.Restore(ctx.Player);
        _collect = new PickUpTask(CollectRadius, CollectTicks, i => !_itemActiveAtStart[i], Name);
        Progress = $"{_t.Name} broken; collecting drops";
        return GameTaskStatus.Running;
    }

    public void OnCancel(GameCtx ctx)
    {
        _collect?.OnCancel(ctx);
        _collect = null;
        Cleanup(ctx);
    }

    private GameTaskStatus Finish(GameCtx ctx, GameTaskStatus status, string? reason)
    {
        Reason = reason;
        Cleanup(ctx);
        return status;
    }

    private void Cleanup(GameCtx ctx)
    {
        _worker.Cancel(ctx);
        _lease.Restore(ctx.Player);
    }

    private static bool Present(CollectTarget t) =>
        WorldGen.InWorld(t.At.X, t.At.Y) && Main.tile[t.At.X, t.At.Y] is { HasTile: true } tile && tile.TileType == t.Type;

    /// <summary>Main inventory, coins and ammo (slots 0..57) as name/stack pairs.</summary>
    private static List<SlotItem> InventorySnapshot(Player p)
    {
        var list = new List<SlotItem>();
        for (int i = 0; i < 58; i++)
            if (p.inventory[i] is { IsAir: false } it) list.Add(new SlotItem(i, it.Name, it.stack));
        return list;
    }

    /// <summary>
    /// The collectible at (x,y) (any of its tiles). Throws <see cref="CommandError"/>: bad_params (outside the world,
    /// nothing there, not loot: orbs/altars/chests/blocks get a hint), not_visible, protected_tile (NPC house or near
    /// spawn), missing_item (no pickaxe).
    /// </summary>
    public static CollectTarget Resolve(Player p, int x, int y)
    {
        if (!WorldGen.InWorld(x, y, 10)) throw new CommandError(ErrorCodes.BadParams, $"({x},{y}) is outside the world");
        if (!Visibility.IsTileVisible(x, y)) throw new CommandError(ErrorCodes.NotVisible, $"({x},{y}) is not visible (unexplored or dark)");
        var tile = Main.tile[x, y];
        if (!tile.HasTile) throw new CommandError(ErrorCodes.BadParams, $"nothing at ({x},{y}) (find category=valuable)");
        var kind = TileClass.Valuable(tile);
        string tileName = TileClass.Name(tile.TileType);
        if (kind is not { } k || !Valuables.IsCollectible(k))
        {
            string hint = kind switch
            {
                ValuableKind.Chest => ": it's a chest; use loot_chest",
                ValuableKind.Orb => ": breaking a Shadow Orb / Crimson Heart has world effects (the third one summons a boss); the AI never breaks it",
                ValuableKind.Altar => ": altars can't be broken before Hardmode (they're a crafting station for boss summons)",
                _ => "; collect takes pots, life crystals, life fruit and the sword in stone (use mine for blocks)",
            };
            throw new CommandError(ErrorCodes.BadParams, $"({x},{y}) ({tileName}) isn't loot to collect{hint}");
        }
        var (ox, oy) = TileClass.ObjectOrigin(x, y, tile);
        var origin = Main.tile[ox, oy];
        if (!origin.HasTile || origin.TileType != tile.TileType) (ox, oy) = (x, y);
        string name = Valuables.Name(k, Main.tile[ox, oy].TileFrameX);
        if (Protection.IsProtected(ox, oy) || Protection.IsProtected(x, y))
            throw new CommandError(ErrorCodes.ProtectedTile, $"{name} at ({ox},{oy}) is protected (NPC house or near spawn)");
        if (HeldItemLease.BestPickaxe(p) < 0) throw new CommandError(ErrorCodes.MissingItem, "no pickaxe in inventory (collect breaks things with a pickaxe)");
        return new CollectTarget(new TilePoint(ox, oy), tile.TileType, k, name);
    }
}
