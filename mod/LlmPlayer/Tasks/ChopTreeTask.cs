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

    /// <summary>Trees tried in a row when no coordinates were given and the nearest can't be reached.</summary>
    public const int MaxTrees = 3;

    private TilePoint _base;
    private readonly Queue<TilePoint> _alternatives;
    private readonly int _skippedAtStart;
    private readonly string _skipText;
    private readonly List<string> _unreachable = new();
    private readonly HeldItemLease _lease = new();
    private readonly TileWorker _worker;
    private readonly bool[] _itemActiveAtStart = new bool[Main.maxItems];
    private PickUpTask? _collect;
    private bool _started;

    public string Name { get; }
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }

    public ChopTreeTask(TilePoint trunkBase, string name = "chop_tree") : this(new TreeTarget(trunkBase, Array.Empty<TilePoint>(), null), name) { }

    /// <param name="target">From <see cref="ResolveBase"/>: the tree plus the next nearest ones to fall back on when
    /// it can't be reached (only when no coordinates were given).</param>
    public ChopTreeTask(TreeTarget target, string name = "chop_tree")
    {
        var trunkBase = target.Base;
        _base = trunkBase;
        _alternatives = new Queue<TilePoint>(target.Alternatives.Take(MaxTrees - 1));
        _skippedAtStart = target.Choice?.Skipped ?? 0;
        _skipText = target.Choice?.SkipText() ?? "";
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
            return Finish(ctx, GameTaskStatus.Succeeded, SkipNote());
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
                if (TreePick.IsReachFailure(_worker.Reason) && NextTree(ctx)) return GameTaskStatus.Running;
                return Finish(ctx, GameTaskStatus.Failed, _worker.Reason + (SkipNote() is { } n ? $" ({n})" : ""));
        }
        _lease.Restore(ctx.Player);
        _collect = new PickUpTask(CollectRadius, CollectTicks, i => !_itemActiveAtStart[i], Name);
        Progress = "tree felled; collecting drops";
        return GameTaskStatus.Running;
    }

    /// <summary>The nearest tree couldn't be reached: go for the next one (still a tree, unprotected, visible).</summary>
    private bool NextTree(GameCtx ctx)
    {
        _unreachable.Add(_base.ToString());
        while (_alternatives.Count > 0)
        {
            var next = _alternatives.Dequeue();
            if (!IsTreeTile(next.X, next.Y) || Protection.IsProtected(next.X, next.Y) || !Visibility.IsTileVisible(next.X, next.Y)) continue;
            _base = next;
            for (int i = 0; i < Main.maxItems; i++) _itemActiveAtStart[i] = Main.item[i].active;
            _worker.Begin(ctx, next);
            Progress = $"tree at {_unreachable[^1]} unreachable; chopping {next}";
            return true;
        }
        return false;
    }

    /// <summary>"skipped 4 protected …; 1 unreachable: (x,y)" or null.</summary>
    private string? SkipNote()
    {
        var parts = new List<string>();
        if (_skippedAtStart > 0) parts.Add($"skipped {_skippedAtStart} nearer tree(s): {_skipText}");
        if (_unreachable.Count > 0) parts.Add($"{_unreachable.Count} unreachable: {string.Join(", ", _unreachable)}");
        return parts.Count == 0 ? null : string.Join("; ", parts);
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
    /// The trunk base to chop. No coordinates → the nearest visible tree that isn't protected (near spawn / in an NPC
    /// house): every visible tree within <see cref="Finder.DefaultRadius"/> is ranked (<see cref="TreePick"/>), the
    /// skipped ones counted, and the next nearest kept as fallbacks if the first can't be reached. Throws
    /// <see cref="CommandError"/>: not_visible (tile or tree not visible / none in sight), bad_params (no tree there),
    /// protected_tile (in an NPC house or near spawn; without coordinates: every visible tree is), missing_item (no axe).
    /// </summary>
    public static TreeTarget ResolveBase(Player p, TilePoint? at)
    {
        if (BestAxe(p) < 0) throw new CommandError(ErrorCodes.MissingItem, "no axe in inventory");
        if (at is { } a)
        {
            if (!WorldGen.InWorld(a.X, a.Y, 10)) throw new CommandError(ErrorCodes.BadParams, $"{a} is outside the world");
            if (!Visibility.IsTileVisible(a.X, a.Y)) throw new CommandError(ErrorCodes.NotVisible, $"{a} is not visible (unexplored or dark)");
            if (!IsTreeTile(a.X, a.Y)) throw new CommandError(ErrorCodes.BadParams, $"no tree at {a} (use find category=tree)");
            var trunk = TreeMath.FindBase(IsTreeTile, a) ?? throw new CommandError(ErrorCodes.BadParams, $"no tree at {a}");
            if (!Visibility.IsTileVisible(trunk.X, trunk.Y))
                throw new CommandError(ErrorCodes.NotVisible, $"the trunk base {trunk} is not visible");
            if (Protection.IsProtected(trunk.X, trunk.Y))
                throw new CommandError(ErrorCodes.ProtectedTile, $"the tree at {trunk} is protected (NPC house or near spawn)");
            return new TreeTarget(trunk, Array.Empty<TilePoint>(), null);
        }

        var found = Finder.AllTrees(p);
        if (found.Count == 0) throw new CommandError(ErrorCodes.NotVisible, $"no visible tree within {Finder.DefaultRadius} tiles");
        var (px, py) = StateReader.StandingTile(p);
        var choice = TreePick.Rank(found.Select(f => new TilePoint(f.X, f.Y)), new TilePoint(px, py),
            f => TreeMath.FindBase(IsTreeTile, f),
            b => Protection.IsProtected(b.X, b.Y),
            b => Visibility.IsTileVisible(b.X, b.Y), max: MaxTrees);
        if (choice.Bases.Count == 0)
            throw new CommandError(choice.SkippedProtected > 0 ? ErrorCodes.ProtectedTile : ErrorCodes.NotVisible,
                $"no choppable tree within {Finder.DefaultRadius} tiles: {choice.SkipText()}; walk away from spawn / NPC houses " +
                "(explore) or give x,y of a tree");
        return new TreeTarget(choice.Bases[0], choice.Bases.Skip(1).ToList(), choice);
    }
}

/// <summary>The tree chop_tree goes for, fallbacks (nearest first) and what was skipped (null with coordinates).</summary>
public sealed record TreeTarget(TilePoint Base, IReadOnlyList<TilePoint> Alternatives, TreeChoice? Choice);
