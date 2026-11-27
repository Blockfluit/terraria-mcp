using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Nav;

/// <summary>Player state in tiles. X is the hitbox's left edge (the hitbox is <see cref="PlayerPhysics.HitboxWidth"/>
/// = 1.25 tiles wide, centred in the 2-wide body at <c>x + 0.375</c>); Y is the feet row (exact when standing:
/// bottom / 16 - 1). Velocities are in tiles per tick; VelY &lt; 0 means rising. OnGround = vanilla's
/// <c>velocity.Y == 0</c>. Build with <see cref="NavMath.FromPixels"/>.</summary>
public readonly record struct PlayerKinematics(float X, float Y, float VelX, float VelY, bool OnGround);

/// <summary>Controls to apply for one tick. CursorTile, when set, is where to aim the cursor. Up is only used to
/// step up onto platforms (vanilla's Collision.StepUp needs it held for those). Interact, when set, asks the game
/// glue to interact with that tile the way a right-click does (open/close a door); it is not a key press.</summary>
public record struct ControlIntent(bool Left, bool Right, bool Jump, bool Down, bool UseItem, TilePoint? CursorTile, bool Up = false,
    TilePoint? Interact = null);

public enum FollowerStatus { Running, Arrived, Stuck, NeedsReplan }

public readonly record struct FollowerOutput(ControlIntent Intent, FollowerStatus Status, PathStep? Current);

/// <summary>
/// Turns a <see cref="Pathfinder"/> path into per-tick control intents. Call Tick once per game tick.
/// Once Stuck or NeedsReplan is reported the follower stays in that state; build a new one from a fresh path.
///
/// How each step is executed (validated end-to-end against a vanilla-physics sim, FollowerIntegrationTests):
/// <list type="bullet">
/// <item>Horizontal motion steers the hitbox centre to the body centre with a braking-distance speed limit, so the
/// player stops on a waypoint instead of sliding ~1.4 tiles past it. Consecutive same-direction Walk/StepUp/1-tile
/// Fall/Jump steps flow at full speed; the run's last waypoint is the stop point.</item>
/// <item>Jump (|dx| ≥ 2): run to the support edge (or a wall) and take off there, hold jump to the apex, steer and
/// brake in the air. |dx| ≤ 1: take off near the source. Landing back on the source retries (3 attempts); landing
/// anywhere else reports NeedsReplan.</item>
/// <item>Dig: hold the centred stance (so a dug-out floor drops the whole hitbox) and mine WorkTile.</item>
/// <item>BuildPlatform bridging: stop at the source, place, walk on. Climbing: stand centred, jump, place WorkTile
/// once the feet are above it, land on it.</item>
/// <item>BuildDown: stand still at the source and place WorkTile (below the feet), then drop through the platform
/// underfoot (centred, Down for a tick) or walk off the edge, and land on it.</item>
/// <item>DropThrough / straight-down Fall: centre first, then hold Down for a tick (or wait for the dig).</item>
/// <item>Swim (vanilla has no swimming without Flippers): jump off the bottom, holding while rising. With
/// <c>canSwim</c> (Flippers etc.) jump is also pressed again in mid-water whenever the player stops rising.</item>
/// <item>OpenDoor: asks for the door (Interact) already while walking up to it (within
/// <see cref="DoorLookahead"/> steps), so an opened door doesn't break the run; otherwise stops and interacts.
/// CloseDoor: stands still and interacts; given up after <see cref="CloseDoorPatience"/> ticks (someone in the
/// doorway) so a door never strands the path.</item>
/// </list>
/// </summary>
public sealed class PathFollower
{
    private const float ArriveTolerance = 0.5f;
    /// <summary>How far below a waypoint's feet row the feet may be when its support is a floor slope or half block:
    /// the hitbox rides the block's surface, up to (almost) a tile under its top (a 45° slope with the hitbox off
    /// centre by <see cref="ArriveTolerance"/>: 0.875). Landing a full row lower (1.0) still doesn't count.</summary>
    private const float LowTopTolerance = 0.95f;
    private const float AlignTolerance = 0.2f;
    private const float StillSpeed = 0.04f;      // tiles/tick (~0.6 px)
    private const float SettleDistance = 0.08f;
    private const float ProgressEpsilon = 0.1f;
    private const int WorkStuckMultiplier = 4;
    private const int MaxJumpAttempts = 3;
    private const int MaxLookahead = 64;
    private const int ResyncWindow = 12, ValidateWindow = 48;
    public const int CloseDoorPatience = 30, DoorLookahead = 4;

