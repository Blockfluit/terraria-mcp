using LlmPlayer.Control;
using LlmPlayer.Core.Build;
using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Inventory;
using LlmPlayer.Core.Nav;
using LlmPlayer.Core.Tasks;
using LlmPlayer.Observe;
using LlmPlayer.Safety;
using Terraria;
using Terraria.ID;

namespace LlmPlayer.Tasks;

/// <summary>What build_platforms reports in the task's <c>result</c>.</summary>
public sealed record BuildResult(string Item, int Placed, int Remaining, int Skipped, Dictionary<string, int> SkippedReasons,
    List<SkippedTile> SkippedTiles);

/// <summary>
/// build_platforms: place one or more horizontal rows of platforms (or blocks) — bridges, floors, stairs-free
/// platform rows — the way a player does it: walk, hold the item, aim at each tile and use it. Nothing is set
/// directly; vanilla ItemCheck/PlaceThing decides every placement (reach, anchoring, items consumed).
/// <list type="bullet">
/// <item>Order and standing spots come from <see cref="PlatformPlan"/>: bottom row first; each row swept from the end
/// nearest the player; the next tile is the first one in sweep order with something to attach to (vanilla's rule:
/// a solid tile, beam, rope or wall next to it, or a wall behind it); the player stands where it reaches that
/// tile and the most of the following ones (it walks on what it just built), via a <see cref="MoveToTask"/> with
/// digging and building allowed. No such spot (start of a bridge over a chasm, an upper row) → it walks toward the
/// tile itself and lets the pathfinder build its way up.</item>
/// <item>Every tile within reach is placed before moving on: after a placement, the next tile is aimed at in the same
/// tick, still holding "use".</item>
/// <item>Skipped (reported, not fatal): tiles already occupied, inside a housed town NPC's room, with nothing to attach
/// to, refused by the game after <see cref="MaxAttempts"/> attempts, or out of reach after <see cref="MaxMoves"/>
/// walks. Three unreachable tiles in a row fail the task (the rest is probably unreachable too).</item>
/// <item>Fails cleanly when the item runs out, with placed/remaining counts. The pathfinder's own build steps use the
/// same building items.</item>
/// </list>
/// </summary>
public sealed class BuildPlatformsTask : IGameTask<GameCtx>
{
    public const int MaxAttempts = 3, AttemptTicks = 20, MaxMoves = 3, MaxUnreachableInARow = 3, HoldTimeoutTicks = 300;

    private readonly int _itemType;
    private readonly bool _anyPlatform;
    private readonly string _itemName;
    private readonly int _x1, _x2;
    private readonly List<int> _rowYs;
    private readonly int _total;
    private readonly HeldItemLease _lease = new();
    private readonly SkipReport _skips = new();

    private int _rowIdx;
    private List<TilePoint>? _remaining;
    private TilePoint? _target;
    private MoveToTask? _move;
    private int _moves, _attempts, _attemptTick, _holdTicks, _placed, _unreachableInARow;
    private bool _attempting;

