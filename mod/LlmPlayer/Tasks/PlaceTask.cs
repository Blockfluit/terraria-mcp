using LlmPlayer.Control;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Inventory;
using LlmPlayer.Core.Nav;
using LlmPlayer.Core.Tasks;
using Terraria;
using Terraria.ID;

namespace LlmPlayer.Tasks;

/// <summary>
/// Place a block, wall, platform, torch or furniture item at a tile, the way a player does: walk within reach
/// (<see cref="MoveToTask"/> sub-task), hold the item, aim at the tile and use it. Vanilla ItemCheck/PlaceThing
/// decides whether the placement is valid (anchors, floor for furniture, overlap with creatures, lava, …) and
/// consumes the item. The command validated visibility, emptiness and protection before starting.
/// <list type="bullet">
/// <item>Reach: vanilla's item range (<see cref="ReachMath.InItemRange"/> with <c>Player.tileRangeX/Y</c> and the
/// item's <c>tileBoost</c>). Out of reach → move to within 3 tiles of the target.</item>
/// <item>A solid block whose tile overlaps the player's body can't be placed: step aside first
/// (<see cref="ReachMath.StepAsideGoal"/>).</item>
/// <item>Succeeds as soon as the target holds the item's tile (or wall). Each attempt holds "use" for
/// <see cref="AttemptTicks"/> then releases for a tick; fails after <see cref="MaxAttempts"/> attempts, or after
/// <see cref="MaxMoves"/> moves that still leave the target out of reach.</item>
/// </list>
/// </summary>
public sealed class PlaceTask : IGameTask<GameCtx>
{
    public const int MaxAttempts = 3, AttemptTicks = 30, MaxMoves = 3, HoldTimeoutTicks = 300;

    private readonly TilePoint _target;
    private readonly int _slot;
    private readonly Item _item;
    private readonly string _itemName;
    private readonly int _createTile, _createWall;
    private readonly HeldItemLease _lease = new();
    private MoveToTask? _move;
    private int _moves, _attempts, _attemptTick, _holdTicks;

    public string Name => "place";
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }

    public PlaceTask(Player p, int slot, TilePoint target)
    {
        _slot = slot;
        _item = p.inventory[slot];
        _itemName = _item.Name;
        _createTile = _item.createTile;
        _createWall = _item.createWall;
        _target = target;
    }

    private bool PlacesTile => _createTile >= 0;

    public GameTaskStatus Tick(GameCtx ctx)
    {
        var p = ctx.Player;
        if (Placed()) return Finish(ctx, GameTaskStatus.Succeeded, $"placed {_itemName} at {_target}");
        if (BlockedBy() is { } what) return Finish(ctx, GameTaskStatus.Failed, $"{_target} is now occupied by {what}");
        if (!TaskItems.StillHas(p, _item)) return Finish(ctx, GameTaskStatus.Failed, $"no {_itemName} left in the inventory");

        if (_move != null)
        {
            var st = _move.Tick(ctx);
            if (st == GameTaskStatus.Running)
            {
                Progress = $"moving within reach ({_move.Progress})";
                return GameTaskStatus.Running;
            }
            var why = _move.Reason;
            _move = null;
            if (st == GameTaskStatus.Failed)
                return Finish(ctx, GameTaskStatus.Failed, $"couldn't get within reach of {_target}: {why}");
        }

        bool inReach = ReachMath.InItemRange(p.position.X, p.position.Y, p.width, p.height,
            Player.tileRangeX, Player.tileRangeY, _item.tileBoost, _target.X, _target.Y);
        bool overlaps = PlacesTile && Main.tileSolid[_createTile] && !Main.tileSolidTop[_createTile]
            && ReachMath.OverlapsBody(p.position.X, p.position.Y, p.width, p.height, _target.X, _target.Y);
        if (!inReach || overlaps)
        {
            if (p.itemAnimation > 0) return Hold(aimOnly: true); // finish any swing before walking
            if (_moves >= MaxMoves)
                return Finish(ctx, GameTaskStatus.Failed, overlaps
                    ? $"couldn't step off {_target} to place a solid block there"
                    : $"still out of reach of {_target} after {_moves} moves");
            _moves++;
            _attemptTick = 0;
            var k = NavMath.FromPixels(p.position.X, p.position.Y, p.velocity.X, p.velocity.Y, p.height);
            var here = NavMath.StandingTile(k);
            _move = overlaps
                ? new MoveToTask(ReachMath.StepAsideGoal(here, _target), tolerance: 1, name: Name)
                : new MoveToTask(_target, tolerance: 3, name: Name);
            ControlState.Pending = default;
            return GameTaskStatus.Running;
        }

        if (!_lease.Hold(p, _slot))
        {
            if (++_holdTicks > HoldTimeoutTicks) return Finish(ctx, GameTaskStatus.Failed, "couldn't switch to the item (busy using another item)");
            Progress = "waiting to switch items";
            return Hold(aimOnly: true);
        }

        // Attempt cycle: hold "use" for AttemptTicks, then release one tick (re-triggers non-autoReuse items).
        if (_attemptTick >= AttemptTicks)
        {
            _attemptTick = 0;
            if (++_attempts >= MaxAttempts)
                return Finish(ctx, GameTaskStatus.Failed,
                    $"{_itemName} wasn't placed at {_target} after {MaxAttempts} attempts (the game refused: blocks need an " +
                    "adjacent block or wall, furniture needs solid floor and room, nothing may stand in the way, no lava)");
            return Hold(aimOnly: true);
        }
        _attemptTick++;
        Progress = $"placing (attempt {_attempts + 1}/{MaxAttempts})";
        return Hold(aimOnly: false);
    }

    public void OnCancel(GameCtx ctx)
    {
        _move?.OnCancel(ctx);
        _move = null;
        Cleanup(ctx);
    }

    private GameTaskStatus Hold(bool aimOnly)
    {
        ControlState.Pending = default(ControlIntent) with { UseItem = !aimOnly, CursorTile = _target };
        return GameTaskStatus.Running;
    }

    private bool Placed()
    {
        var t = Main.tile[_target.X, _target.Y];
        return PlacesTile ? t.HasTile && t.TileType == _createTile : t.WallType == _createWall;
    }

    /// <summary>Something else took the spot (a different tile, or a wall when placing a wall). Cuttable plants
    /// don't count: placing replaces them.</summary>
    private string? BlockedBy()
    {
        var t = Main.tile[_target.X, _target.Y];
        if (PlacesTile)
            return t.HasTile && t.TileType != _createTile && !Main.tileCut[t.TileType] ? TileName(t.TileType) : null;
        return t.WallType != WallID.None && t.WallType != _createWall ? "another wall" : null;
    }

    private static string TileName(int type) =>
        TileID.Search.TryGetName(type, out var n) ? n : $"tile {type}";

    private GameTaskStatus Finish(GameCtx ctx, GameTaskStatus status, string reason)
    {
        Reason = reason;
        _move?.OnCancel(ctx);
        _move = null;
        Cleanup(ctx);
        return status;
    }

    private void Cleanup(GameCtx ctx)
    {
        ControlState.Pending = default;
        _lease.Restore(ctx.Player);
    }
}