    private readonly IReadOnlyList<PathStep> _steps;
    private readonly int _stuckTicks;
    private readonly float _brake, _gravityPx;
    private int? _ceiling;
    /// <summary>Jump out of a shaft: feet level the body must reach before moving sideways (0 = none), see
    /// <see cref="ShaftLaunch"/>.</summary>
    private int? _shaft;
    private bool _shaftCleared;
    private TilePoint? _prev;
    private int _index;
    private FollowerStatus _terminal = FollowerStatus.Running;

    private float _bestDist = float.MaxValue;
    private int _noProgress;
    private bool _working;
    private bool _airborne, _walkRest;
    private int _attempts;
    private bool _jumpHeld;
    private int _closeTicks;
    private ITileGrid? _grid;
    private readonly bool _canSwim;

    /// <param name="start">Where the path starts (the planner's start); defaults to the standing tile on the first tick.</param>
    /// <param name="physics">Run constants; braking distances are planned with RunSlowdown.</param>
    /// <param name="canSwim">The player can swim up in mid-water (<see cref="MovementProfile.CanSwim"/>).</param>
    public PathFollower(IReadOnlyList<PathStep> steps, int stuckTicks = 90, TilePoint? start = null, PlayerPhysics? physics = null,
        bool canSwim = false)
    {
        _canSwim = canSwim;
        _steps = steps;
        _stuckTicks = stuckTicks;
        _prev = start;
        var phys = physics ?? PlayerPhysics.Vanilla;
        _brake = phys.RunSlowdown / PlayerPhysics.TilePx;
        _gravityPx = phys.Gravity;
    }

    public int RemainingSteps => _steps.Count - _index;

    /// <summary>The steps not yet completed (for overlays).</summary>
    public IEnumerable<PathStep> Remaining
    {
        get
        {
            for (int i = _index; i < _steps.Count; i++) yield return _steps[i];
        }
    }

    public FollowerOutput Tick(PlayerKinematics k, ITileGrid grid)
    {
        if (_terminal != FollowerStatus.Running) return new(default, _terminal, Current);
        _grid = grid;
        _prev ??= Pathfinder.NormalizeStart(grid, NavMath.StandingTile(k, grid));

        if (PathInvalidated(grid)) return Finish(FollowerStatus.NeedsReplan);

        for (int guard = 0; _index < _steps.Count && guard < MaxLookahead; guard++)
        {
            if (!Satisfied(_steps[_index], k, grid)) break;
            Advance();
        }
        Resync(k, grid);
        if (_index >= _steps.Count) return Finish(FollowerStatus.Arrived);

        var step = _steps[_index];
        var intent = new ControlIntent();
        var fail = Control(step, k, grid, ref intent);
        if (fail is { } f) return Finish(f);

        _jumpHeld = intent.Jump;
        if (UpdateStuck(step, k, IsWork(step.Kind) && !WorkDone(step, grid))) return Finish(FollowerStatus.Stuck);
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
        _prev = _steps[_index].Pos;
        _index++;
        _bestDist = float.MaxValue;
        _noProgress = 0;
        _working = false;
        _airborne = false;
        _walkRest = false;
        _attempts = 0;
        _ceiling = null;
        _shaft = null;
        _shaftCleared = false;
        _closeTicks = 0;
    }

    private static float Tx(TilePoint p) => p.X + PlayerPhysics.CenterOffset;
    private static bool IsWork(MoveKind kind) => kind is MoveKind.Dig or MoveKind.BuildPlatform or MoveKind.BuildDown or MoveKind.OpenDoor;
    private static bool IsDoor(MoveKind kind) => kind is MoveKind.OpenDoor or MoveKind.CloseDoor;

