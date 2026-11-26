using LlmPlayer.Control;
using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Tasks;
using LlmPlayer.Observe;
using Terraria;

namespace LlmPlayer.Tasks;

/// <summary>
/// Collect visible dropped items within <c>radius</c> tiles of where the task started, nearest first, by walking to
/// them (<see cref="MoveToTask"/> following the item, no digging). Vanilla does the actual pickup: once an item is
/// inside the player's grab range (<see cref="GrabMath"/>, <c>Player.GetItemGrabRange</c>) it flies in by itself, so
/// the player stops and waits for it instead of chasing it.
/// Only items vanilla would let this player take are considered (reserved for this player, room in the inventory,
/// <c>CanPullItem</c>). An item that can't be reached or won't come in is skipped.
/// Ends Succeeded when none are left, or after the time limit (default 20 s) with the count left in the reason.
/// Progress: "picked up N, M left in range".
/// </summary>
public sealed class PickUpTask : IGameTask<GameCtx>
{
    public const int DefaultTimeoutTicks = 20 * 60;
    private const int GrabWaitTicks = 90;

    private readonly int _radius, _timeoutTicks;
    private readonly Func<int, bool>? _filter;
    private readonly HashSet<int> _skipped = new();
    private TilePoint? _centre;
    private MoveToTask? _move;
    private int _target = -1, _targetType, _elapsed, _waitTicks, _pickedUp;
    private bool _targetWasClose;

    public string Name { get; }
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }

    /// <param name="filter">Optional: only Main.item indices for which this returns true.</param>
    public PickUpTask(int radius, int timeoutTicks = DefaultTimeoutTicks, Func<int, bool>? filter = null, string name = "pick_up_items")
    {
        _radius = radius;
        _timeoutTicks = timeoutTicks;
        _filter = filter;
        Name = name;
    }

    public GameTaskStatus Tick(GameCtx ctx)
    {
        var p = ctx.Player;
        _centre ??= Here(p);

        // Did the current target get collected (or otherwise vanish)?
        if (_target >= 0 && !(Main.item[_target].active && Main.item[_target].type == _targetType))
        {
            if (_targetWasClose) _pickedUp++; // it was flying in or touching us: picked up (not despawned/merged)
            DropTarget(ctx);
        }

        var candidates = Candidates(p);
        Progress = $"picked up {_pickedUp}, {candidates.Count} left in range";
        if (candidates.Count == 0) return Finish(ctx, GameTaskStatus.Succeeded, null);
        if (++_elapsed > _timeoutTicks)
            return Finish(ctx, GameTaskStatus.Succeeded, $"time limit ({_timeoutTicks / 60} s) reached with {candidates.Count} items left");

        if (_target < 0 || !candidates.Contains(_target))
        {
            DropTarget(ctx);
            _target = candidates.MinBy(i => Dist2(p, Main.item[i]));
            _targetType = Main.item[_target].type;
        }

        var item = Main.item[_target];
        _targetWasClose = item.beingGrabbed || InGrabRange(p, item);
        if (_targetWasClose)
        {
            // Vanilla pulls it in; stand still and wait.
            StopMove(ctx);
            ControlState.Pending = default;
            if (++_waitTicks > GrabWaitTicks) SkipTarget(ctx);
            return GameTaskStatus.Running;
        }
        _waitTicks = 0;

        _move ??= new MoveToTask(EntityReader.ItemIdOffset + _target, allowDig: false, allowBuild: true, tolerance: 1, name: Name);
        var s = _move.Tick(ctx);
        if (s == GameTaskStatus.Running) return GameTaskStatus.Running;
        _move = null;
        ControlState.Pending = default;
        // Reached it but it didn't come in, or couldn't reach it: try the others.
        if (Main.item[_target].active && Main.item[_target].type == _targetType && !InGrabRange(p, Main.item[_target]))
            SkipTarget(ctx);
        return GameTaskStatus.Running;
    }

    public void OnCancel(GameCtx ctx)
    {
        StopMove(ctx);
        ControlState.Pending = default;
    }

    private List<int> Candidates(Player p)
    {
        var list = new List<int>();
        var screen = Visibility.Screen;
        var c = _centre!.Value;
        long r2 = (long)_radius * _radius;
        for (int i = 0; i < Main.maxItems; i++)
        {
            var it = Main.item[i];
            if (!it.active || it.IsAir || _skipped.Contains(i)) continue;
            if (_filter != null && !_filter(i)) continue;
            if (it.playerIndexTheItemIsReservedFor != p.whoAmI) continue;
            if (!p.CanAcceptItemIntoInventory(it) || !p.CanPullItem(it, p.ItemSpace(it))) continue;
            var (x, y) = EntityReader.TileOf(it);
            long dx = x - c.X, dy = y - c.Y;
            if (dx * dx + dy * dy > r2) continue;
            if (!Visibility.IsEntityVisible(it, screen, p)) continue;
            list.Add(i);
        }
        return list;
    }

    private static bool InGrabRange(Player p, Item it) =>
        GrabMath.InGrabRange((int)p.position.X, (int)p.position.Y, p.width, p.height, p.GetItemGrabRange(it),
            (int)it.position.X, (int)it.position.Y, it.width, it.height);

    private static float Dist2(Player p, Item it) => (it.Center - p.Center).LengthSquared();

    private static TilePoint Here(Player p)
    {
        var (x, y) = StateReader.StandingTile(p);
        return new TilePoint(x, y);
    }

    private void SkipTarget(GameCtx ctx)
    {
        if (_target >= 0) _skipped.Add(_target);
        DropTarget(ctx);
    }

    private void DropTarget(GameCtx ctx)
    {
        StopMove(ctx);
        _target = -1;
        _waitTicks = 0;
        _targetWasClose = false;
    }

    private void StopMove(GameCtx ctx)
    {
        _move?.OnCancel(ctx);
        _move = null;
    }

    private GameTaskStatus Finish(GameCtx ctx, GameTaskStatus status, string? reason)
    {
        Reason = reason;
        OnCancel(ctx);
        return status;
    }
}
