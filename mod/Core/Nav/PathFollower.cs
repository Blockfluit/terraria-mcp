using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Nav;

/// <summary>Player state in tiles. X/Y are the feet-left position (Y = feet tile row when standing).
/// Velocities are in tiles per tick; VelY &lt; 0 means rising.</summary>
public readonly record struct PlayerKinematics(float X, float Y, float VelX, float VelY, bool OnGround);

/// <summary>Controls to apply for one tick. CursorTile, when set, is where to aim the cursor.</summary>
public record struct ControlIntent(bool Left, bool Right, bool Jump, bool Down, bool UseItem, TilePoint? CursorTile);

public enum FollowerStatus { Running, Arrived, Stuck, NeedsReplan }

public readonly record struct FollowerOutput(ControlIntent Intent, FollowerStatus Status, PathStep? Current);

/// <summary>
/// Turns a path into per-tick control intents. Call Tick once per game tick.
/// Once Stuck or NeedsReplan is reported the follower stays in that state; build a new one from a fresh path.
/// </summary>
public sealed class PathFollower
{
    private const float Deadzone = 0.3f;
    private const float ArriveTolerance = 0.5f;
    private const float ProgressEpsilon = 0.1f;
    /// <summary>Dig/Build show no grid progress until the tile flips, so they get a larger budget.</summary>
    private const int WorkStuckMultiplier = 4;

    private enum JumpPhase { Ready, Rising, Falling }

    private readonly IReadOnlyList<PathStep> _steps;
    private readonly int _stuckTicks;
    private int _index;
    private FollowerStatus _terminal = FollowerStatus.Running;

    private float _bestDist = float.MaxValue;
    private int _noProgress;
    private bool _working;

    private JumpPhase _jump = JumpPhase.Ready;
    private bool _leftGround;
    private float _jumpStartY;

    public PathFollower(IReadOnlyList<PathStep> steps, int stuckTicks = 90)
    {
        _steps = steps;
        _stuckTicks = stuckTicks;
    }

    public int RemainingSteps => _steps.Count - _index;

    public FollowerOutput Tick(PlayerKinematics k, ITileGrid grid)
    {
        if (_terminal != FollowerStatus.Running) return new(default, _terminal, Current);

        if (PathInvalidated(grid)) return Finish(FollowerStatus.NeedsReplan);

        // Advance past every waypoint already satisfied (work done and feet on it).
        while (_index < _steps.Count)
        {
            var s = _steps[_index];
            if (IsWork(s.Kind) && !WorkDone(s, grid)) break;
            if (IsWork(s.Kind) && _index + 1 < _steps.Count
                && IsWork(_steps[_index + 1].Kind) && _steps[_index + 1].Pos == s.Pos)
            {
                Advance(); // more work from the same spot: do it before moving
                continue;
            }
            if (!Reached(s, k)) break;
            Advance();
        }
        if (_index >= _steps.Count) return Finish(FollowerStatus.Arrived);

        var step = _steps[_index];
        var intent = new ControlIntent();
        bool working = IsWork(step.Kind) && !WorkDone(step, grid);

        if (working)
        {
            intent.UseItem = true;
            intent.CursorTile = step.WorkTile;
        }
        else
        {
            float dx = step.Pos.X - k.X;
            intent.Right = dx > Deadzone;
            intent.Left = dx < -Deadzone;
            switch (step.Kind)
            {
                case MoveKind.Jump:
                    intent.Jump = JumpControl(step, k);
                    break;
                case MoveKind.DropThrough:
                    intent.Down = k.OnGround && k.Y < step.Pos.Y - ArriveTolerance;
                    break;
                case MoveKind.Swim:
                    intent.Jump = k.Y > step.Pos.Y + Deadzone;
                    break;
            }
        }

        if (UpdateStuck(step, k, working)) return Finish(FollowerStatus.Stuck);
        return new(intent, FollowerStatus.Running, step);
    }

    private PathStep? Current => _index < _steps.Count ? _steps[_index] : null;

    private FollowerOutput Finish(FollowerStatus status)
    {
        _terminal = status;
        return new(default, status, Current);
    }

    private void Advance()
    {
        _index++;
        _bestDist = float.MaxValue;
        _noProgress = 0;
        _working = false;
        _jump = JumpPhase.Ready;
        _leftGround = false;
    }