    private static bool WorkDone(PathStep s, ITileGrid grid)
    {
        if (s.WorkTile is not { } w) return true;
        var t = grid.Get(w);
        return s.Kind switch
        {
            MoveKind.Dig => !t.IsSolid,
            MoveKind.OpenDoor => !t.BlocksBody,
            MoveKind.CloseDoor => t.BlocksBody,
            _ => t.IsStandable,
        };
    }

    // ---- completion --------------------------------------------------------------------------------------------

    private bool Satisfied(PathStep s, PlayerKinematics k, ITileGrid grid) => s.Kind switch
    {
        MoveKind.Dig or MoveKind.OpenDoor => WorkDone(s, grid),
        MoveKind.CloseDoor => WorkDone(s, grid) || _closeTicks >= CloseDoorPatience,
        MoveKind.BuildPlatform or MoveKind.BuildDown => WorkDone(s, grid) && StandingAt(s.Pos, k),
        MoveKind.Swim => SwimReached(s.Pos, k),
        // Falling/jumping into water: the player sinks (no swimming without Flippers), so anywhere at or below
        // the planned entry point counts; the Swim steps that follow lift it back up.
        _ when BodyInLiquid(grid, s.Pos) => Math.Abs(k.X - Tx(s.Pos)) <= ArriveTolerance && k.Y >= s.Pos.Y - ArriveTolerance,
        _ => StandingAt(s.Pos, k),
    };

    private static bool BodyInLiquid(ITileGrid grid, TilePoint p)
    {
        for (int y = p.Y - 2; y <= p.Y; y++)
            for (int x = p.X; x <= p.X + 1; x++)
                if (grid.Get(x, y).Kind is TileKind.Water or TileKind.Honey) return true;
        return false;
    }

    /// <summary>On the ground at p: within <see cref="ArriveTolerance"/> sideways and of its feet row, or up to
    /// <see cref="LowTopTolerance"/> lower when p stands on a floor slope / half block (the feet sink into its row; on
    /// a slope how far depends on where the hitbox is, so a fixed half-tile window flickered on and off).</summary>
    private bool StandingAt(TilePoint p, PlayerKinematics k)
    {
        if (!k.OnGround || Math.Abs(k.X - Tx(p)) > ArriveTolerance) return false;
        float dy = k.Y - p.Y;
        float below = _grid != null && LowTopSupport(_grid, p) ? LowTopTolerance : ArriveTolerance;
        return dy >= -ArriveTolerance && dy <= below;
    }

    private static bool LowTopSupport(ITileGrid grid, TilePoint p) =>
        grid.Get(p.X, p.Y + 1).IsLowTop || grid.Get(p.X + 1, p.Y + 1).IsLowTop;

    private bool SwimReached(TilePoint p, PlayerKinematics k)
    {
        if (Math.Abs(k.X - Tx(p)) > ArriveTolerance) return false;
        // Rising off the bottom overshoots upward waypoints; being at or above one counts.
        bool upward = _prev is { } prev && p.Y < prev.Y;
        return upward ? k.Y <= p.Y + ArriveTolerance : Math.Abs(k.Y - p.Y) <= ArriveTolerance;
    }

    /// <summary>Landed or drifted onto a later waypoint (e.g. carried past by momentum): skip ahead to it.</summary>
    private void Resync(PlayerKinematics k, ITileGrid grid)
    {
        int end = Math.Min(_steps.Count, _index + ResyncWindow);
        for (int j = end - 1; j > _index; j--)
        {
            var s = _steps[j];
            if (IsWork(s.Kind) || s.Kind == MoveKind.CloseDoor) continue;
            bool there = s.Kind == MoveKind.Swim ? Math.Abs(k.X - Tx(s.Pos)) <= ArriveTolerance && Math.Abs(k.Y - s.Pos.Y) <= ArriveTolerance
                : StandingAt(s.Pos, k);
            if (!there) continue;
            bool workPending = false;
            for (int i = _index; i < j; i++)
                if (IsWork(_steps[i].Kind) && !WorkDone(_steps[i], grid)) workPending = true;
            if (workPending) continue;
            while (_index <= j) Advance();
            return;
        }
    }

    // ---- control -----------------------------------------------------------------------------------------------

