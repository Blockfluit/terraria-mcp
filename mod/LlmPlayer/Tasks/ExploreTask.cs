using LlmPlayer.Control;
using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using LlmPlayer.Core.Tasks;
using LlmPlayer.Nav;
using LlmPlayer.Observe;
using Terraria;
using Terraria.ID;

namespace LlmPlayer.Tasks;

public enum ExploreDirection { Left, Right, Down, Up }

/// <summary>
/// Travel <c>distance</c> tiles in a direction. Succeeds once the distance is covered (within 1 tile).
/// <list type="bullet">
/// <item><b>Walking</b>: each leg is a <see cref="MoveToTask"/> to the visible standable tile farthest along the
/// direction (fair vision: Unknown tiles are never picked) within a window ahead.</item>
/// <item><b>Digging</b> (<c>allowDig</c>, needs a pickaxe; not upward): when no visible ground ahead exists or the
/// last leg made no progress, dig a chunk planned by <see cref="DigPlanner"/>: a 2-wide shaft under the body (down;
/// the player drops as the floor goes, after lining its hitbox up over the two columns) or a 3-tall tunnel (left/right,
/// walked into by the next leg). Tiles are broken by <see cref="MineTask"/> (real swings, vanilla reach and pick
/// power). Stops with the planner's reason: lava/liquid next to the hole, protected, too hard, unsafe drop.</item>
/// <item><b>Light</b>: a dig target that's Unknown because it's dark gets a torch placed near it (<see cref="PlaceTask"/>)
/// and is planned again; no torch → "too dark: no torches". With <c>torches</c>, also places one whenever the body is
/// dark underground (or while digging) and no torch is within <see cref="TorchPlanner.SpacingTiles"/>.</item>
/// <item>Fails after <see cref="MaxStalls"/> sub-tasks in a row without progress (3 when it can't dig).</item>
/// </list>
/// Result: <see cref="ExploreResult"/> {covered, dug, torchesPlaced, stoppedBecause?}.
/// </summary>
public sealed class ExploreTask : IGameTask<GameCtx>
{
    private const int MaxStalls = 5, MaxWalkStalls = 3, LegReach = 40, LateralReach = 15, ArrivalTolerance = 1;
    private const int MaxDigFailures = 3, MaxTorchFailures = 3, TorchCheckTicks = 60, LightSettleTicks = 15, AlignTimeoutTicks = 180;
    private const int TunnelScanMax = 12;

    private readonly ExploreDirection _dir;
    private readonly int _distance;
    private readonly bool _allowDig, _torches;
    private TilePoint? _origin;
    private int _bestAlong, _stallBest, _stalls, _covered;

    private MoveToTask? _leg;
    private MineTask? _mine;
    private List<TilePoint>? _mineTiles;
    private PlaceTask? _torch;
    private TilePoint _torchSpot;
    private bool _torchForDark;
    private TilePoint? _alignTo; // line the hitbox up over this stand's two columns before digging a shaft chunk
    private int _alignTicks;

    private bool _digNext;
    private int _dug, _torchesPlaced, _digFailures, _torchFailures, _torchCheckIn, _settle;
    private TilePoint? _darkFixAt;
    private readonly HashSet<TilePoint> _badTorchSpots = new();
    private string _mode = "walking";
    private string? _stopped, _lastDigStop;
    private string LastDigStop => _lastDigStop != null ? $" (digging stopped: {_lastDigStop})" : "";

