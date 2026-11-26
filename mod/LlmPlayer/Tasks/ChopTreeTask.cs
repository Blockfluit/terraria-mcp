using LlmPlayer.Bridge;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Tasks;
using LlmPlayer.Observe;
using LlmPlayer.Safety;
using Terraria;

namespace LlmPlayer.Tasks;

/// <summary>
/// Fell one tree: walk next to the trunk's bottom tile, hold the best axe and swing at that tile
/// (<see cref="TileWorker"/>) until it's gone (vanilla then fells everything above it). Afterwards it collects the
/// wood that dropped nearby (a <see cref="PickUpTask"/> limited to items that appeared during the chop, radius 12,
/// 6 s). Progress: "walking to tree (N tiles remaining)" / "chopping (x,y)" / "collecting drops: …".
/// Resolve and validate the base with <see cref="ResolveBase"/> before starting.
/// </summary>
public sealed class ChopTreeTask : IGameTask<GameCtx>
{
    private const int CollectRadius = 12, CollectTicks = 6 * 60;
    /// <summary>Reach to spare when choosing where to stand: ~2 tiles from the trunk ("next to" it).</summary>
    private const float StandMargin = 3f;

    private readonly TilePoint _base;
    private readonly HeldItemLease _lease = new();
    private readonly TileWorker _worker;
    private readonly bool[] _itemActiveAtStart = new bool[Main.maxItems];
    private PickUpTask? _collect;
    private bool _started;

    public string Name { get; }
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }

    public ChopTreeTask(TilePoint trunkBase, string name = "chop_tree")
    {
        _base = trunkBase;
        Name = name;
        _worker = new TileWorker(name, BestAxe, "axe", _lease, StandMargin);
        Progress = $"chopping {trunkBase}";
    }

    public GameTaskStatus Tick(GameCtx ctx)
    {
        if (!_started)
        {
            _started = true;
            for (int i = 0; i < Main.maxItems; i++) _itemActiveAtStart[i] = Main.item[i].active;
            _worker.Begin(ctx, _base);
        }

        if (_collect != null)
        {
            var c = _collect.Tick(ctx);
            Progress = $"tree felled; collecting drops: {_collect.Progress}";
            if (c == GameTaskStatus.Running) return GameTaskStatus.Running;
            _collect = null;
            return Finish(ctx, GameTaskStatus.Succeeded, null);
        }

        bool gone = !IsTreeTile(_base.X, _base.Y);
        if (!gone && Protection.IsProtected(_base.X, _base.Y))
            return Finish(ctx, GameTaskStatus.Failed, $"{_base} became protected; stopped");

        var s = _worker.Tick(ctx, gone);
        switch (s)
        {
            case GameTaskStatus.Running:
                Progress = _worker.MoveProgress is { } mp ? $"walking to tree ({mp})" : $"chopping {_base}";
                return GameTaskStatus.Running;
            case GameTaskStatus.Failed:
                return Finish(ctx, GameTaskStatus.Failed, _worker.Reason);
        }
        _lease.Restore(ctx.Player);
        _collect = new PickUpTask(CollectRadius, CollectTicks, i => !_itemActiveAtStart[i], Name);
        Progress = "tree felled; collecting drops";
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

    /// <summary>Inventory slot (0..57) of the axe with the highest axe power, or -1.</summary>
    public static int BestAxe(Player p)
    {
        int best = -1, power = 0;
        for (int i = 0; i < 58; i++)
        {
            var it = p.inventory[i];
            if (it.IsAir || it.axe <= power) continue;
            best = i;
            power = it.axe;
        }
        return best;
    }

    public static bool IsTreeTile(int x, int y)
    {
        if (!WorldGen.InWorld(x, y)) return false;
        var tile = Main.tile[x, y];
        return tile.HasTile && TileClass.IsTree(tile.TileType);
    }

    /// <summary>
    /// The trunk base to chop. No coordinates → the nearest visible tree (<see cref="Finder"/>). Throws
    /// <see cref="CommandError"/>: not_visible (tile or tree not visible / none in sight), bad_params (no tree there),
    /// protected_tile (in an NPC house or near spawn), missing_item (no axe).
    /// </summary>
    public static TilePoint ResolveBase(Player p, TilePoint? at)
    {
        TilePoint start;
        if (at is { } a)
        {
            if (!WorldGen.InWorld(a.X, a.Y, 10)) throw new CommandError(ErrorCodes.BadParams, $"{a} is outside the world");
            if (!Visibility.IsTileVisible(a.X, a.Y)) throw new CommandError(ErrorCodes.NotVisible, $"{a} is not visible (unexplored or dark)");
            if (!IsTreeTile(a.X, a.Y)) throw new CommandError(ErrorCodes.BadParams, $"no tree at {a} (use find category=tree)");
            start = a;
        }
        else
        {
            var found = Finder.Find(p, "tree", null);
            if (found.Count == 0) throw new CommandError(ErrorCodes.NotVisible, $"no visible tree within {Finder.DefaultRadius} tiles");
            // Nearest tree that isn't protected (near spawn / in an NPC house); if all are, the nearest one's error below.
            var pick = found.FirstOrDefault(f => TreeMath.FindBase(IsTreeTile, new TilePoint(f.X, f.Y)) is { } b
                                                 && !Protection.IsProtected(b.X, b.Y)) ?? found[0];
            start = new TilePoint(pick.X, pick.Y);
        }

        var trunk = TreeMath.FindBase(IsTreeTile, start)
                    ?? throw new CommandError(ErrorCodes.BadParams, $"no tree at {start}");
        if (!Visibility.IsTileVisible(trunk.X, trunk.Y))
            throw new CommandError(ErrorCodes.NotVisible, $"the trunk base {trunk} is not visible");
        if (Protection.IsProtected(trunk.X, trunk.Y))
            throw new CommandError(ErrorCodes.ProtectedTile, $"the tree at {trunk} is protected (NPC house or near spawn)");
        if (BestAxe(p) < 0) throw new CommandError(ErrorCodes.MissingItem, "no axe in inventory");
        return trunk;
    }
}