    private FollowerStatus? Control(PathStep step, PlayerKinematics k, ITileGrid grid, ref ControlIntent intent)
    {
        var prev = _prev!.Value;
        switch (step.Kind)
        {
            case MoveKind.Walk:
                Flow(k, ref intent);
                intent.Interact = UpcomingDoor(grid);
                return null;

            case MoveKind.OpenDoor:
            case MoveKind.CloseDoor:
                if (step.Kind == MoveKind.CloseDoor) _closeTicks++;
                Steer(Tx(prev), k, ref intent);
                intent.Interact = step.WorkTile;
                return null;

            case MoveKind.StepUp:
                Flow(k, ref intent);
                intent.Up = !grid.Get(step.Pos.X, step.Pos.Y + 1).IsSolid && !grid.Get(step.Pos.X + 1, step.Pos.Y + 1).IsSolid;
                return null;

            case MoveKind.Fall:
                if (step.Pos.X == prev.X) Steer(Tx(step.Pos), k, ref intent); // dig-down or mid-air start
                else if (!k.OnGround && step.Pos.Y - prev.Y > 1) SteerInto(step.Pos, k, ref intent);
                else Flow(k, ref intent);
                return null;

            case MoveKind.DropThrough:
                if (k.OnGround && k.Y < step.Pos.Y - ArriveTolerance)
                {
                    if (Aligned(Tx(prev), k)) intent.Down = true;
                    else Steer(Tx(prev), k, ref intent);
                }
                else SteerInto(step.Pos, k, ref intent);
                return null;

            case MoveKind.Dig:
                Steer(Tx(prev), k, ref intent);
                intent.UseItem = true;
                intent.CursorTile = step.WorkTile;
                return null;

            case MoveKind.BuildPlatform:
                return Build(step, prev, k, grid, ref intent);

            case MoveKind.BuildDown:
                BuildDown(step, prev, k, grid, ref intent);
                return null;

            case MoveKind.Jump:
                return Jump(step, prev, k, grid, ref intent);

            case MoveKind.Swim:
                Steer(Tx(step.Pos), k, ref intent);
                if (step.Pos.Y < k.Y - 0.3f)
                    intent.Jump = k.OnGround ? !_jumpHeld
                        : k.VelY < 0 ? _jumpHeld
                        : _canSwim && !_jumpHeld; // a swimmer strokes again; a non-swimmer waits for the bottom
                return null;
        }
        return null;
    }