    public string Name => "build_platforms";
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }

    public object? Result => new BuildResult(_itemName, _placed, Math.Max(0, _total - _placed - _skips.Count), _skips.Count,
        _skips.ByReason, _skips.Examples);

    /// <param name="anyPlatform">Use any platform item (the first found) rather than only <paramref name="itemType"/>.</param>
    public BuildPlatformsTask(int itemType, bool anyPlatform, string itemName, int x1, int x2, int y, int rows, int spacing)
    {
        _itemType = itemType;
        _anyPlatform = anyPlatform;
        _itemName = itemName;
        _x1 = Math.Min(x1, x2);
        _x2 = Math.Max(x1, x2);
        _rowYs = PlatformPlan.RowYs(y, rows, spacing);
        _total = (int)PlatformPlan.TileCount(x1, x2, rows);
        Progress = $"placed 0/{_total}";
    }

    public GameTaskStatus Tick(GameCtx ctx)
    {
        var p = ctx.Player;
        if (p.noBuilding) return Finish(ctx, GameTaskStatus.Failed, "building isn't possible here (noBuilding)");

        // A few passes per tick: a placement (or skip) moves straight on to the next tile.
        for (int pass = 0; pass < 4; pass++)
        {
            if (_target == null && !PickTarget(p))
                return Finish(ctx, GameTaskStatus.Succeeded, Summary("done"));
            var t = _target!.Value;

            var tile = Main.tile[t.X, t.Y];
            if (tile.HasTile && !Main.tileCut[tile.TileType])
            {
                if (_attempting && IsOurTile(tile.TileType)) _placed++;
                else _skips.Add(t, "occupied");
                NextTarget(ctx);
                continue;
            }

            int slot = FindItemSlot(p);
            if (slot < 0)
                return Finish(ctx, GameTaskStatus.Failed, $"out of {_itemName}: " + Summary("stopped"));
            var item = p.inventory[slot];
            bool solid = item.createTile >= 0 && Main.tileSolid[item.createTile] && !Main.tileSolidTop[item.createTile];

            if (_move != null)
            {
                var st = _move.Tick(ctx);
                if (st == GameTaskStatus.Running)
                {
                    Progress = $"placed {_placed}/{_total}, walking to {t} ({_move.Progress})";
                    return GameTaskStatus.Running;
                }
                _move = null; // finished cleanly on its own
                ControlState.Pending = default;
                // Succeeded or failed: the reach check below decides (a failed walk may still have got close enough).
            }

            bool inReach = ReachMath.InItemRange(p.position.X, p.position.Y, p.width, p.height,
                Player.tileRangeX + p.blockRange, Player.tileRangeY + p.blockRange, item.tileBoost, t.X, t.Y);
            bool overlaps = solid && ReachMath.OverlapsBody(p.position.X, p.position.Y, p.width, p.height, t.X, t.Y);
            if (!inReach || overlaps)
            {
                if (p.itemAnimation > 0) return Aim(t, use: false); // finish the swing before walking
                if (_moves >= MaxMoves)
                {
                    _skips.Add(t, "out of reach");
                    if (++_unreachableInARow >= MaxUnreachableInARow)
                        return Finish(ctx, GameTaskStatus.Failed, $"couldn't get within reach of {MaxUnreachableInARow} tiles in a row (last {t}): " + Summary("stopped"));
                    NextTarget(ctx);
                    continue;
                }
                _moves++;
                _attempting = false;
                StartMove(ctx, p, t, item, solid);
                Progress = $"placed {_placed}/{_total}, walking to {t}";
                return GameTaskStatus.Running;
            }

            if (!_lease.Hold(p, slot))
            {
                if (++_holdTicks > HoldTimeoutTicks) return Finish(ctx, GameTaskStatus.Failed, "couldn't switch to the building item (busy using another item)");
                Progress = "waiting to switch items";
                return Aim(t, use: false);
            }

            // Attempt cycle: hold "use" for AttemptTicks, then release one tick (re-triggers non-autoReuse items).
            _attempting = true;
            if (_attemptTick >= AttemptTicks)
            {
                _attemptTick = 0;
                if (++_attempts >= MaxAttempts)
                {
                    _skips.Add(t, "game refused to place");
                    NextTarget(ctx);
                    continue;
                }
                return Aim(t, use: false);
            }
            _attemptTick++;
            _unreachableInARow = 0;
            Progress = $"placed {_placed}/{_total}" + (_skips.Count > 0 ? $" (skipped {_skips.Count})" : "");
            return Aim(t, use: true);
        }
        return GameTaskStatus.Running;
    }

    public void OnCancel(GameCtx ctx)
    {
        _move?.OnCancel(ctx);
        _move = null;
        ControlState.Pending = default;
        _lease.Restore(ctx.Player);
    }

    // ---- planning glue -----------------------------------------------------------------------------------------

    /// <summary>Choose the next tile (rows bottom-first, sweep order, anchored first); false when all rows are done.</summary>
    private bool PickTarget(Player p)
    {
        while (_rowIdx < _rowYs.Count)
        {
            if (_remaining == null)
            {
                var (px, _) = StateReader.StandingTile(p);
                _remaining = new List<TilePoint>();
                foreach (var t in PlatformPlan.SweepRow(_x1, _x2, _rowYs[_rowIdx], px))
                {
                    if (SkipReason(t.X, t.Y) is { } why) _skips.Add(t, why);
                    else _remaining.Add(t);
                }
            }
            if (_remaining.Count == 0)
            {
                _rowIdx++;
                _remaining = null;
                continue;
            }
            int i = PlatformPlan.NextIndex(_remaining, t => IsAnchored(t.X, t.Y));
            if (i < 0)
            {
                foreach (var t in _remaining) _skips.Add(t, "nothing to attach to");
                _remaining.Clear();
                continue;
            }
            _target = _remaining[i];
            _remaining.RemoveAt(i);
            _moves = _attempts = _attemptTick = _holdTicks = 0;
            _attempting = false;
            return true;
        }
        return false;
    }

    private void NextTarget(GameCtx ctx)
    {
        _move?.OnCancel(ctx); // a walk toward the old tile's stand is no use for the next one
        _move = null;
        _target = null;
        _attempting = false;
    }

    private void StartMove(GameCtx ctx, Player p, TilePoint t, Item item, bool solid)
    {
        var k = NavMath.FromPixels(p.position.X, p.position.Y, p.velocity.X, p.velocity.Y, p.height);
        var here = NavMath.StandingTile(k, ctx.Grid);
        var upcoming = new List<TilePoint> { t };
        if (_remaining != null) upcoming.AddRange(_remaining);
        var reach = new ReachSpec(Player.tileRangeX + p.blockRange, Player.tileRangeY + p.blockRange, item.tileBoost);
        var stand = PlatformPlan.BestStand(ctx.Grid, here, upcoming, reach, solid);
        _move = stand is { } s
            ? new MoveToTask(s, allowDig: true, allowBuild: true, tolerance: 1, name: Name)
            : new MoveToTask(t, allowDig: true, allowBuild: true, tolerance: 3, name: Name);
        ControlState.Pending = default;
    }

    private GameTaskStatus Aim(TilePoint t, bool use)
    {
        ControlState.Pending = default(ControlIntent) with { UseItem = use, CursorTile = t };
        return GameTaskStatus.Running;
    }

    private string Summary(string verb) =>
        $"{verb}: placed {_placed}/{_total}, {Math.Max(0, _total - _placed - _skips.Count)} remaining" +
        (_skips.Count > 0 ? $", skipped {_skips.Count} ({_skips.Summary()})" : "");

    private GameTaskStatus Finish(GameCtx ctx, GameTaskStatus status, string reason)
    {
        Reason = reason;
        Progress = $"placed {_placed}/{_total}";
        OnCancel(ctx);
        return status;
    }

    // ---- game facts --------------------------------------------------------------------------------------------

    private bool IsOurTile(int type) =>
        _anyPlatform ? TileID.Sets.Platforms[type] : new Item(_itemType).createTile == type;

    /// <summary>The held item if it qualifies, else the first qualifying inventory slot; -1 if none left.</summary>
    private int FindItemSlot(Player p)
    {
        if (Qualifies(p.inventory[p.selectedItem])) return p.selectedItem;
        for (int i = 0; i < 58; i++)
            if (Qualifies(p.inventory[i])) return i;
        return -1;
    }

    private bool Qualifies(Item it) => !it.IsAir && (_anyPlatform ? HeldItemLease.IsPlatform(it) : it.type == _itemType);

    /// <summary>Why a tile can't be built on right now (null = it can): occupied by a tile (cuttable plants don't count:
    /// placing replaces them), or inside a housed town NPC's room (Protection.HouseRooms: building there could wall
    /// the NPC in or break its housing).</summary>
    public static string? SkipReason(int x, int y)
    {
        if (!WorldGen.InWorld(x, y, 10)) return "outside the world";
        var tile = Main.tile[x, y];
        if (tile.HasTile && !Main.tileCut[tile.TileType])
            return TileClass.IsProtectedType(tile.TileType) ? "protected object" : "occupied";
        foreach (var room in Protection.HouseRooms)
            if (x >= room.Left && x < room.Right && y >= room.Top && y < room.Bottom) return "protected (NPC house)";
        return null;
    }

    /// <summary>
    /// Vanilla's anchor rule for blocks and platforms (Player.PlaceThing_Tiles_BlockPlacementForAssortedThings, generic
    /// branch): a neighbour (left/right/up/down) that is an active solid tile (platforms count), beam, rope or minecart
    /// track, or has a wall; or a wall behind the target itself.
    /// </summary>
    public static bool IsAnchored(int x, int y)
    {
        if (!WorldGen.InWorld(x, y, 2)) return false;
        if (Main.tile[x, y].WallType > 0) return true;
        return Anchors(x + 1, y) || Anchors(x - 1, y) || Anchors(x, y + 1) || Anchors(x, y - 1);
    }

    private static bool Anchors(int x, int y)
    {
        var t = Main.tile[x, y];
        if (t.WallType > 0) return true;
        if (!t.HasTile) return false;
        int type = t.TileType;
        return Main.tileSolid[type] || TileID.Sets.IsBeam[type] || Main.tileRope[type] || type == TileID.MinecartTrack;
    }
}
