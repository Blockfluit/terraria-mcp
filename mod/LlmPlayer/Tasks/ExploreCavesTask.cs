using System.Text.Json.Serialization;
using LlmPlayer.Bridge;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Control;
using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using LlmPlayer.Core.Observe;
using LlmPlayer.Core.Tasks;
using LlmPlayer.Observe;
using Terraria;
using Terraria.ID;

namespace LlmPlayer.Tasks;

/// <summary>Something explore_caves saw that wasn't visible when it started. Kind: a <see cref="Valuables.Key"/>.</summary>
public sealed record FoundThing(string Kind, string Name, int X, int Y);

/// <summary>explore_caves' result. RevealedTiles: tiles that became visible around the player during the task
/// (approximate: counted in a window around the player).</summary>
public sealed record ExploreCavesResult(int RevealedTiles, List<FoundThing> Found, int TorchesPlaced, int PotsBroken,
    int Legs, string StoppedBecause,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FoundThing? StoppedAt = null);

/// <summary>
/// Explore caves through their open space, frontier by frontier (<see cref="CaveFrontier"/>): walk (a
/// <see cref="MoveToTask"/>, which may dig a tile or two where that's shorter) to the nearest spot under known open
/// space that touches unknown tiles, light it, look, repeat. Fair vision throughout: only what the player could see.
/// <list type="bullet">
/// <item>Torches (<c>torches</c>, default on): underground, whenever the body is dark and no torch is within
/// <see cref="TorchPlanner.SpacingTiles"/> (also mid-walk), like explore; and once more where no frontier is left,
/// to reveal the dark around the player before giving up.</item>
/// <item>Safety: the frontier search never enters lava, spikes or honey and skips frontiers within 3 tiles of lava;
/// stands must be on a known floor away from lava; the pathfinder's own fall/lava rules apply to every walk.</item>
/// <item>Every <see cref="ScanTicks"/> ticks it scans for valuables (<see cref="ValuableScan"/>, radius
/// <see cref="ScanRadius"/>). Anything not visible at the start goes into <c>found</c>; a kind in <c>stop_on</c> stops
/// the task (succeeded) with what and where. With <c>collect_pots</c>, visible pots within
/// <see cref="PotReach"/> tiles are broken on the way (<see cref="CollectTask"/>).</item>
/// <item>Ends (succeeded) on the time limit or when no reachable frontier is left within <c>radius</c> of the start;
/// fails after <see cref="MaxStalls"/> walks in a row that failed or revealed nothing.</item>
/// </list>
/// </summary>
public sealed class ExploreCavesTask : IGameTask<GameCtx>
{
    public const int ScanTicks = 30, ScanRadius = 35, RevealRadius = 30, SeedRadius = 50, PotReach = 12;
    public const int MaxStalls = 6, LegTimeoutTicks = 30 * 60, MaxFound = 30;
    private const int TorchCheckTicks = 60, LightSettleTicks = 20, MaxTorchFailures = 4, NearRejected = 2;

    private readonly int _radius, _maxTicks;
    private readonly HashSet<ValuableKind> _stopOn;
    private readonly bool _collectPots, _torches;
    private readonly bool _wantOre;

    private TilePoint? _origin;
    private int _elapsed;
    private bool _seeded;

    private MoveToTask? _leg;
    private TilePoint _legStand;
    private int _legTicks, _revealedAtLegStart, _legs;
    private PlaceTask? _torch;
    private TilePoint _torchSpot;
    private CollectTask? _collect;

    private readonly List<TilePoint> _rejected = new(), _visited = new();
    private readonly HashSet<TilePoint> _badTorchSpots = new(), _potsTried = new();
    private readonly HashSet<long> _known = new();
    private readonly HashSet<(int, int)> _initial = new();
    private readonly List<FoundThing> _found = new();
    private List<ValuableHit> _lastHits = new();
    private int _revealed, _torchesPlaced, _potsBroken, _stalls, _scanIn, _torchCheckIn, _settle, _torchFailures;
    private TilePoint? _darkFixAt;
    private FoundThing? _stopHit;
    private string _mode = "starting";
    private string? _stopped;