    private FollowerStatus? Jump(PathStep step, TilePoint s, PlayerKinematics k, ITileGrid grid, ref ControlIntent intent)
    {
        var t = step.Pos;
        int dx = t.X - s.X, dir = Math.Sign(dx);

        if (!k.OnGround)
        {
            _airborne = true;
            // Hold to the apex (maximum height and air time), unless that would rise past a platform overhead
            // that the descent would then land on.
            _ceiling ??= Pathfinder.JumpPlatformCeiling(grid, s, t, 30);
            intent.Jump = _jumpHeld && k.VelY < 0 && !ApexAbove(k, s, _ceiling.Value);
            _shaft ??= ShaftLaunch(grid, s, t, dir);
            // Out of a shaft: stay inside the source columns (pressed toward the exit side) until the feet clear the
            // shaft's lip, or the head bonks on the overhang beside it.
            // (Only on the way up to that level: afterwards the hitbox is over the target, maybe settling on a slope.)
            if (_shaft > 0 && k.Y <= s.Y - _shaft.Value + 0.05f) _shaftCleared = true;
            if (_shaft > 0 && !_shaftCleared && k.VelY < 0) Steer(EdgeX(s, dir), k, ref intent);
            // Aim for this jump's own landing spot: carrying speed past it lands wherever the arc ends
            // (e.g. on a platform above the planned row). The approach used the run's full speed.
            else SteerInto(t, k, ref intent);
            return null;
        }

        if (_airborne)
        {
            // Landed, but Satisfied() didn't accept it: back on the source (retry), a little short of the target on
            // its row (an obstacle on the way ate some air time: walk the rest), or somewhere else (off the path).
            _airborne = false;
            var here = Pathfinder.NormalizeStart(grid, NavMath.StandingTile(k, grid));
            if (StandingAt(s, k)) here = s;
            if (here.Y == t.Y && Math.Abs(here.X - t.X) <= 2) _walkRest = true;
            else if (here != s) return FollowerStatus.NeedsReplan;
            else if (++_attempts >= MaxJumpAttempts) return FollowerStatus.Stuck;
        }
        if (_walkRest)
        {
            Flow(k, ref intent);
            return null;
        }

        float txs = Tx(s);
        bool ready;
        _shaft ??= ShaftLaunch(grid, s, t, dir);
        if (_shaft > 0)
        {
            // A run-up would carry the hitbox under the overhang / into the hazard: take off standing at the edge.
            _shaftCleared = false; // (a retry after landing back on the source starts over)
            float edge = EdgeX(s, dir);
            if (Aligned(edge, k) && !_jumpHeld) intent.Jump = true;
            Steer(edge, k, ref intent);
            return null;
        }
        if (Math.Abs(dx) <= 1)
            ready = dx == 0 ? Aligned(txs, k) : Math.Abs(k.X - txs) <= 0.6f;
        else
        {
            // Judge by where the hitbox will be next tick: at running speed (0.19 tiles/tick) the takeoff window
            // is only a couple of ticks wide, and running into a 1-tile bump would auto-step us off the source.
            var next = k with { X = k.X + k.VelX };
            float along = (next.X - txs) * dir;
            ready = along >= -0.25f
                    && (along >= 0.375f || LosingSupport(k, grid, s.Y + 1) || Blocked(next, grid, dir));
        }
        if (ready && !_jumpHeld) intent.Jump = true;

        if (dir == 0) Steer(txs, k, ref intent);
        else Flow(k, ref intent);
        return null;
    }

    private FollowerStatus? Build(PathStep step, TilePoint s, PlayerKinematics k, ITileGrid grid, ref ControlIntent intent)
    {
        bool done = WorkDone(step, grid);
        var w = step.WorkTile ?? step.Pos + new TilePoint(0, 1);
        bool climbing = step.Pos.Y < s.Y;

        if (!climbing)
        {
            if (done)
            {
                Flow(k, ref intent);
                return null;
            }
            Steer(Tx(s), k, ref intent); // stop at the edge first: momentum would carry us off it
            intent.UseItem = true;
            intent.CursorTile = w;
            return null;
        }

        float txs = Tx(s);
        if (!k.OnGround)
        {
            _airborne = true;
            _ceiling ??= Pathfinder.JumpPlatformCeiling(grid, s, step.Pos, 30);
            intent.Jump = _jumpHeld && k.VelY < 0 && !ApexAbove(k, s, _ceiling.Value);
            Steer(txs, k, ref intent);
            // Place it once the feet are above the work tile, then land on it.
            if (!done && k.Y <= w.Y - 1 + 0.05f)
            {
                intent.UseItem = true;
                intent.CursorTile = w;
            }
            return null;
        }

        if (_airborne)
        {
            _airborne = false;
            if (++_attempts >= MaxJumpAttempts) return FollowerStatus.Stuck;
        }
        Steer(txs, k, ref intent);
        if (Aligned(txs, k) && !_jumpHeld && k.Y > step.Pos.Y + ArriveTolerance) intent.Jump = true;
        return null;
    }

    private void BuildDown(PathStep step, TilePoint s, PlayerKinematics k, ITileGrid grid, ref ControlIntent intent)
    {
        bool onSource = k.OnGround && k.Y < step.Pos.Y - ArriveTolerance;
        if (!WorkDone(step, grid))
        {
            // Place it from the source; never leave before it exists (the drop below isn't safe).
            if (onSource) Steer(Tx(s), k, ref intent);
            else SteerInto(step.Pos, k, ref intent);
            intent.UseItem = onSource;
            intent.CursorTile = step.WorkTile;
            return;
        }
        if (!onSource)
        {
            SteerInto(step.Pos, k, ref intent);
            return;
        }
        if (step.Pos.X == s.X)
        {
            // Through the platform underfoot: centre over the new one first, then Down for a tick.
            if (Aligned(Tx(s), k)) intent.Down = true;
            else Steer(Tx(s), k, ref intent);
        }
        else Steer(Tx(step.Pos), k, ref intent); // walk off the edge (slowly: the landing is right below)
    }