    private static bool IsWork(MoveKind kind) => kind is MoveKind.Dig or MoveKind.BuildPlatform;

    private static bool WorkDone(PathStep s, ITileGrid grid)
    {
        if (s.WorkTile is not { } w) return true;
        var t = grid.Get(w);
        return s.Kind == MoveKind.Dig ? !t.IsSolid : t.IsStandable;
    }

    private static bool Reached(PathStep s, PlayerKinematics k)
    {
        float dx = s.Pos.X - k.X, dy = s.Pos.Y - k.Y;
        if (Math.Abs(dy) > ArriveTolerance) return false;
        if (s.Kind != MoveKind.Swim && !k.OnGround) return false;
        if (Math.Abs(dx) <= ArriveTolerance) return true;
        // Fast horizontal movement can carry the feet past a waypoint between ticks.
        bool overshot = k.VelX != 0 && Math.Sign(dx) != Math.Sign(k.VelX);
        return overshot && Math.Abs(dx) <= Math.Abs(k.VelX);
    }

    private bool JumpControl(PathStep s, PlayerKinematics k)
    {
        switch (_jump)
        {
            case JumpPhase.Ready:
                if (!k.OnGround) return false;
                _jump = JumpPhase.Rising;
                _leftGround = false;
                _jumpStartY = k.Y;
                return true;
            case JumpPhase.Rising:
                if (!k.OnGround) _leftGround = true;
                else if (_leftGround) { _jump = JumpPhase.Ready; return false; } // landed short: release this tick
                bool rising = !_leftGround || k.VelY < 0;
                bool heightReached = _jumpStartY > s.Pos.Y && k.Y <= s.Pos.Y;
                if (rising && !heightReached) return true;
                _jump = JumpPhase.Falling;
                return false;
            default: // Falling: wait for landing; the landing tick keeps jump released
                if (k.OnGround) _jump = JumpPhase.Ready;
                return false;
        }
    }

    private bool UpdateStuck(PathStep s, PlayerKinematics k, bool working)
    {
        if (working != _working)
        {
            _working = working;
            _bestDist = float.MaxValue;
            _noProgress = 0;
        }
        float dist = Math.Abs(s.Pos.X - k.X) + Math.Abs(s.Pos.Y - k.Y);
        if (working || _bestDist == float.MaxValue || dist >= _bestDist - ProgressEpsilon)
        {
            if (_bestDist == float.MaxValue) _bestDist = dist;
            _noProgress++;
        }
        else
        {
            _bestDist = dist;
            _noProgress = 0;
        }
        int limit = working ? _stuckTicks * WorkStuckMultiplier : _stuckTicks;
        return _noProgress >= limit;
    }

    /// <summary>True if a remaining standing position lost its support or got its body blocked.
    /// Unknown tiles and tiles the path itself will dig/build don't count.</summary>
    private bool PathInvalidated(ITileGrid grid)
    {
        for (int i = _index; i < _steps.Count; i++)
        {
            var s = _steps[i];
            if (s.Kind is MoveKind.Swim or MoveKind.Dig) continue;
            var p = s.Pos;

            for (int bx = p.X; bx <= p.X + 1; bx++)
                for (int by = p.Y - 2; by <= p.Y; by++)
                    if (grid.Get(bx, by).BlocksBody && !IsPlannedWork(new TilePoint(bx, by), MoveKind.Dig, i))
                        return true;

            bool inLiquid = false, supported = false;
            for (int bx = p.X; bx <= p.X + 1; bx++)
            {
                var below = new TilePoint(bx, p.Y + 1);
                var t = grid.Get(below);
                if (t.IsStandable || t.Kind == TileKind.Unknown || IsPlannedWork(below, MoveKind.BuildPlatform, i))
                    supported = true;
                if (grid.Get(bx, p.Y).Kind is TileKind.Water or TileKind.Honey or TileKind.Lava) inLiquid = true;
            }
            if (!supported && !inLiquid) return true;
        }
        return false;
    }

    /// <summary>Is p the WorkTile of a step of this kind at or before step `upTo`?</summary>
    private bool IsPlannedWork(TilePoint p, MoveKind kind, int upTo)
    {
        for (int j = _index; j <= upTo; j++)
            if (_steps[j].Kind == kind && _steps[j].WorkTile == p) return true;
        return false;
    }
}