    public string Name => "explore";
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }
    public object? Result => new ExploreResult(_covered, _dug, _torchesPlaced, _stopped);

    public ExploreTask(ExploreDirection dir, int distance, bool allowDig = true, bool torches = true)
    {
        _dir = dir;
        _distance = Math.Clamp(distance, 1, 500);
        _allowDig = allowDig;
        _torches = torches;
    }

    public GameTaskStatus Tick(GameCtx ctx)
    {
        var here = Here(ctx);
        _origin ??= here;
        int along = Along(here - _origin.Value);
        _bestAlong = Math.Max(_bestAlong, along);
        _covered = Math.Max(0, along);
        UpdateProgress();
        // Legs are MoveToTasks with tolerance 1, so the last one may stop a tile short: that counts as covered.
        if (along >= _distance - ArrivalTolerance)
        {
            CancelSubTasks(ctx);
            return GameTaskStatus.Succeeded;
        }

        if (_torch != null) return TickTorch(ctx);
        if (_alignTo != null) return TickAlign(ctx, here);
        if (_mine != null) return TickMine(ctx, here);
        if (_leg != null)
        {
            if (TickLeg(ctx, here) is { } st) return st;
        }
        if (_settle > 0)
        {
            _settle--; // let the light from a new torch spread before planning against it
            ControlState.Pending = default;
            return GameTaskStatus.Running;
        }

        if (_torches && --_torchCheckIn <= 0)
        {
            _torchCheckIn = TorchCheckTicks;
            if (TryAutoTorch(ctx, here)) return GameTaskStatus.Running;
        }
        return Decide(ctx, here);
    }

    public void OnCancel(GameCtx ctx) => CancelSubTasks(ctx);

    // ---- decisions ---------------------------------------------------------------------------------------------

    private GameTaskStatus Decide(GameCtx ctx, TilePoint here)
    {
        bool hasTarget = PickTarget(ctx, here, out var target);
        int power = HeldItemLease.PickPower(ctx.Player);
        bool canDig = _allowDig && power > 0 && _dir != ExploreDirection.Up;

        if (hasTarget && (!_digNext || !canDig)) return StartLeg(target, Along(here - _origin!.Value));
        if (!canDig)
        {
            string why = _dir == ExploreDirection.Up ? "; digging upward isn't supported (move_to a spot above builds platforms)"
                : !_allowDig ? " (allow_dig is off)"
                : " and no pickaxe to dig with";
            return Fail($"no visible ground further {DirName} (covered {_covered}/{_distance} tiles){why}");
        }

        var plan = Plan(ctx, here, power);
        if (plan.HasWork)
        {
            _digNext = false;
            if (_dir == ExploreDirection.Down)
            {
                _alignTo = plan.Stand;
                _alignTicks = 0;
            }
            _mineTiles = plan.Tiles.ToList();
            _mine = new MineTask(_mineTiles, name: "explore");
            _mode = "digging";
            return GameTaskStatus.Running;
        }

        if (plan.Stop is not { } stop)
        {
            // Everything ahead within the scan is already open: walking has to do it.
            if (hasTarget) return StartLeg(target, Along(here - _origin!.Value));
            return Fail($"stuck at {_covered}/{_distance} tiles: the way {DirName} is open but can't be walked{LastDigStop}");
        }
        if (stop.Kind == DigStopKind.Dark) return HandleDark(ctx, here, stop);
        _lastDigStop = stop.Describe(power);
        if (hasTarget && _stalls < MaxStalls - 1) return StartLeg(target, Along(here - _origin!.Value)); // walking may still help
        return Fail(_lastDigStop);
    }

    private DigPlan Plan(GameCtx ctx, TilePoint here, int power)
    {
        int remaining = Math.Max(1, _distance - _covered);
        if (_dir == ExploreDirection.Down)
        {
            var profile = ProfileBuilder.From(ctx.Player);
            return DigPlanner.ShaftDownNear(ctx.Grid, here, Math.Min(DigPlanner.ShaftChunk, remaining), power,
                profile.SafeFallTiles, profile.NoFallDamage);
        }
        int sign = _dir == ExploreDirection.Left ? -1 : 1;
        return DigPlanner.Tunnel(ctx.Grid, here, sign, DigPlanner.TunnelChunk, Math.Min(TunnelScanMax, remaining), power);
    }

    private GameTaskStatus HandleDark(GameCtx ctx, TilePoint here, DigStop stop)
    {
        if (!_torches) return Fail($"too dark: can't see {stop.At} (torches are off)");
        int slot = TorchSlot(ctx.Player);
        if (slot < 0) return Fail($"too dark: no torches (can't see {stop.At})");
        if (_darkFixAt == here) return Fail($"too dark: can't see {stop.At} even after placing a torch");
        var spot = TorchSpot(ctx, here, slot, stop.At);
        if (spot == null) return Fail($"too dark: no spot to place a torch near {here} (can't see {stop.At})");
        _darkFixAt = here;
        StartTorch(ctx.Player, slot, spot.Value, forDark: true);
        return GameTaskStatus.Running;
    }

    private GameTaskStatus StartLeg(TilePoint target, int along)
    {
        _stallBest = Math.Max(_stallBest, _bestAlong);
        _leg = new MoveToTask(target, allowDig: _allowDig, name: "explore");
        _mode = "walking";
        return GameTaskStatus.Running;
    }

    // ---- sub-tasks ---------------------------------------------------------------------------------------------

    /// <summary>Null: the leg finished and the caller should decide what's next.</summary>
    private GameTaskStatus? TickLeg(GameCtx ctx, TilePoint here)
    {
        // Lighting the way mid-leg (on the ground only): cancel, place, and pick a new leg afterwards.
        if (_torches && ctx.Player.velocity.Y == 0f && --_torchCheckIn <= 0)
        {
            _torchCheckIn = TorchCheckTicks;
            if (WantsAutoTorch(ctx, here, out int slot, out var spot))
            {
                _leg!.OnCancel(ctx);
                _leg = null;
                StartTorch(ctx.Player, slot, spot, forDark: false);
                return GameTaskStatus.Running;
            }
        }
        var st = _leg!.Tick(ctx);
        if (st == GameTaskStatus.Running) return GameTaskStatus.Running;
        string? legReason = _leg.Reason;
        _leg = null;
        bool progressed = _bestAlong > _stallBest;
        if (!progressed) _digNext = true;
        if (RecordStall(progressed) is { } fail) return Fail(fail + (legReason != null ? ": " + legReason : ""));
        return null;
    }

    private GameTaskStatus TickMine(GameCtx ctx, TilePoint here)
    {
        var st = _mine!.Tick(ctx);
        if (st == GameTaskStatus.Running)
        {
            if (_dir == ExploreDirection.Down) KeepCentred(ctx.Player, here);
            return GameTaskStatus.Running;
        }
        string? why = _mine.Reason;
        _dug += _mineTiles!.Count(t => !MineTask.HasMinableTile(t.X, t.Y));
        _mine = null;
        _mineTiles = null;
        ControlState.Pending = default;
        if (st == GameTaskStatus.Failed)
        {
            if (++_digFailures >= MaxDigFailures) return Fail($"digging failed: {why}");
        }
        else _digFailures = 0;
        bool progressed = _bestAlong > _stallBest;
        _stallBest = Math.Max(_stallBest, _bestAlong);
        // A tunnel chunk moves nobody (the next leg walks in), so only a shaft chunk counts toward stalls.
        if (_dir == ExploreDirection.Down && RecordStall(progressed) is { } fail) return Fail(fail + (why != null ? ": " + why : ""));
        return GameTaskStatus.Running;
    }

    /// <summary>Hold left/right until the hitbox lies within the stand's two columns, so digging both drops it.</summary>
    private GameTaskStatus TickAlign(GameCtx ctx, TilePoint here)
    {
        var p = ctx.Player;
        var stand = _alignTo!.Value;
        float left = p.position.X, min = stand.X * 16f + 1f, max = (stand.X + 2) * 16f - p.width - 1f;
        if (left >= min && left <= max && p.velocity.Y == 0f && Math.Abs(p.velocity.X) < 0.5f)
        {
            _alignTo = null;
            ControlState.Pending = default;
            return GameTaskStatus.Running;
        }
        if (++_alignTicks > AlignTimeoutTicks)
        {
            _alignTo = null;
            _mine?.OnCancel(ctx);
            _mine = null;
            _mineTiles = null;
            ControlState.Pending = default;
            if (RecordStall(false) is { } fail) return Fail($"{fail}: couldn't line up over the shaft at {stand}");
            return GameTaskStatus.Running;
        }
        // Steer to the window's middle; release once friction alone would stop the player there.
        float d = (min + max) / 2f - left, v = p.velocity.X;
        float stopDist = v * v / (2f * Math.Max(p.runSlowdown, 0.05f));
        bool release = Math.Abs(d) < 2f || (Math.Sign(v) == Math.Sign(d) && stopDist >= Math.Abs(d));
        ControlState.Pending = default(ControlIntent) with { Left = !release && d < 0f, Right = !release && d > 0f };
        return GameTaskStatus.Running;
    }

    /// <summary>While swinging at a shaft, nudge the hitbox back inside the body columns if it drifted.</summary>
    private static void KeepCentred(Player p, TilePoint here)
    {
        float left = p.position.X, min = here.X * 16f + 1f, max = (here.X + 2) * 16f - p.width - 1f;
        if (p.velocity.Y != 0f || (left >= min && left <= max)) return;
        ControlState.Pending = ControlState.Pending with { Left = left > max, Right = left < min };
    }

    private GameTaskStatus TickTorch(GameCtx ctx)
    {
        var st = _torch!.Tick(ctx);
        if (st == GameTaskStatus.Running)
        {
            _mode = "placing a torch";
            return GameTaskStatus.Running;
        }
        string? why = _torch.Reason;
        _torch = null;
        ControlState.Pending = default;
        _mode = "walking";
        if (st == GameTaskStatus.Succeeded)
        {
            _torchesPlaced++;
            _torchFailures = 0;
            _settle = LightSettleTicks;
            return GameTaskStatus.Running;
        }
        _badTorchSpots.Add(_torchSpot);
        if (_torchForDark)
        {
            _darkFixAt = null; // the torch never went up: allow another spot
            if (++_torchFailures >= MaxTorchFailures) return Fail($"too dark: couldn't place a torch ({why})");
        }
        return GameTaskStatus.Running;
    }

    private void StartTorch(Player p, int slot, TilePoint spot, bool forDark)
    {
        _torch = new PlaceTask(p, slot, spot);
        _torchSpot = spot;
        _torchForDark = forDark;
        _mode = "placing a torch";
    }

    private bool TryAutoTorch(GameCtx ctx, TilePoint here)
    {
        if (!WantsAutoTorch(ctx, here, out int slot, out var spot)) return false;
        StartTorch(ctx.Player, slot, spot, forDark: false);
        return true;
    }

    /// <summary>Dark body underground (or while digging), no torch within the spacing, a torch in the inventory and a
    /// valid spot within reach.</summary>
    private bool WantsAutoTorch(GameCtx ctx, TilePoint here, out int slot, out TilePoint spot)
    {
        slot = -1;
        spot = default;
        bool underground = here.Y > Main.worldSurface;
        if (!underground && _mode != "digging" && _dug == 0) return false;
        if (!TorchPlanner.ShouldPlace(BodyBrightness(here), NearestTorch(here, TorchPlanner.SpacingTiles))) return false;
        slot = TorchSlot(ctx.Player);
        if (slot < 0) return false;
        var s = TorchSpot(ctx, here, slot, focus: null);
        if (s == null) return false;
        spot = s.Value;
        return true;
    }

    private TilePoint? TorchSpot(GameCtx ctx, TilePoint here, int slot, TilePoint? focus)
    {
        var reach = new ReachSpec(Player.tileRangeX, Player.tileRangeY, ctx.Player.inventory[slot].tileBoost);
        HashSet<TilePoint>? keepClear = null;
        if (_dir == ExploreDirection.Down)
        {
            // Torches in the shaft block the platforms move_to builds to climb back out: prefer the shaft walls' alcoves.
            keepClear = new HashSet<TilePoint>();
            for (int y = here.Y - 8; y <= here.Y; y++) { keepClear.Add(new TilePoint(here.X, y)); keepClear.Add(new TilePoint(here.X + 1, y)); }
        }
        // Blocks still to be dug can't anchor a torch (it would drop): this chunk, and the way ahead.
        var pending = _mineTiles?.ToHashSet() ?? new HashSet<TilePoint>();
        if (_allowDig && _dir == ExploreDirection.Down)
            for (int y = here.Y + 1; y <= here.Y + DigPlanner.ShaftChunk * 2; y++) { pending.Add(new TilePoint(here.X, y)); pending.Add(new TilePoint(here.X + 1, y)); }
        else if (_allowDig && _dir is ExploreDirection.Left or ExploreDirection.Right)
            for (int i = 0; i < TunnelScanMax; i++)
                for (int y = here.Y - 2; y <= here.Y; y++)
                    pending.Add(new TilePoint(_dir == ExploreDirection.Right ? here.X + 2 + i : here.X - 1 - i, y));
        return TorchPlanner.BestSpot(ctx.Grid, TorchCellAt, here, reach, focus, pending, keepClear);
    }

    private TorchCell TorchCellAt(TilePoint t)
    {
        if (!WorldGen.InWorld(t.X, t.Y, 2) || _badTorchSpots.Contains(t)) return new TorchCell(false, false);
        var tile = Main.tile[t.X, t.Y];
        bool free = (!tile.HasTile || Main.tileCut[tile.TileType]) && tile.LiquidAmount == 0;
        return new TorchCell(free, tile.WallType != WallID.None);
    }

    /// <summary>Inventory slot of a torch (plain torches first), or -1.</summary>
    private static int TorchSlot(Player p)
    {
        int any = -1;
        for (int i = 0; i < 58; i++)
        {
            var it = p.inventory[i];
            if (it.IsAir || it.createTile != TileID.Torches) continue;
            if (it.type == ItemID.Torch) return i;
            if (any < 0) any = i;
        }
        return any;
    }

    private static float BodyBrightness(TilePoint here)
    {
        float sum = 0f;
        for (int y = here.Y - 2; y <= here.Y; y++)
            for (int x = here.X; x <= here.X + 1; x++)
                sum += WorldGen.InWorld(x, y) ? Lighting.Brightness(x, y) : 0f;
        return sum / 6f;
    }

    /// <summary>Chebyshev distance from the body to the nearest torch tile within <paramref name="radius"/>, or null.</summary>
    private static int? NearestTorch(TilePoint here, int radius)
    {
        int? best = null;
        for (int y = here.Y - 1 - radius; y <= here.Y - 1 + radius; y++)
            for (int x = here.X - radius; x <= here.X + 1 + radius; x++)
            {
                if (!WorldGen.InWorld(x, y)) continue;
                var t = Main.tile[x, y];
                if (!t.HasTile || t.TileType != TileID.Torches) continue;
                int d = Math.Max(Math.Abs(x - here.X), Math.Abs(y - (here.Y - 1)));
                if (best == null || d < best) best = d;
            }
        return best;
    }

    // ---- bookkeeping -------------------------------------------------------------------------------------------

    /// <summary>Counts sub-tasks in a row that ended without new progress; returns a failure message at the limit.</summary>
    private string? RecordStall(bool progressed)
    {
        if (progressed)
        {
            _stalls = 0;
            return null;
        }
        int limit = _allowDig ? MaxStalls : MaxWalkStalls;
        return ++_stalls >= limit ? $"no progress after {limit} attempts at {_covered}/{_distance} tiles{LastDigStop}" : null;
    }

    private void UpdateProgress()
    {
        var s = $"{_mode}: {_covered}/{_distance} tiles";
        if (_dug > 0) s += $", dug {_dug}";
        if (_torchesPlaced > 0) s += $", torches {_torchesPlaced}";
        Progress = s;
    }

    private void CancelSubTasks(GameCtx ctx)
    {
        _leg?.OnCancel(ctx);
        _mine?.OnCancel(ctx);
        _torch?.OnCancel(ctx);
        if (_mineTiles != null) _dug += _mineTiles.Count(t => !MineTask.HasMinableTile(t.X, t.Y));
        _leg = null;
        _mine = null;
        _mineTiles = null;
        _torch = null;
        _alignTo = null;
        ControlState.Pending = default;
    }

    private GameTaskStatus Fail(string reason)
    {
        Reason = reason;
        _stopped = reason;
        return GameTaskStatus.Failed;
    }

    private string DirName => _dir.ToString().ToLowerInvariant();

    private static TilePoint Here(GameCtx ctx)
    {
        var p = ctx.Player;
        return NavMath.StandingTile(NavMath.FromPixels(p.position.X, p.position.Y, p.velocity.X, p.velocity.Y, p.height));
    }

    private int Along(TilePoint d) => _dir switch
    {
        ExploreDirection.Left => -d.X,
        ExploreDirection.Right => d.X,
        ExploreDirection.Down => d.Y,
        _ => -d.Y,
    };

    /// <summary>The visible standable cell ahead with the most progress (ties: closest to the line of travel),
    /// not beyond the remaining distance.</summary>
    private bool PickTarget(GameCtx ctx, TilePoint here, out TilePoint best)
    {
        best = default;
        int remaining = _distance - Along(here - _origin!.Value);
        int reach = Math.Min(LegReach, Math.Max(1, remaining));
        bool horizontal = _dir is ExploreDirection.Left or ExploreDirection.Right;
        int sign = _dir is ExploreDirection.Left or ExploreDirection.Up ? -1 : 1;
        int bestScore = int.MinValue;
        for (int a = reach; a >= 2; a--)
            for (int l = -LateralReach; l <= LateralReach; l++)
            {
                var p = horizontal ? new TilePoint(here.X + sign * a, here.Y + l) : new TilePoint(here.X + l, here.Y + sign * a);
                int score = a * 100 - Math.Abs(l);
                if (score <= bestScore || !Pathfinder.CanStand(ctx.Grid, p) || HasUnknown(ctx.Grid, p)) continue;
                bestScore = score;
                best = p;
            }
        return bestScore != int.MinValue;
    }

    private static bool HasUnknown(ITileGrid g, TilePoint p)
    {
        for (int y = p.Y - 2; y <= p.Y + 1; y++)
            for (int x = p.X; x <= p.X + 1; x++)
                if (g.Get(x, y).Kind == TileKind.Unknown) return true;
        return false;
    }
}