    /// <summary>Hitbox left edge pressed toward the dir side of the body at s, without entering the next column.</summary>
    private static float EdgeX(TilePoint s, int dir) => dir > 0 ? s.X + 2f - PlayerPhysics.HitboxWidth - 0.15f : s.X + 0.15f;

    /// <summary>
    /// A jump from s to t whose arc must not drift sideways before the feet reach the launch level (the lowest level from
    /// which the body crosses the whole span; below it something is in the way). Usually that something is a wall that
    /// simply stops the hitbox, but two cases hurt (both seen in the real-terrain fuzz):
    /// <list type="bullet">
    /// <item>A shaft: the column beside the source on t's side is open at the source body's height but blocked higher,
    /// below the launch level. A hitbox that drifts into it while rising bonks on that overhang, and the jump fails
    /// (running to the edge of a 2-wide shaft, jumping, head into the rock beside it, into the pit below).</item>
    /// <item>Cactus / lava / spikes below the launch level between source and target: the run-up carried the hitbox
    /// into them before the feet had risen above them.</item>
    /// </list>
    /// Returns that launch level (feet tiles above s.Y) for such a jump, else 0. The follower then takes off standing and
    /// keeps the hitbox inside the source columns until the feet are up there.
    /// </summary>
    internal static int ShaftLaunch(ITileGrid grid, TilePoint s, TilePoint t, int dir)
    {
        if (dir == 0) return 0;
        static bool Deadly(TileInfo i) => i.Kind is TileKind.Lava or TileKind.Hazard;
        int x0 = Math.Min(s.X, t.X), x1 = Math.Max(s.X, t.X) + 1;
        // As the planner (Pathfinder.Jumps): the crossing level, then down while every lower level is clear across.
        bool ClearAt(int a)
        {
            for (int x = x0; x <= x1; x++)
                for (int r = s.Y - a - 2; r <= s.Y - a; r++)
                {
                    var i = grid.Get(x, r);
                    if (i.BlocksBody || Deadly(i)) return false;
                }
            return true;
        }
        int launch = s.Y - Math.Min(s.Y, t.Y) + 1;
        while (launch > 0 && ClearAt(launch - 1)) launch--;
        if (launch <= 0) return 0;
        int c = dir > 0 ? s.X + 2 : s.X - 1;
        // Hazards anywhere the hitbox could drift through below the launch level.
        int cx0 = dir > 0 ? s.X + 2 : x0, cx1 = dir > 0 ? x1 : s.X - 1;
        for (int x = cx0; x <= cx1; x++)
            for (int r = s.Y - launch - 2; r <= s.Y; r++)
                if (Deadly(grid.Get(x, r))) return launch;
        for (int r = s.Y - 2; r <= s.Y; r++)
            if (grid.Get(c, r).BlocksBody) return 0; // a wall right beside: the hitbox can't drift into it
        for (int r = s.Y - launch - 2; r <= s.Y - 3; r++)
            if (grid.Get(c, r).BlocksBody) return launch;
        return 0;
    }

    /// <summary>Would releasing jump now still carry the feet above `levels` tiles over the source feet row?</summary>
    private bool ApexAbove(PlayerKinematics k, TilePoint s, int levels)
    {
        if (levels == int.MaxValue) return false;
        float risePx = (s.Y - k.Y) * PlayerPhysics.TilePx, v = -k.VelY * PlayerPhysics.TilePx;
        float apex = risePx + (v > 0 ? v * v / (2f * _gravityPx) + v / 2f : 0f);
        return apex >= levels * PlayerPhysics.TilePx - 2f;
    }

    private static bool Aligned(float target, PlayerKinematics k) =>
        Math.Abs(k.X - target) <= AlignTolerance && Math.Abs(k.VelX) <= StillSpeed;

