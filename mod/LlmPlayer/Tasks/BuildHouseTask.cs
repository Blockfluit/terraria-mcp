using LlmPlayer.Control;
using LlmPlayer.Core.Build;
using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Inventory;
using LlmPlayer.Core.Nav;
using LlmPlayer.Core.Tasks;
using LlmPlayer.Interact;
using LlmPlayer.Observe;
using Terraria;
using Terraria.ID;

namespace LlmPlayer.Tasks;

/// <summary>What build_house reports in the task's <c>result</c>.</summary>
public sealed record HouseResult(int X, int Y, int Width, int Height, string Door, bool Valid, string? Reason,
    Dictionary<string, int> Placed, int Skipped, Dictionary<string, int> SkippedReasons, bool ExitedThroughDoor, HousingVerdict? Housing);

/// <summary>
/// build_house: place a whole house the way a player builds one: walk inside the plot, hold each item, aim at each
/// tile and use it. Every tile, wall and piece of furniture goes through vanilla ItemCheck/PlaceThing (reach, anchors,
/// item consumed); nothing is set directly.
/// <list type="bullet">
/// <item>Order from <see cref="HousePlan.Steps"/>: frame (floor, far wall, ceiling, door-side wall hanging from it),
/// door, background walls, table, chair, torch. The command checked the site and the bill of materials and did any
/// crafting before starting.</item>
/// <item>The player stands inside on the floor (<see cref="HousePlan.InsideStand"/>), on the spot that reaches the next
/// tile and the most following ones; the first walk there may dig/build, every later walk may not (it would break or
/// clutter the house). The door gap stays open until the door goes in, then <see cref="MoveToTask"/> opens and closes
/// the door itself.</item>
/// <item>A frame tile, the door or a piece of furniture that can't be placed fails the task (the house couldn't be valid);
/// a background wall that can't be placed is skipped and reported.</item>
/// <item>Finally the player walks out through the door (closing it behind) and vanilla's housing query
/// (<see cref="Housing.Query"/>) decides: Succeeded when the house is valid, else Failed with vanilla's reason. The
/// result carries the counts either way.</item>
/// </list>
/// </summary>
public sealed class BuildHouseTask : IGameTask<GameCtx>
{
    public const int MaxAttempts = 3, AttemptTicks = 20, MaxMoves = 3, HoldTimeoutTicks = 300;

    private enum Phase { Build, Exit, Done }

    private readonly HouseLayout _l;
    private readonly List<HouseStep> _steps;
    private readonly bool _canExit;
    private readonly HeldItemLease _lease = new();
    private readonly SkipReport _skips = new();
    private readonly Dictionary<string, int> _placed = new();

    private Phase _phase = Phase.Build;
    private int _idx, _moves, _attempts, _attemptTick, _holdTicks, _placedTotal;
    private bool _attempting, _exited;
    private MoveToTask? _move;
    private HousingVerdict? _verdict;

