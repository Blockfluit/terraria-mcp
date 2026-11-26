using LlmPlayer.Control;
using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using LlmPlayer.Core.Tasks;
using Terraria;

namespace LlmPlayer.Tasks;

/// <summary>
/// Shared engine of the gathering tasks: get within tool reach of one tile, hold the tool, and swing at the tile
/// (UseItem with the cursor on it) until the caller says it's done. Nothing is broken directly; vanilla ItemCheck
/// applies reach, tool power and use time.
/// <list type="bullet">
/// <item>Reach = <c>Player.tileRangeX/Y</c> + the tool's <c>tileBoost</c> (vanilla <c>IsTargetTileInItemRange</c>,
/// <see cref="Reach"/>), checked every tick against the real hitbox.</item>
/// <item>Out of reach → a <see cref="MoveToTask"/> sub-task to the spot <see cref="StandFinder"/> picks (with
/// <c>approachMargin</c> tiles of reach to spare), abandoned as soon as the tile is in reach. At most
/// <see cref="MaxApproaches"/> approaches per tile.</item>
/// <item>In reach → stand still, hold the tool (<see cref="HeldItemLease"/>, owned by the caller and restored when
/// its task ends), swing. Fails if the tile survives <see cref="SwingTimeoutTicks"/> of swinging.</item>
/// </list>
/// </summary>
internal sealed class TileWorker
{
    public const int MaxApproaches = 4;
    public const int SwingTimeoutTicks = 15 * 60;

    private readonly string _name;
    private readonly Func<Player, int> _toolSlot;
    private readonly string _toolName;
    private readonly HeldItemLease _lease;
    private readonly float _approachMargin;

    private TilePoint _target;
    private IReadOnlySet<TilePoint>? _pending;
    private MoveToTask? _move;
    private int _approaches, _swingTicks, _settleWait;

    /// <summary>Ticks to let the player come to rest after arriving before judging reach again.</summary>
    private const int SettleTicks = 20;
    private bool _working;

    public string? Reason { get; private set; }

    /// <summary>Walking right now (for progress strings): the move sub-task's progress, else null.</summary>
    public string? MoveProgress => _move?.Progress;

    /// <param name="toolSlot">Inventory slot of the tool to use (-1: none).</param>
    /// <param name="approachMargin">Tiles of reach to spare when choosing where to stand (larger = closer).</param>
    public TileWorker(string name, Func<Player, int> toolSlot, string toolName, HeldItemLease lease, float approachMargin = 1f)
    {
        _name = name;
        _toolSlot = toolSlot;
        _toolName = toolName;
        _lease = lease;
        _approachMargin = approachMargin;
    }

    /// <summary>Work on a new tile. <paramref name="pending"/>: other tiles still to be broken (not stood on if avoidable).</summary>
    public void Begin(GameCtx ctx, TilePoint target, IReadOnlySet<TilePoint>? pending = null)
    {
        StopMove(ctx);
        _target = target;
        _pending = pending;
        _approaches = _swingTicks = _settleWait = 0;
        _working = false;
        Reason = null;
    }

    /// <summary>Running until <paramref name="done"/> is true (Succeeded) or it gives up (Failed, see <see cref="Reason"/>).</summary>
    public GameTaskStatus Tick(GameCtx ctx, bool done)
    {
        var p = ctx.Player;
        if (done)
        {
            StopMove(ctx);
            ControlState.Pending = default;
            return GameTaskStatus.Succeeded;
        }
        int slot = _toolSlot(p);
        if (slot < 0) return Fail(ctx, $"no {_toolName} in inventory");
        if (p.noBuilding) return Fail(ctx, "tools can't be used here (noBuilding)");

        var reach = new ReachSpec(Player.tileRangeX, Player.tileRangeY, p.inventory[slot].tileBoost);
        // Hysteresis: start swinging with a little reach to spare, keep swinging until truly out of reach. Standing
        // still on the ground nothing drifts, so the exact vanilla reach counts: tiles at the very edge of reach
        // (e.g. two rows below the feet with a copper/tin pickaxe, tileBoost -1) were otherwise never worked.
        bool settled = p.velocity.X == 0f && p.velocity.Y == 0f;
        float margin = _working || settled ? 0f : 0.25f;
        bool inReach = Reach.InRange(p.position.X / 16f, p.position.Y / 16f, p.width / 16f, p.height / 16f, _target, reach, margin);

        if (inReach)
        {
            StopMove(ctx);
            _working = true;
            if (!_lease.Hold(p, slot))
            {
                ControlState.Pending = default; // waiting for the current swing to end before switching items
                return GameTaskStatus.Running;
            }
            if (++_swingTicks > SwingTimeoutTicks)
                return Fail(ctx, $"{_target} didn't break after {SwingTimeoutTicks / 60} s of swinging");
            ControlState.Pending = new ControlIntent { UseItem = true, CursorTile = _target };
            return GameTaskStatus.Running;
        }

        _working = false;
        // Just arrived: while still sliding the 0.25-tile margin applies, so a tile at the very edge of reach looks
        // out of reach for a few ticks. Re-approaching then "arrived" instantly and burned every approach within a
        // few ticks of stopping (seen in game). Wait for the player to settle first.
        if (_move == null && _settleWait > 0 && !settled)
        {
            _settleWait--;
            ControlState.Pending = default;
            return GameTaskStatus.Running;
        }
        if (_move == null)
        {
            if (++_approaches > MaxApproaches) return Fail(ctx, $"couldn't get within reach of {_target}");
            var k = NavMath.FromPixels(p.position.X, p.position.Y, p.velocity.X, p.velocity.Y, p.height);
            var here = NavMath.StandingTile(k);
            // Prefer a spot with reach to spare; fall back to the exact edge of reach before giving up on stands.
            var stand = StandFinder.Best(ctx.Grid, here, _target, reach, _pending, _approachMargin)
                        ?? (_approachMargin > 1f ? StandFinder.Best(ctx.Grid, here, _target, reach, _pending) : null)
                        ?? StandFinder.Best(ctx.Grid, here, _target, reach, _pending, margin: 0f);
            // No known spot (e.g. the tile is buried in rock): head for the tile itself and let the pathfinder dig.
            // A stand spot is exact: it can sit at the very edge of reach, and stopping a tile short (tolerance 1)
            // left the tile out of reach while every re-approach "arrived" instantly at the same spot (seen in game:
            // "couldn't get within reach" after 4 approaches from far away).
            _move = stand is { } st
                ? new MoveToTask(st, allowDig: true, allowBuild: true, tolerance: 0, name: _name)
                : new MoveToTask(_target, allowDig: true, allowBuild: true, tolerance: 1, name: _name);
        }

        var status = _move.Tick(ctx);
        if (status == GameTaskStatus.Running) return GameTaskStatus.Running;
        string? why = _move.Reason;
        _move = null; // it cleaned up after itself
        ControlState.Pending = default;
        if (status == GameTaskStatus.Failed) return Fail(ctx, $"can't reach {_target}: {why}");
        _settleWait = SettleTicks;
        return GameTaskStatus.Running; // arrived: re-check reach once settled
    }

    public void Cancel(GameCtx ctx)
    {
        StopMove(ctx);
        ControlState.Pending = default;
    }

    private GameTaskStatus Fail(GameCtx ctx, string reason)
    {
        Reason = reason;
        Cancel(ctx);
        return GameTaskStatus.Failed;
    }

    private void StopMove(GameCtx ctx)
    {
        _move?.OnCancel(ctx);
        _move = null;
    }
}