    /// <summary>Would the hitbox be off its support row two ticks from now?</summary>
    private static bool LosingSupport(PlayerKinematics k, ITileGrid grid, int supportRow)
    {
        float x = k.X + 2 * k.VelX;
        int c0 = (int)MathF.Floor(x + 0.01f), c1 = (int)MathF.Floor(x + PlayerPhysics.HitboxWidth - 0.01f);
        for (int c = c0; c <= c1; c++)
            if (grid.Get(c, supportRow).IsStandable) return false;
        return true;
    }

    /// <summary>A solid tile right in front of the hitbox at body height.</summary>
    private static bool Blocked(PlayerKinematics k, ITileGrid grid, int dir)
    {
        int col = dir > 0 ? (int)MathF.Floor(k.X + PlayerPhysics.HitboxWidth + 0.05f) : (int)MathF.Floor(k.X - 0.05f);
        int feet = (int)MathF.Floor(k.Y + 1f - 1f / 16f);
        for (int r = feet - 2; r <= feet; r++)
            if (grid.Get(col, r).BlocksBody) return true;
        return false;
    }

    /// <summary>The door of an OpenDoor step coming up within <see cref="DoorLookahead"/> steps (only plain walks in
    /// between), still closed: ask for it early so the walk can flow through.</summary>
    private TilePoint? UpcomingDoor(ITileGrid grid)
    {
        int end = Math.Min(_steps.Count, _index + 1 + DoorLookahead);
        for (int j = _index + 1; j < end; j++)
        {
            var s = _steps[j];
            if (s.Kind == MoveKind.OpenDoor) return WorkDone(s, grid) ? null : s.WorkTile;
            if (s.Kind != MoveKind.Walk) return null;
        }
        return null;
    }

    /// <summary>Steer toward the stop point of the run of same-direction flowing steps starting here.</summary>
    private void Flow(PlayerKinematics k, ref ControlIntent intent)
    {
        var prev = _prev!.Value;
        float target = Tx(_steps[_index].Pos);
        int dir0 = 0;
        bool airborneStep = false; // at most one jump/fall per run: where it lands must stay under control
        int end = Math.Min(_steps.Count, _index + MaxLookahead);
        for (int j = _index; j < end; j++)
        {
            var s = _steps[j];
            // A door that is already open (or shut) behind us doesn't stop the run.
            if (j > _index && IsDoor(s.Kind) && _grid != null && WorkDone(s, _grid)) continue;
            int d = Math.Sign(s.Pos.X - prev.X);
            bool air = s.Kind is MoveKind.Jump or MoveKind.Fall;
            if (j > _index)
            {
                if (!Flowing(s, prev) || d == 0) break;
                if (dir0 != 0 && d != dir0) break;
                if (air && airborneStep) break;
            }
            airborneStep |= air;
            if (d != 0) dir0 = d;
            target = Tx(s.Pos);
            prev = s.Pos;
        }
        Steer(target, k, ref intent);
    }

    /// <summary>Steps that can be entered at running speed without overshooting into trouble.</summary>
    private static bool Flowing(PathStep s, TilePoint prev) => s.Kind switch
    {
        MoveKind.Walk or MoveKind.StepUp => true,
        MoveKind.Fall => s.Pos.X != prev.X && s.Pos.Y - prev.Y <= 1,
        MoveKind.Jump => s.Pos.X != prev.X,
        _ => false,
    };

    /// <summary>
    /// In the air, landing matters more than stopping precisely: get the whole hitbox over the target body's two
    /// columns ([t.X, t.X + 0.75] for the left edge; a neighbouring bump would otherwise catch it) as fast as
    /// still allows stopping before the window's far side (planned with the conservative RunSlowdown), then centre.
    /// </summary>
    private void SteerInto(TilePoint t, PlayerKinematics k, ref ControlIntent intent)
    {
        float lo = t.X, hi = t.X + 2f - PlayerPhysics.HitboxWidth;
        if (k.X >= lo && k.X <= hi)
        {
            Steer(Tx(t), k, ref intent);
            return;
        }
        int dir = k.X < lo ? 1 : -1;
        float room = dir > 0 ? hi - k.X : k.X - lo; // distance to the far side of the window
        float allowed = MathF.Sqrt(2f * _brake * Math.Max(0f, room - 0.15f));
        float toward = k.VelX * dir;
        intent.Right = toward < allowed ? dir > 0 : dir < 0;
        intent.Left = toward < allowed ? dir < 0 : dir > 0;
    }

