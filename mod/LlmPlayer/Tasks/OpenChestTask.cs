using LlmPlayer.Control;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Interact;
using LlmPlayer.Core.Tasks;
using LlmPlayer.Interact;
using Terraria;

namespace LlmPlayer.Tasks;

/// <summary>What open_chest reports in the task's <c>result</c>.</summary>
public sealed record ChestContents(int X, int Y, string Name, List<SlotItem> Items);

/// <summary>
/// Walk within vanilla interaction reach of a container (<see cref="MoveToTask"/> to its floor, tolerance 2) and
/// open it like a right-click (<see cref="ChestAccess.Open"/>). The reach check runs every tick
/// (<c>Player.IsInTileInteractionRange</c> on the container's tiles), so it opens as soon as it can. Succeeds with
/// the contents (<see cref="ChestContents"/>, also the task's <c>result</c>); the chest stays open for
/// chest_deposit / chest_withdraw. Fails if the container is gone, locked, rigged or in use, the walk fails, it
/// is still out of reach after <see cref="MaxWalks"/> walks, or (multiplayer) the server doesn't open it within
/// <see cref="OpenWaitTicks"/>.
/// </summary>
public sealed class OpenChestTask : IGameTask<GameCtx>
{
    public const int MaxWalks = 3, OpenWaitTicks = 120;

    private readonly ContainerRef _c;
    private readonly string _label;
    private MoveToTask? _move;
    private int _walks, _waiting = -1;

    public string Name => "open_chest";
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }
    public object? Result { get; private set; }

    public OpenChestTask(ContainerRef c)
    {
        _c = c;
        _label = $"{ChestAccess.NameOf(c)} at ({c.X},{c.Y})";
    }

    public GameTaskStatus Tick(GameCtx ctx)
    {
        var p = ctx.Player;
        if (ChestAccess.Resolve(_c.X, _c.Y) is not { } now || now.Type != _c.Type || now.X != _c.X || now.Y != _c.Y)
            return Finish(ctx, GameTaskStatus.Failed, $"{_label} is gone");

        if (_waiting >= 0)
        {
            if (ChestAccess.IsOpenContainer(p, _c)) return Opened(p);
            if (++_waiting > OpenWaitTicks) return Finish(ctx, GameTaskStatus.Failed, $"{_label} didn't open (the game refused)");
            return GameTaskStatus.Running;
        }

        if (ChestAccess.ClickTile(p, _c) is { } click)
        {
            StopMove(ctx);
            ControlState.Pending = default;
            if (ChestAccess.Open(p, _c, click.X, click.Y) is { } error) return Finish(ctx, GameTaskStatus.Failed, error);
            if (ChestAccess.IsOpenContainer(p, _c)) return Opened(p);
            _waiting = 0;
            Progress = "opening";
            return GameTaskStatus.Running;
        }

        if (_move == null)
        {
            if (_walks >= MaxWalks) return Finish(ctx, GameTaskStatus.Failed, $"still out of reach of {_label} after {_walks} walks");
            _walks++;
            _move = new MoveToTask(new TilePoint(_c.X, _c.Y + _c.Height - 1), tolerance: 2, name: Name);
        }
        var s = _move.Tick(ctx);
        Progress = $"walking to {_label}" + (_move.Progress is { } mp ? $": {mp}" : "");
        if (s == GameTaskStatus.Running) return GameTaskStatus.Running;
        var why = _move.Reason;
        _move = null;
        ControlState.Pending = default;
        if (s != GameTaskStatus.Succeeded) return Finish(ctx, GameTaskStatus.Failed, $"couldn't reach {_label}: {why}");
        return GameTaskStatus.Running; // arrived: the reach check runs next tick
    }

    private GameTaskStatus Opened(Player p)
    {
        var items = ChestAccess.OpenItems(p);
        Result = new ChestContents(_c.X, _c.Y, ChestAccess.NameOf(_c), items == null ? new() : ChestAccess.Snapshot(items));
        Progress = "open";
        Reason = $"opened {_label}";
        return GameTaskStatus.Succeeded;
    }

    public void OnCancel(GameCtx ctx)
    {
        StopMove(ctx);
        ControlState.Pending = default;
    }

    private void StopMove(GameCtx ctx)
    {
        _move?.OnCancel(ctx);
        _move = null;
    }

    private GameTaskStatus Finish(GameCtx ctx, GameTaskStatus status, string reason)
    {
        Reason = reason;
        OnCancel(ctx);
        return status;
    }
}