    public string Name => "explore_caves";
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }
    public object? Result => new ExploreCavesResult(_revealed, _found, _torchesPlaced, _potsBroken, _legs,
        _stopped ?? "running", _stopHit);

    public ExploreCavesTask(int radius, int maxSeconds, IEnumerable<ValuableKind> stopOn, bool collectPots, bool torches)
    {
        _radius = radius;
        _maxTicks = maxSeconds * 60;
        _stopOn = stopOn.ToHashSet();
        _collectPots = collectPots;
        _torches = torches;
        _wantOre = _stopOn.Contains(ValuableKind.Ore);
    }

    public GameTaskStatus Tick(GameCtx ctx)
    {
        var p = ctx.Player;
        var here = Here(p);
        _origin ??= here;
        if (!_seeded)
        {
            _seeded = true;
            Seed(p, here);
        }
        _elapsed++;
        if (--_scanIn <= 0)
        {
            _scanIn = ScanTicks;
            Scan(p, here);
            if (_stopHit is { } hit)
                return Finish(ctx, GameTaskStatus.Succeeded, $"found {hit.Name} at ({hit.X},{hit.Y})");
        }
        UpdateProgress();
        if (_elapsed > _maxTicks)
            return Finish(ctx, GameTaskStatus.Succeeded, $"time limit ({_maxTicks / 60} s) reached");

        if (_torch != null) return TickTorch(ctx);
        if (_collect != null) return TickCollect(ctx);
        if (_leg != null && TickLeg(ctx, here) is { } st) return st;
        if (_leg != null) return GameTaskStatus.Running;
        if (_settle > 0)
        {
            _settle--; // let a new torch's light spread before looking for frontiers
            ControlState.Pending = default;
            return GameTaskStatus.Running;
        }
        if (_torches && --_torchCheckIn <= 0)
        {
            _torchCheckIn = TorchCheckTicks;
            if (WantsAutoTorch(ctx, here, out int slot, out var spot))
            {
                StartTorch(ctx.Player, slot, spot);
                return GameTaskStatus.Running;
            }
        }
        if (_collectPots && TryStartPot(ctx, here)) return GameTaskStatus.Running;
        return Decide(ctx, here);
    }

    public void OnCancel(GameCtx ctx) => CancelSubTasks(ctx);

    // ---- decisions ---------------------------------------------------------------------------------------------

    private GameTaskStatus Decide(GameCtx ctx, TilePoint here)
    {
        var pick = CaveFrontier.Pick(ctx.Grid, here, _origin!.Value, _radius, IsRejected);
        if (pick == null)
        {
            // Nothing known to walk to: light up where we stand once, then look again.
            if (_torches && _darkFixAt != here && ExploreTask.TorchSlot(ctx.Player) is var slot and >= 0
                && ExploreTask.NearestTorch(here, 3) == null && TorchSpot(ctx, here, slot) is { } spot)
            {
                _darkFixAt = here;
                StartTorch(ctx.Player, slot, spot);
                return GameTaskStatus.Running;
            }
            return Finish(ctx, GameTaskStatus.Succeeded,
                $"nothing left to explore: no reachable unexplored opening within {_radius} tiles of the start");
        }
        _legStand = pick.Stand;
        _legTicks = 0;
        _revealedAtLegStart = _revealed;
        _legs++;
        _leg = new MoveToTask(pick.Stand, allowDig: true, allowBuild: true, tolerance: 1, name: Name);
        _mode = $"walking to {pick.Stand}";
        return GameTaskStatus.Running;
    }

    private bool IsRejected(TilePoint s) =>
        _rejected.Any(r => CaveFrontier.Chebyshev(r, s) <= NearRejected) || _visited.Any(v => CaveFrontier.Chebyshev(v, s) <= NearRejected);

    private bool TryStartPot(GameCtx ctx, TilePoint here)
    {
        var pot = _lastHits
            .Where(h => h.Kind == ValuableKind.Pot && !_potsTried.Contains(new TilePoint(h.X, h.Y))
                        && Math.Abs(h.X - here.X) <= PotReach && Math.Abs(h.Y - here.Y) <= PotReach)
            .OrderBy(h => Math.Abs(h.X - here.X) + Math.Abs(h.Y - here.Y))
            .FirstOrDefault();
        if (pot.Name == null) return false;
        var at = new TilePoint(pot.X, pot.Y);
        _potsTried.Add(at);
        try
        {
            var target = CollectTask.Resolve(ctx.Player, pot.X, pot.Y);
            _collect = new CollectTask(target, Name);
            _mode = $"breaking a pot at {at}";
            return true;
        }
        catch (CommandError)
        {
            return false; // protected, no pickaxe, gone: walk on
        }
    }

    // ---- sub-tasks ---------------------------------------------------------------------------------------------

    /// <summary>Null: the leg finished; decide next tick.</summary>
    private GameTaskStatus? TickLeg(GameCtx ctx, TilePoint here)
    {
        if (_torches && ctx.Player.velocity.Y == 0f && --_torchCheckIn <= 0)
        {
            _torchCheckIn = TorchCheckTicks;
            if (WantsAutoTorch(ctx, here, out int slot, out var spot))
            {
                // Light the way: drop this leg (the frontier is picked again afterwards).
                _leg!.OnCancel(ctx);
                _leg = null;
                StartTorch(ctx.Player, slot, spot);
                return GameTaskStatus.Running;
            }
        }
        var st = ++_legTicks > LegTimeoutTicks ? GameTaskStatus.Failed : _leg!.Tick(ctx);
        if (st == GameTaskStatus.Running) return GameTaskStatus.Running;
        if (_legTicks > LegTimeoutTicks) _leg!.OnCancel(ctx);
        _leg = null;
        ControlState.Pending = default;
        bool revealedSomething = _revealed > _revealedAtLegStart;
        if (st == GameTaskStatus.Succeeded) _visited.Add(_legStand);
        else _rejected.Add(_legStand);
        if (st == GameTaskStatus.Succeeded || revealedSomething) _mode = "looking around";
        _stalls = revealedSomething ? 0 : _stalls + 1;
        if (_stalls >= MaxStalls)
            return Finish(ctx, GameTaskStatus.Failed, $"no progress after {MaxStalls} walks in a row (last: {(st == GameTaskStatus.Succeeded ? "revealed nothing new" : "couldn't reach " + _legStand)})");
        return null;
    }

    private GameTaskStatus TickTorch(GameCtx ctx)
    {
        var st = _torch!.Tick(ctx);
        if (st == GameTaskStatus.Running) return GameTaskStatus.Running;
        _torch = null;
        ControlState.Pending = default;
        if (st == GameTaskStatus.Succeeded)
        {
            _torchesPlaced++;
            _torchFailures = 0;
            _settle = LightSettleTicks;
        }
        else
        {
            _badTorchSpots.Add(_torchSpot);
            if (++_torchFailures >= MaxTorchFailures) _torchCheckIn = TorchCheckTicks * 10; // stop trying for a while
        }
        _mode = "looking around";
        return GameTaskStatus.Running;
    }

    private GameTaskStatus TickCollect(GameCtx ctx)
    {
        var st = _collect!.Tick(ctx);
        if (st == GameTaskStatus.Running) return GameTaskStatus.Running;
        if (st == GameTaskStatus.Succeeded) _potsBroken++;
        _collect = null;
        ControlState.Pending = default;
        _mode = "looking around";
        return GameTaskStatus.Running;
    }

    private void StartTorch(Player p, int slot, TilePoint spot)
    {
        _torch = new PlaceTask(p, slot, spot);
        _torchSpot = spot;
        _mode = "placing a torch";
    }

    private bool WantsAutoTorch(GameCtx ctx, TilePoint here, out int slot, out TilePoint spot)
    {
        slot = -1;
        spot = default;
        if (here.Y <= Main.worldSurface) return false;
        if (!TorchPlanner.ShouldPlace(ExploreTask.BodyBrightness(here), ExploreTask.NearestTorch(here, TorchPlanner.SpacingTiles))) return false;
        slot = ExploreTask.TorchSlot(ctx.Player);
        if (slot < 0) return false;
        if (TorchSpot(ctx, here, slot) is not { } s) return false;
        spot = s;
        return true;
    }

    private TilePoint? TorchSpot(GameCtx ctx, TilePoint here, int slot)
    {
        var reach = new ReachSpec(Player.tileRangeX, Player.tileRangeY, ctx.Player.inventory[slot].tileBoost);
        return TorchPlanner.BestSpot(ctx.Grid, TorchCellAt, here, reach);
    }

    private TorchCell TorchCellAt(TilePoint t)
    {
        if (!WorldGen.InWorld(t.X, t.Y, 2) || _badTorchSpots.Contains(t)) return new TorchCell(false, false);
        var tile = Main.tile[t.X, t.Y];
        bool free = (!tile.HasTile || Main.tileCut[tile.TileType]) && tile.LiquidAmount == 0;
        return new TorchCell(free, tile.WallType != WallID.None);
    }

    // ---- looking -----------------------------------------------------------------------------------------------

    /// <summary>What's visible at the start: not reported as found, not counted as revealed.</summary>
    private void Seed(Player p, TilePoint here)
    {
        foreach (var h in ValuableScan.Scan(p, here.X, here.Y - 1, ScanRadius, _wantOre)) _initial.Add((h.X, h.Y));
        var screen = Visibility.Screen;
        for (int y = here.Y - SeedRadius; y <= here.Y + SeedRadius; y++)
            for (int x = here.X - SeedRadius; x <= here.X + SeedRadius; x++)
                if (Visibility.IsTileVisible(x, y, screen, p)) _known.Add(Key(x, y));
    }

    private void Scan(Player p, TilePoint here)
    {
        var screen = Visibility.Screen;
        for (int y = here.Y - RevealRadius; y <= here.Y + RevealRadius; y++)
            for (int x = here.X - RevealRadius; x <= here.X + RevealRadius; x++)
                if (CaveFrontier.Chebyshev(new TilePoint(x, y), _origin!.Value) <= _radius + RevealRadius
                    && Visibility.IsTileVisible(x, y, screen, p) && _known.Add(Key(x, y)))
                    _revealed++;

        _lastHits = ValuableScan.Scan(p, here.X, here.Y - 1, ScanRadius, _wantOre);
        foreach (var h in _lastHits)
        {
            if (_initial.Contains((h.X, h.Y))) continue;
            // One entry per object; one per vein for ores (same name within 3 tiles); pots only once each.
            if (_found.Any(f => f.Name == h.Name && Math.Abs(f.X - h.X) <= (h.Kind == ValuableKind.Ore ? 3 : 0)
                                                 && Math.Abs(f.Y - h.Y) <= (h.Kind == ValuableKind.Ore ? 3 : 0)))
                continue;
            if (h.Kind == ValuableKind.Pot && _potsTried.Contains(new TilePoint(h.X, h.Y))) continue;
            var thing = new FoundThing(Valuables.Key(h.Kind), h.Name, h.X, h.Y);
            if (_found.Count < MaxFound) _found.Add(thing);
            // A pot we're about to break on the way isn't a reason to stop.
            if (_stopOn.Contains(h.Kind) && !(h.Kind == ValuableKind.Pot && _collectPots) && _stopHit == null)
                _stopHit = thing;
        }
    }

    private static long Key(int x, int y) => ((long)x << 32) | (uint)y;

    // ---- bookkeeping -------------------------------------------------------------------------------------------

    private void UpdateProgress()
    {
        Progress = $"{_mode}; {_elapsed / 60}/{_maxTicks / 60} s, revealed {_revealed}, found {_found.Count}"
                   + (_torchesPlaced > 0 ? $", torches {_torchesPlaced}" : "")
                   + (_potsBroken > 0 ? $", pots {_potsBroken}" : "");
    }

    private GameTaskStatus Finish(GameCtx ctx, GameTaskStatus status, string reason)
    {
        CancelSubTasks(ctx);
        Reason = reason;
        _stopped = reason;
        UpdateProgress();
        return status;
    }

    private void CancelSubTasks(GameCtx ctx)
    {
        _leg?.OnCancel(ctx);
        _torch?.OnCancel(ctx);
        _collect?.OnCancel(ctx);
        _leg = null;
        _torch = null;
        _collect = null;
        ControlState.Pending = default;
    }

    private static TilePoint Here(Player p) =>
        NavMath.StandingTile(NavMath.FromPixels(p.position.X, p.position.Y, p.velocity.X, p.velocity.Y, p.height));
}
