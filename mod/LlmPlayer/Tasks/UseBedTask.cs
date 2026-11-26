using LlmPlayer.Control;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Tasks;
using LlmPlayer.Interact;
using Terraria;

namespace LlmPlayer.Tasks;

/// <summary>What use_bed reports in the task's <c>result</c>.</summary>
public sealed record BedResult(int SpawnX, int SpawnY, string Message);

/// <summary>
/// Walk within vanilla interaction reach of a bed's foot half and right-click it to set the spawn point
/// (<see cref="BedAccess.SetSpawn"/>: vanilla CheckSpawn/ChangeSpawn, valid housing required). Fails with vanilla's
/// reason (obstructed, not valid housing: too small, missing walls, …) when the game refuses, if the bed is gone, or
/// if it is still out of reach after <see cref="MaxWalks"/> walks.
/// </summary>
public sealed class UseBedTask : IGameTask<GameCtx>
{
    public const int MaxWalks = 3;

    private readonly BedRef _bed;
    private readonly string _label;
    private MoveToTask? _move;
    private int _walks;

    public string Name => "use_bed";
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }
    public object? Result { get; private set; }

    public UseBedTask(BedRef bed)
    {
        _bed = bed;
        _label = $"bed at ({bed.X},{bed.Y})";
    }

    public GameTaskStatus Tick(GameCtx ctx)
    {
        var p = ctx.Player;
        if (BedAccess.Resolve(_bed.X, _bed.Y) is not { } now || now != _bed) return Finish(ctx, GameTaskStatus.Failed, $"the {_label} is gone");

        if (BedAccess.ClickTile(p, _bed) is { } click)
        {
            StopMove(ctx);
            ControlState.Pending = default;
            var (ok, msg, sx, sy) = BedAccess.SetSpawn(p, click.X, click.Y);
            if (!ok) return Finish(ctx, GameTaskStatus.Failed, $"can't set spawn at the {_label}: {msg}");
            Result = new BedResult(sx, sy, msg);
            Reason = msg;
            Progress = "spawn set";
            return GameTaskStatus.Succeeded;
        }

        if (_move == null)
        {
            if (_walks >= MaxWalks) return Finish(ctx, GameTaskStatus.Failed, $"still out of reach of the {_label} after {_walks} walks");
            _walks++;
            _move = new MoveToTask(new TilePoint(_bed.X + 1, _bed.Y + 1), tolerance: 2, name: Name);
        }
        var s = _move.Tick(ctx);
        Progress = $"walking to the {_label}" + (_move.Progress is { } mp ? $": {mp}" : "");
        if (s == GameTaskStatus.Running) return GameTaskStatus.Running;
        var why = _move.Reason;
        _move = null;
        ControlState.Pending = default;
        if (s != GameTaskStatus.Succeeded) return Finish(ctx, GameTaskStatus.Failed, $"couldn't reach the {_label}: {why}");
        return GameTaskStatus.Running;
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