    /// <summary>
    /// Bang-bang horizontal control with a braking-distance speed limit: the speed toward the target may not exceed
    /// sqrt(2·a·d), with a = RunSlowdown (pressing the opposite key brakes harder, 0.28 px/tick², so this stops short
    /// rather than past). Settles within <see cref="SettleDistance"/>.
    /// </summary>
    private void Steer(float target, PlayerKinematics k, ref ControlIntent intent)
    {
        float d = target - k.X;
        if (Math.Abs(d) <= SettleDistance && Math.Abs(k.VelX) <= StillSpeed) return;
        int dir = d > 0 ? 1 : d < 0 ? -1 : -Math.Sign(k.VelX);
        float toward = k.VelX * dir;
        float allowed = MathF.Sqrt(2f * _brake * Math.Max(0f, Math.Abs(d) - 0.04f));
        const float band = 0.004f;
        if (toward > allowed + band)
        {
            intent.Right = dir < 0;
            intent.Left = dir > 0;
        }
        else if (toward < allowed - band)
        {
            intent.Right = dir > 0;
            intent.Left = dir < 0;
        }
    }

    // ---- stuck / invalidation ----------------------------------------------------------------------------------

    private bool UpdateStuck(PathStep s, PlayerKinematics k, bool working)
    {
        if (working != _working)
        {
            _working = working;
            _bestDist = float.MaxValue;
            _noProgress = 0;
        }
        float dist = Math.Abs(Tx(s.Pos) - k.X) + Math.Abs(s.Pos.Y - k.Y);
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

    /// <summary>True if one of the next <see cref="ValidateWindow"/> standing positions lost its support or got its
    /// body blocked (later ones are checked as they come closer; the live grid isn't free to read).
    /// Unknown tiles and tiles the path itself will dig/build don't count.</summary>
    private bool PathInvalidated(ITileGrid grid)
    {
        int end = Math.Min(_steps.Count, _index + ValidateWindow);
        for (int i = _index; i < end; i++)
        {
            var s = _steps[i];
            if (s.Kind is MoveKind.Swim or MoveKind.Dig or MoveKind.OpenDoor or MoveKind.CloseDoor) continue;
            var p = s.Pos;

            for (int bx = p.X; bx <= p.X + 1; bx++)
                for (int by = p.Y - 2; by <= p.Y; by++)
                {
                    var t = grid.Get(bx, by);
                    // A floor slope / half block in the feet row is walked over (the hitbox rides its surface);
                    // the start body can overlap one (the plain feet row of a hitbox sunk into a slope).
                    if (by == p.Y && t.IsLowTop) continue;
                    if (t.BlocksBody && !IsPlannedWork(new TilePoint(bx, by), MoveKind.Dig, i)
                        && !(t.IsClosedDoor && IsPlannedDoor(bx, by, i)))
                        return true;
                }

            bool inLiquid = false, supported = false;
            for (int bx = p.X; bx <= p.X + 1; bx++)
            {
                var below = new TilePoint(bx, p.Y + 1);
                var t = grid.Get(below);
                if (t.IsStandable || t.Kind == TileKind.Unknown || IsPlannedWork(below, MoveKind.BuildPlatform, i)
                    || IsPlannedWork(below, MoveKind.BuildDown, i))
                    supported = true;
                if (grid.Get(bx, p.Y).Kind is TileKind.Water or TileKind.Honey or TileKind.Lava) inLiquid = true;
            }
            if (!supported && !inLiquid) return true;
        }
        return false;
    }

    /// <summary>Does an OpenDoor step at or before `upTo` open the door that (x,y) belongs to (same column, a door is
    /// 3 tall)?</summary>
    private bool IsPlannedDoor(int x, int y, int upTo)
    {
        for (int j = _index; j <= upTo; j++)
            if (_steps[j].Kind == MoveKind.OpenDoor && _steps[j].WorkTile is { } w && w.X == x && Math.Abs(w.Y - y) <= 2)
                return true;
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