    public string Name => "build_house";
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }

    public object? Result => new HouseResult(_l.X, _l.FloorY, _l.Width, _l.Height, _l.Door == DoorSide.Left ? "left" : "right",
        _verdict?.Valid ?? false, _verdict is { Valid: false } v ? v.Message : null, new Dictionary<string, int>(_placed),
        _skips.Count, _skips.ByReason, _exited, _verdict);

    private MineTask? _clear;

    /// <param name="clearFirst">Natural decoration in the plot (rock piles, dye plants) to break before building.</param>
    public BuildHouseTask(HouseLayout layout, List<HouseStep> steps, bool canExit, List<TilePoint>? clearFirst = null)
    {
        if (clearFirst is { Count: > 0 }) _clear = new MineTask(clearFirst, "build_house");
        _l = layout;
        _steps = steps;
        _canExit = canExit;
        Progress = $"0/{_steps.Count} steps";
    }

    public GameTaskStatus Tick(GameCtx ctx)
    {
        var p = ctx.Player;
        if (p.noBuilding) return Finish(ctx, GameTaskStatus.Failed, "building isn't possible here (noBuilding)");
        if (_clear != null)
        {
            var cs = _clear.Tick(ctx);
            Progress = $"clearing decoration from the plot ({_clear.Progress})";
            if (cs == GameTaskStatus.Running) return GameTaskStatus.Running;
            string? why = _clear.Reason;
            _clear = null;
            if (cs == GameTaskStatus.Failed) return Finish(ctx, GameTaskStatus.Failed, $"couldn't clear the plot: {why}");
        }
        return _phase switch
        {
            Phase.Build => TickBuild(ctx, p),
            Phase.Exit => TickExit(ctx),
            _ => Conclude(ctx),
        };
    }

    // ---- building ----------------------------------------------------------------------------------------------

    private GameTaskStatus TickBuild(GameCtx ctx, Player p)
    {
        for (int pass = 0; pass < 4; pass++)
        {
            if (_idx >= _steps.Count)
            {
                _lease.Restore(p);
                ControlState.Pending = default;
                _phase = _canExit ? Phase.Exit : Phase.Done;
                return GameTaskStatus.Running;
            }
            var step = _steps[_idx];
            if (IsDone(step))
            {
                if (_attempting)
                {
                    string key = PartName(step.Part);
                    _placed[key] = _placed.GetValueOrDefault(key) + 1;
                    _placedTotal++;
                }
                NextStep(ctx);
                continue;
            }
            if (BlockedBy(step) is { } what)
            {
                if (step.Part == HousePart.BackWall) { _skips.Add(step.Target, what); NextStep(ctx); continue; }
                return Finish(ctx, GameTaskStatus.Failed, $"{PartName(step.Part)} at {step.Target} is blocked by {what}");
            }

            int slot = HouseItems.SlotFor(p, step.Part);
            if (slot < 0) return Finish(ctx, GameTaskStatus.Failed, $"out of {Needed(step.Part)} (placed {Summary()})");
            var item = p.inventory[slot];
            bool solid = step.Part == HousePart.Frame;
            var aim = Aim(step);

            if (_move != null)
            {
                var st = _move.Tick(ctx);
                if (st == GameTaskStatus.Running)
                {
                    Progress = $"{_idx}/{_steps.Count} steps, walking ({_move.Progress})";
                    return GameTaskStatus.Running;
                }
                _move = null;
                ControlState.Pending = default;
            }

            int range = p.blockRange; // vanilla adds blockRange for every item that places a tile or wall
            bool inReach = ReachMath.InItemRange(p.position.X, p.position.Y, p.width, p.height,
                Player.tileRangeX + range, Player.tileRangeY + range, item.tileBoost, aim.X, aim.Y);
            bool overlaps = solid && ReachMath.OverlapsBody(p.position.X, p.position.Y, p.width, p.height, aim.X, aim.Y);
            if (!inReach || overlaps)
            {
                if (p.itemAnimation > 0) return AimAt(aim, use: false);
                if (_moves >= MaxMoves)
                {
                    if (step.Part == HousePart.BackWall) { _skips.Add(step.Target, "out of reach"); NextStep(ctx); continue; }
                    return Finish(ctx, GameTaskStatus.Failed, $"couldn't get within reach of the {PartName(step.Part)} at {aim} (placed {Summary()})");
                }
                _moves++;
                _attempting = false;
                StartMove(ctx, p, item, solid, range);
                Progress = $"{_idx}/{_steps.Count} steps, walking to place the {PartName(step.Part)} at {aim}";
                return GameTaskStatus.Running;
            }

            if (!_lease.Hold(p, slot))
            {
                if (++_holdTicks > HoldTimeoutTicks) return Finish(ctx, GameTaskStatus.Failed, "couldn't switch to the building item (busy using another item)");
                Progress = "waiting to switch items";
                return AimAt(aim, use: false);
            }

            _attempting = true;
            if (_attemptTick >= AttemptTicks)
            {
                _attemptTick = 0;
                if (++_attempts >= MaxAttempts)
                {
                    if (step.Part == HousePart.BackWall) { _skips.Add(step.Target, "game refused to place"); NextStep(ctx); continue; }
                    return Finish(ctx, GameTaskStatus.Failed,
                        $"the game refused to place the {PartName(step.Part)} ({item.Name}) at {aim} after {MaxAttempts} attempts " +
                        $"(something in the way, or nothing to attach to); placed {Summary()}");
                }
                return AimAt(Aim(step), use: false);
            }
            _attemptTick++;
            Progress = $"{_idx}/{_steps.Count} steps: placing the {PartName(step.Part)}";
            return AimAt(aim, use: true);
        }
        return GameTaskStatus.Running;
    }

    private void NextStep(GameCtx ctx)
    {
        _move?.OnCancel(ctx);
        _move = null;
        _idx++;
        _moves = _attempts = _attemptTick = _holdTicks = 0;
        _attempting = false;
    }

    /// <summary>The tile to aim at. A door is tried at its middle tile first, then the top and the bottom (vanilla
    /// picks the door's origin row from the cursor and its anchors).</summary>
    private TilePoint Aim(HouseStep step) =>
        step.Part == HousePart.Door ? _l.DoorTiles[new[] { 1, 0, 2 }[_attempts % 3]] : step.Target;

    private void StartMove(GameCtx ctx, Player p, Item item, bool solid, int range)
    {
        var k = NavMath.FromPixels(p.position.X, p.position.Y, p.velocity.X, p.velocity.Y, p.height);
        var here = NavMath.StandingTile(k, ctx.Grid);
        var upcoming = new List<TilePoint> { Aim(_steps[_idx]) };
        for (int i = _idx + 1; i < _steps.Count && upcoming.Count < 8; i++)
            if (_steps[i].Part == _steps[_idx].Part) upcoming.Add(_steps[i].Target);
        var reach = new ReachSpec(Player.tileRangeX + range, Player.tileRangeY + range, item.tileBoost);
        var stand = HousePlan.InsideStand(_l, here, upcoming, reach, solid, s => Pathfinder.CanStand(ctx.Grid, s))
                    ?? (upcoming[0].Y == _l.FloorY ? PlatformPlan.BestStand(ctx.Grid, here, upcoming, reach, solid) : null);
        // Before anything is placed the walk may dig and build to get to the plot; afterwards it must not touch the house.
        bool free = _placedTotal == 0;
        _move = stand is { } s
            ? new MoveToTask(s, allowDig: free, allowBuild: free, tolerance: 0, name: Name)
            : new MoveToTask(_l.CheckTile, allowDig: free, allowBuild: free, tolerance: 1, name: Name);
        ControlState.Pending = default;
    }

    private GameTaskStatus AimAt(TilePoint t, bool use)
    {
        ControlState.Pending = default(ControlIntent) with { UseItem = use, CursorTile = t };
        return GameTaskStatus.Running;
    }

    // ---- leaving and checking ----------------------------------------------------------------------------------

    private GameTaskStatus TickExit(GameCtx ctx)
    {
        _move ??= new MoveToTask(_l.OutsideStand, allowDig: false, allowBuild: false, tolerance: 0, name: Name);
        var st = _move.Tick(ctx);
        if (st == GameTaskStatus.Running)
        {
            Progress = $"walking out through the door ({_move.Progress})";
            return GameTaskStatus.Running;
        }
        _exited = st == GameTaskStatus.Succeeded;
        _move = null;
        ControlState.Pending = default;
        CloseDoorIfOpen(ctx.Player);
        _phase = Phase.Done;
        return GameTaskStatus.Running;
    }

    /// <summary>An open door lets the room check leak out: close it like a right-click if the walk left it open.</summary>
    private void CloseDoorIfOpen(Player p)
    {
        foreach (var t in _l.DoorTiles)
        {
            var tile = Main.tile[t.X, t.Y];
            if (tile.HasTile && TileID.Sets.CloseDoorID[tile.TileType] != -1 && DoorActions.InReach(p, t.X, t.Y))
            {
                DoorActions.TryClose(p, t.X, t.Y);
                return;
            }
        }
    }

    private GameTaskStatus Conclude(GameCtx ctx)
    {
        _verdict = Housing.Query(_l.CheckTile.X, _l.CheckTile.Y);
        string exit = _canExit ? (_exited ? "" : " (couldn't walk out through the door)") : " (no room to stand outside the door)";
        string skipped = _skips.Count > 0 ? $"; skipped {_skips.Count} ({_skips.Summary()})" : "";
        return _verdict.Valid
            ? Finish(ctx, GameTaskStatus.Succeeded, $"house built at ({_l.X},{_l.FloorY}): {_verdict.Message}{exit}{skipped}")
            : Finish(ctx, GameTaskStatus.Failed, $"house built at ({_l.X},{_l.FloorY}) but vanilla says: {_verdict.Message}{exit}{skipped}");
    }

    public void OnCancel(GameCtx ctx)
    {
        _clear?.OnCancel(ctx);
        _move?.OnCancel(ctx);
        _move = null;
        ControlState.Pending = default;
        _lease.Restore(ctx.Player);
    }

    private GameTaskStatus Finish(GameCtx ctx, GameTaskStatus status, string reason)
    {
        Reason = reason;
        Progress = $"{Math.Min(_idx, _steps.Count)}/{_steps.Count} steps";
        OnCancel(ctx);
        return status;
    }

    // ---- game facts --------------------------------------------------------------------------------------------

    private bool IsDone(HouseStep s)
    {
        if (s.Part == HousePart.Door)
            return _l.DoorTiles.Any(t => Main.tile[t.X, t.Y] is { HasTile: true } tile && IsIn(TileID.Sets.RoomNeeds.CountsAsDoor, tile.TileType));
        var tile = Main.tile[s.Target.X, s.Target.Y];
        return s.Part switch
        {
            HousePart.Frame => tile.HasTile && !tile.IsActuated && Main.tileSolid[tile.TileType],
            HousePart.BackWall => tile.WallType != WallID.None && Main.wallHouse[tile.WallType],
            HousePart.Table => tile.HasTile && IsIn(TileID.Sets.RoomNeeds.CountsAsTable, tile.TileType),
            HousePart.Chair => tile.HasTile && IsIn(TileID.Sets.RoomNeeds.CountsAsChair, tile.TileType),
            HousePart.Torch => tile.HasTile && IsIn(TileID.Sets.RoomNeeds.CountsAsTorch, tile.TileType),
            _ => false,
        };
    }

    /// <summary>Something else took the spot since the site check (cuttable plants don't count: placing replaces them).</summary>
    private string? BlockedBy(HouseStep s)
    {
        if (s.Part == HousePart.BackWall)
        {
            var w = Main.tile[s.Target.X, s.Target.Y];
            return w.WallType != WallID.None && !Main.wallHouse[w.WallType] ? "a natural wall" : null;
        }
        if (s.Part == HousePart.Door) return null; // the door's own tiles are checked by IsDone; vanilla decides the rest
        var t = Main.tile[s.Target.X, s.Target.Y];
        return t.HasTile && !Main.tileCut[t.TileType] ? TileClass.Name(t.TileType) : null;
    }

    private static bool IsIn(int[] set, int type) => Array.IndexOf(set, type) >= 0;

    private static string PartName(HousePart part) => part switch
    {
        HousePart.Frame => "frame block",
        HousePart.Door => "door",
        HousePart.BackWall => "background wall",
        HousePart.Table => "table",
        HousePart.Chair => "chair",
        _ => "torch",
    };

    private static string Needed(HousePart part) => part switch
    {
        HousePart.Frame => "blocks for the frame",
        HousePart.Door => "doors",
        HousePart.BackWall => "background walls",
        HousePart.Table => "tables/work benches",
        HousePart.Chair => "chairs",
        _ => "torches",
    };

    private string Summary() => _placed.Count == 0 ? "nothing" : string.Join(", ", _placed.Select(kv => $"{kv.Value} {kv.Key}"));
}
