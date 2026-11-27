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
/// (approximate: counted in a window around the player). Descended: tiles dug getting below the surface layer;
/// Tunnels/Tunnelled: tunnels started and tiles dug in them once a pocket was exhausted. LifeCrystals: every Life
/// Crystal seen (also ones already in view at the start). Tunnels only counts tunnels that dug or moved; FailedTunnels:
/// attempts that couldn't start (start unreachable, first dig refused). Relocations: trips to a far unexplored region
/// once the neighbourhood was exhausted; Relocated: tiles travelled on them (their digging is in Tunnelled).</summary>
public sealed record ExploreCavesResult(int RevealedTiles, List<FoundThing> Found, int TorchesPlaced, int PotsBroken,
    int Legs, int Descended, int Tunnels, int Tunnelled, List<FoundThing> LifeCrystals, string StoppedBecause,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FoundThing? StoppedAt = null,
    int FailedTunnels = 0, int Relocations = 0, int Relocated = 0);

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
/// the task (succeeded) with what and where — once: a thing that already stopped a call (remembered across calls in
/// <see cref="Memory"/>), a rigged Dead Man's Chest, or a chest the player opened before and that is empty now is only
/// listed. With <c>collect_pots</c>, visible pots within
/// <see cref="PotReach"/> tiles are broken on the way (<see cref="CollectTask"/>).</item>
/// <item>Underground first: started in the surface layer (above <c>Main.worldSurface</c>) it first goes down
/// (<see cref="ExploreTask"/> down: walking, or a 2-wide shaft) to <see cref="CaveTunnel.DescentDistance"/> below it.
/// Frontiers in the surface layer are never picked.</item>
/// <item>Tunnelling: when no reachable frontier is left (the pocket is exhausted), it digs toward the nearest large
/// unrevealed region (<see cref="CaveTunnel.Choose"/>: down preferred, else sideways; never toward known lava, through
/// liquid, protected/too-hard rock): walk to the start, then an <see cref="ExploreTask"/> of at most
/// <see cref="CaveTunnel.MaxTiles"/> tiles (DigPlanner's safety rules, torches in the dark). It stops as soon as a
/// frontier outside its own corridor appears (it broke into a cave) and exploring resumes, <c>radius</c> now counted
/// from there. At most <see cref="MaxTunnels"/> tunnels per area; a tunnel that dug nothing and moved nobody doesn't
/// count (<see cref="TunnelTally"/>): it is remembered and another direction/stand is tried.</item>
/// <item>Memory across calls (<see cref="Memory"/>, per world, expiring): failed walks (stand + frontier) and failed
/// tunnel/relocation starts aren't retried by the next call.</item>
/// <item>Relocation: when no frontier and no tunnel is left (or the tunnel limit is reached, or
/// <see cref="MaxStalls"/> walks in a row failed or revealed nothing), it travels to the nearest large unexplored
/// region (<see cref="CaveRelocate"/>: a long tunnel left/right at the current depth or a shaft down, through known
/// caves where they lie on the way, torches in the dark, DigPlanner's safety rules) and explores again there; it
/// stops early when a frontier well along the way appears. At most <see cref="MaxRelocations"/> per task.</item>
/// <item>Ends (succeeded) on the time limit or when nothing of the above is left; fails when walks keep failing and
/// no tunnel or relocation is possible either.</item>
/// </list>
/// </summary>
public sealed class ExploreCavesTask : IGameTask<GameCtx>
{
    public const int ScanTicks = 30, ScanRadius = 35, RevealRadius = 30, SeedRadius = 50, PotReach = 12;
    public const int MaxStalls = 6, LegTimeoutTicks = 30 * 60, LegStallTicks = 8 * 60, MaxFound = 30;
    private int _legBest = int.MaxValue, _legStallTicks;
    private const int TorchCheckTicks = 60, LightSettleTicks = 20, MaxTorchFailures = 4, NearRejected = 2;
    public const int MaxTunnels = 10, BreakCheckTicks = 45, MaxRelocations = 4;

    /// <summary>Failed walks/tunnels, kept across calls (per world, expiring).</summary>
    public static readonly ExploreMemory Memory = new();
    private static long Now => Main.GameUpdateCount;

    private readonly int _radius, _maxTicks;
    private readonly HashSet<ValuableKind> _stopOn;
    private readonly bool _collectPots, _torches;
    private readonly bool _wantOre;

    private TilePoint? _origin;
    private int _elapsed;
    private bool _seeded;

    private MoveToTask? _leg;
    private TilePoint _legStand, _legFrontier;
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

    // Getting underground and tunnelling out of exhausted pockets.
    private ExploreTask? _descend, _tunnel, _relocTask;
    private MoveToTask? _tunnelWalk;
    private TunnelChoice? _tunnelChoice;
    private RelocateChoice? _reloc;
    private bool _descendTried, _pocketExhausted;
    private int _descended, _tunnelTicks, _breakCheckIn, _relocations, _relocated;
    private readonly TunnelTally _tally = new();
    private readonly List<(TilePoint Start, TunnelDir Dir, int Length)> _corridors = new();
    private readonly List<(TilePoint Start, TunnelDir Dir)> _triedTunnels = new(), _triedRelocs = new();
    private string? _stallNote;
    private readonly List<FoundThing> _lifeCrystals = new();
    private string? _lastTunnelStop;
    private FoundThing? _stopHit;
    private string _mode = "starting";
    private string? _stopped;

    public string Name => "explore_caves";
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }
    public object? Result => new ExploreCavesResult(_revealed, _found, _torchesPlaced, _potsBroken, _legs,
        _descended, _tally.Counted, _tally.Tiles, _lifeCrystals, _stopped ?? "running", _stopHit,
        _tally.Wasted, _relocations, _relocated);

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
            Memory.UseWorld(WorldKey);
            Seed(p, here);
        }
        _elapsed++;
        if (--_scanIn <= 0)
        {
            _scanIn = ScanTicks;
            Scan(p, here);
            if (_stopHit is { } hit)
                return Finish(ctx, GameTaskStatus.Succeeded, hit.Kind == Valuables.Key(ValuableKind.LifeCrystal)
                    ? $"found a Life Crystal at ({hit.X},{hit.Y}): collect it for +20 max life"
                    : $"found {hit.Name} at ({hit.X},{hit.Y})");
        }
        UpdateProgress();
        if (_elapsed > _maxTicks)
            return Finish(ctx, GameTaskStatus.Succeeded, $"time limit ({_maxTicks / 60} s) reached");

        if (_descend != null) return TickDescend(ctx, here);
        if (_tunnelWalk != null || _tunnel != null) return TickTunnel(ctx, here);
        if (_relocTask != null) return TickRelocate(ctx, here);
        if (!_descendTried)
        {
            _descendTried = true;
            int down = CaveTunnel.DescentDistance(here.Y, Main.worldSurface);
            if (down > 0)
            {
                _descend = new ExploreTask(ExploreDirection.Down, down, allowDig: true, torches: _torches);
                _corridors.Add((here, TunnelDir.Down, down));
                _mode = $"going underground ({down} tiles down)";
                return GameTaskStatus.Running;
            }
        }

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
        int power = HeldItemLease.PickPower(ctx.Player);
        var pick = _pocketExhausted ? null
            : CaveFrontier.Pick(ctx.Grid, here, _origin!.Value, _radius, IsRejected, power, ignoreFrontier: IgnoreFrontier);
        if (pick == null)
        {
            // Nothing known to walk to: light up where we stand once (toward the nearest dark opening, if any), then
            // look again.
            var focus = _torches && !_pocketExhausted && _darkFixAt != here
                ? CaveFrontier.NearestFrontier(ctx.Grid, here, _origin!.Value, _radius, power, ignoreFrontier: IgnoreFrontier) : null;
            if (_torches && !_pocketExhausted && _darkFixAt != here && ExploreTask.TorchSlot(ctx.Player) is var slot and >= 0
                && ExploreTask.NearestTorch(here, 3) == null && TorchSpot(ctx, here, slot, focus) is { } spot)
            {
                _darkFixAt = here;
                StartTorch(ctx.Player, slot, spot);
                return GameTaskStatus.Running;
            }
            // The pocket is exhausted: tunnel toward unexplored space, else relocate to a far unexplored region.
            string where = _tally.Counted > 0 || _relocations > 0 ? "of the last tunnel's end" : "of the start";
            string nothing = _stallNote != null
                ? $"no progress after {MaxStalls} walks in a row ({_stallNote})"
                : $"nothing left to explore: no reachable unexplored opening within {_radius} tiles {where}";
            var status = _stallNote != null ? GameTaskStatus.Failed : GameTaskStatus.Succeeded;
            if (power <= 0)
                return Finish(ctx, status, $"{nothing}, and no pickaxe to tunnel with");
            string tunnelWhy;
            if (_tally.LimitReached(MaxTunnels))
                tunnelWhy = $"tunnel limit reached ({_tally.CountedHere} tunnels, {_tally.WastedHere} failed starts here)";
            else
            {
                var decision = CaveTunnel.Choose(ctx.Grid, here, _origin!.Value, _radius, power, TunnelTried);
                if (decision.Choice is { } choice)
                {
                    StartTunnel(choice, here);
                    return GameTaskStatus.Running;
                }
                tunnelWhy = $"no safe tunnel ({decision.Summary})";
            }
            string relocWhy;
            if (_relocations >= MaxRelocations) relocWhy = $"relocation limit ({MaxRelocations}) reached";
            else
            {
                var reloc = CaveRelocate.Choose(ctx.Grid, here, power, RelocTried);
                if (reloc.Choice is { } r)
                {
                    StartRelocate(r);
                    return GameTaskStatus.Running;
                }
                relocWhy = $"no way to a large unexplored region ({reloc.Summary})";
            }
            return Finish(ctx, status, $"{nothing}; {tunnelWhy}; {relocWhy}"
                + (_lastTunnelStop != null ? $"; last tunnel: {_lastTunnelStop}" : ""));
        }
        _legStand = pick.Stand;
        _legFrontier = pick.Frontier;
        _legTicks = 0;
        _revealedAtLegStart = _revealed;
        _legs++;
        _leg = new MoveToTask(pick.Stand, allowDig: true, allowBuild: true, tolerance: 1, name: Name);
        _legBest = int.MaxValue;
        _legStallTicks = 0;
        _mode = $"walking to {pick.Stand}";
        return GameTaskStatus.Running;
    }

    /// <summary>Frontier cells not worth walking to: the surface layer, and the faces of our own shafts/tunnels.</summary>
    private bool IgnoreFrontier(TilePoint c) =>
        c.Y <= Main.worldSurface || _corridors.Any(k => CaveTunnel.InCorridor(k.Start, k.Dir, k.Length, c))
        || Memory.FrontierBlocked(c, Now);

    private bool TunnelTried(TunnelDir dir, TilePoint s) =>
        _triedTunnels.Any(t => t.Dir == dir && CaveFrontier.Chebyshev(t.Start, s) <= NearRejected) || Memory.TunnelBlocked(dir, s, Now);

    private bool RelocTried(TunnelDir dir, TilePoint s) =>
        _triedRelocs.Any(t => t.Dir == dir && CaveFrontier.Chebyshev(t.Start, s) <= NearRejected) || Memory.TunnelBlocked(dir, s, Now);

    private void StartRelocate(RelocateChoice r)
    {
        _reloc = r;
        _tunnelTicks = 0;
        _breakCheckIn = BreakCheckTicks;
        _triedRelocs.Add((r.Start, r.Dir));
        _corridors.Add((r.Start, r.Dir, r.Distance));
        _relocTask = new ExploreTask(ToExplore(r.Dir), r.Distance, allowDig: true, torches: _torches);
        _mode = $"relocating {DirName(r.Dir)} {r.Distance} tiles from {r.Start} (unexplored region {r.RegionAt} tiles away, {r.UnknownShare}% unknown)";
    }

    private GameTaskStatus TickRelocate(GameCtx ctx, TilePoint here)
    {
        var r = _reloc!;
        string? stop = null;
        if (BrokeThrough(ctx, here, c => CaveRelocate.Beyond(r, c))) stop = "reached unexplored caves";
        else
        {
            var st = _relocTask!.Tick(ctx);
            if (st == GameTaskStatus.Running) return GameTaskStatus.Running;
            stop = st == GameTaskStatus.Succeeded ? "arrived" : _relocTask.Reason ?? "stopped";
        }
        int dug = Dug(_relocTask!), covered = Covered(_relocTask!);
        _relocTask!.OnCancel(ctx);
        _relocTask = null;
        _reloc = null;
        ControlState.Pending = default;
        bool counted = _tally.Record(dug, covered);
        if (counted)
        {
            _relocations++;
            _relocated += covered;
            _tally.NewArea();
            _lastTunnelStop = $"relocation {DirName(r.Dir)} from {r.Start}: {stop} after {covered} tiles ({dug} dug)";
            return Resume(here, "exploring a new area");
        }
        Memory.TunnelFailed(r.Dir, r.Start, Now);
        _lastTunnelStop = $"relocation {DirName(r.Dir)} from {r.Start} went nowhere: {stop}";
        _mode = "looking around";
        return GameTaskStatus.Running;
    }

    private void StartTunnel(TunnelChoice choice, TilePoint here)
    {
        _tunnelChoice = choice;
        _tunnelTicks = 0;
        _breakCheckIn = BreakCheckTicks;
        _triedTunnels.Add((choice.Start, choice.Dir));
        _corridors.Add((choice.Start, choice.Dir, CaveTunnel.MaxTiles));
        if (choice.Start != here)
        {
            _tunnelWalk = new MoveToTask(choice.Start, allowDig: true, allowBuild: true, tolerance: 0, name: Name);
            _mode = $"walking to {choice.Start} to tunnel {DirName(choice.Dir)}";
        }
        else BeginDigging(choice);
    }

    private void BeginDigging(TunnelChoice choice)
    {
        _tunnel = new ExploreTask(ToExplore(choice.Dir), CaveTunnel.MaxTiles, allowDig: true, torches: _torches);
        _mode = $"tunnelling {DirName(choice.Dir)} from {choice.Start} (unexplored space ~{choice.ToUnknown + 1} tiles away)";
    }

    private static ExploreDirection ToExplore(TunnelDir d) => d switch
    {
        TunnelDir.Down => ExploreDirection.Down,
        TunnelDir.Left => ExploreDirection.Left,
        _ => ExploreDirection.Right,
    };

    private static string DirName(TunnelDir d) => d.ToString().ToLowerInvariant();

    /// <summary>A frontier outside our own shafts/tunnels within reach (and passing <paramref name="accept"/>): we
    /// broke into a cave.</summary>
    private bool BrokeThrough(GameCtx ctx, TilePoint here, Func<TilePoint, bool>? accept = null)
    {
        if (here.Y <= Main.worldSurface || --_breakCheckIn > 0) return false;
        _breakCheckIn = BreakCheckTicks;
        return CaveFrontier.Pick(ctx.Grid, here, here, _radius, IsRejected, HeldItemLease.PickPower(ctx.Player),
            ignoreFrontier: c => IgnoreFrontier(c) || (accept != null && !accept(c))) != null;
    }

    private GameTaskStatus TickDescend(GameCtx ctx, TilePoint here)
    {
        if (BrokeThrough(ctx, here))
        {
            _descended += Dug(_descend!);
            _descend!.OnCancel(ctx);
            _descend = null;
            return Resume(here, "broke into a cave");
        }
        var st = _descend!.Tick(ctx);
        if (st == GameTaskStatus.Running) return GameTaskStatus.Running;
        _descended += Dug(_descend);
        string? why = _descend.Reason;
        _descend = null;
        ControlState.Pending = default;
        if (st != GameTaskStatus.Succeeded && here.Y <= Main.worldSurface)
            return Finish(ctx, GameTaskStatus.Failed, $"couldn't get underground: {why ?? "explore down failed"}");
        return Resume(here, "looking around");
    }

    private GameTaskStatus TickTunnel(GameCtx ctx, TilePoint here)
    {
        var choice = _tunnelChoice!;
        if (_tunnelWalk != null)
        {
            var ws = ++_tunnelTicks > LegTimeoutTicks ? GameTaskStatus.Failed : _tunnelWalk.Tick(ctx);
            if (ws == GameTaskStatus.Running) return GameTaskStatus.Running;
            if (_tunnelTicks > LegTimeoutTicks) _tunnelWalk.OnCancel(ctx);
            string? why = _tunnelWalk.Reason;
            _tunnelWalk = null;
            ControlState.Pending = default;
            if (ws != GameTaskStatus.Succeeded)
            {
                // Nothing dug, nobody moved toward it: doesn't count; remembered so the next pick is another start.
                _tally.Record(0, 0);
                Memory.TunnelFailed(choice.Dir, choice.Start, Now);
                _lastTunnelStop = $"couldn't reach {choice.Start} to tunnel {DirName(choice.Dir)}" + (why != null ? $": {why}" : "");
                _tunnelChoice = null;
                _mode = "looking around";
                return GameTaskStatus.Running;
            }
            BeginDigging(choice);
            return GameTaskStatus.Running;
        }
        if (BrokeThrough(ctx, here))
        {
            _tally.Record(Dug(_tunnel!), Covered(_tunnel!));
            _tunnel!.OnCancel(ctx);
            _tunnel = null;
            _tunnelChoice = null;
            return Resume(here, "broke into open space");
        }
        var st = _tunnel!.Tick(ctx);
        if (st == GameTaskStatus.Running) return GameTaskStatus.Running;
        int dug = Dug(_tunnel);
        if (!_tally.Record(dug, Covered(_tunnel))) Memory.TunnelFailed(choice.Dir, choice.Start, Now);
        _lastTunnelStop = st == GameTaskStatus.Succeeded
            ? $"dug {DirName(choice.Dir)} {CaveTunnel.MaxTiles} tiles without reaching a cave"
            : $"{DirName(choice.Dir)} from {choice.Start} stopped: {_tunnel.Reason}";
        _tunnel = null;
        _tunnelChoice = null;
        ControlState.Pending = default;
        return Resume(here, "looking around");
    }

    /// <summary>Back to frontier exploration from here (radius now counts from here).</summary>
    private GameTaskStatus Resume(TilePoint here, string mode)
    {
        _origin = here;
        _stalls = 0;
        _darkFixAt = null;
        _pocketExhausted = false;
        _stallNote = null;
        _mode = mode;
        ControlState.Pending = default;
        return GameTaskStatus.Running;
    }

    private static int Dug(ExploreTask t) => (t.Result as ExploreResult)?.Dug ?? 0;
    private static int Covered(ExploreTask t) => (t.Result as ExploreResult)?.Covered ?? 0;

    private bool IsRejected(TilePoint s) =>
        _rejected.Any(r => CaveFrontier.Chebyshev(r, s) <= NearRejected) || _visited.Any(v => CaveFrontier.Chebyshev(v, s) <= NearRejected)
        || Memory.StandBlocked(s, Now);

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
        // A walk that stops getting closer for LegStallTicks is abandoned (that spot is rejected) so the exploration
        // goes on; before, it ran into the task-wide stall watchdog, which cancelled the whole explore_caves (seen in
        // game at a cave pool).
        int dist = Math.Max(Math.Abs(here.X - _legStand.X), Math.Abs(here.Y - _legStand.Y));
        if (dist < _legBest) { _legBest = dist; _legStallTicks = 0; }
        else _legStallTicks++;
        bool giveUp = ++_legTicks > LegTimeoutTicks || _legStallTicks > LegStallTicks;
        var st = giveUp ? GameTaskStatus.Failed : _leg!.Tick(ctx);
        if (st == GameTaskStatus.Running) return GameTaskStatus.Running;
        if (giveUp) _leg!.OnCancel(ctx);
        _leg = null;
        ControlState.Pending = default;
        bool revealedSomething = _revealed > _revealedAtLegStart;
        if (st == GameTaskStatus.Succeeded) _visited.Add(_legStand);
        else
        {
            _rejected.Add(_legStand);
            Memory.WalkFailed(_legStand, _legFrontier, Now);
        }
        // Arrived next to a dark opening (often a cave whose floor isn't known yet): light it from here.
        if (st == GameTaskStatus.Succeeded && _torches && Lighting.Brightness(_legFrontier.X, _legFrontier.Y) < TorchPlanner.DarkBrightness
            && ExploreTask.NearestTorch(here, 4) == null && ExploreTask.TorchSlot(ctx.Player) is var tslot and >= 0
            && TorchSpot(ctx, here, tslot, _legFrontier) is { } tspot)
        {
            _stalls = revealedSomething ? 0 : _stalls; // judged after the torch, on the next walk
            StartTorch(ctx.Player, tslot, tspot);
            return GameTaskStatus.Running;
        }
        if (st == GameTaskStatus.Succeeded || revealedSomething) _mode = "looking around";
        _stalls = revealedSomething ? 0 : _stalls + 1;
        if (_stalls >= MaxStalls)
        {
            // The openings left here are out of reach: stop picking them and tunnel/relocate instead (Decide fails
            // the task only when neither is possible).
            _stallNote = $"last: {(st == GameTaskStatus.Succeeded ? "revealed nothing new" : "couldn't reach " + _legStand)}";
            _pocketExhausted = true;
            _stalls = 0;
            _mode = "openings here are out of reach";
        }
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

    /// <summary>A pot gets this long (walk + swing); then it is skipped (seen in game: a pot behind rock held the task
    /// at "breaking a pot" for the whole 300 s).</summary>
    private const int MaxPotTicks = 10 * 60;
    private int _potTicks;

    private GameTaskStatus TickCollect(GameCtx ctx)
    {
        var st = _collect!.Tick(ctx);
        if (st == GameTaskStatus.Running && ++_potTicks < MaxPotTicks) return GameTaskStatus.Running;
        if (st == GameTaskStatus.Running) _collect.OnCancel(ctx); // gave up on this pot (it stays in _potsTried)
        _potTicks = 0;
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
        if (TorchSpot(ctx, here, slot, null) is not { } s) return false;
        spot = s;
        return true;
    }

    private TilePoint? TorchSpot(GameCtx ctx, TilePoint here, int slot, TilePoint? focus)
    {
        var reach = new ReachSpec(Player.tileRangeX, Player.tileRangeY, ctx.Player.inventory[slot].tileBoost);
        return TorchPlanner.BestSpot(ctx.Grid, TorchCellAt, here, reach, focus);
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
        foreach (var h in ValuableScan.Scan(p, here.X, here.Y - 1, ScanRadius, _wantOre))
        {
            _initial.Add((h.X, h.Y));
            if (h.Kind == ValuableKind.LifeCrystal) _lifeCrystals.Add(new FoundThing(Valuables.Key(h.Kind), h.Name, h.X, h.Y));
        }
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
            if (h.Kind == ValuableKind.LifeCrystal && !_lifeCrystals.Any(c => c.X == h.X && c.Y == h.Y)) _lifeCrystals.Add(thing);
            // A pot we're about to break on the way isn't a reason to stop.
            if (_stopOn.Contains(h.Kind) && !(h.Kind == ValuableKind.Pot && _collectPots) && _stopHit == null
                && StopSkip(h, thing) == null)
            {
                _stopHit = thing;
                Memory.StopReported(thing.Kind, new TilePoint(h.X, h.Y), Now);
            }
        }
    }

    /// <summary>Why this stop_on hit must not end the call (<see cref="ExploreMemory.SkipStop"/>), or null.</summary>
    private static string? StopSkip(ValuableHit h, FoundThing thing)
    {
        var at = new TilePoint(h.X, h.Y);
        bool chest = h.Kind == ValuableKind.Chest;
        bool opened = chest && Memory.ChestSeenOpened(at);
        return ExploreMemory.SkipStop(Memory.WasReported(thing.Kind, at, Now), chest,
            rigged: chest && WorldGen.IsChestRigged(h.X, h.Y), openedBefore: opened, emptyNow: opened && ChestEmpty(h.X, h.Y));
    }

    private static bool ChestEmpty(int x, int y)
    {
        int idx = Chest.FindChest(x, y);
        if (idx < 0 || Main.chest[idx]?.item == null) return false;
        foreach (var it in Main.chest[idx].item)
            if (it != null && !it.IsAir) return false;
        return true;
    }

    /// <summary>Memory's world key (see <see cref="ExploreMemory.UseWorld"/>).</summary>
    public static string WorldKey => $"{Main.worldID}:{Main.worldName}";

    private static long Key(int x, int y) => ((long)x << 32) | (uint)y;

    // ---- bookkeeping -------------------------------------------------------------------------------------------

    private void UpdateProgress()
    {
        Progress = $"{_mode}; {_elapsed / 60}/{_maxTicks / 60} s, revealed {_revealed}, found {_found.Count}"
                   + (_torchesPlaced > 0 ? $", torches {_torchesPlaced}" : "")
                   + (_tally.Counted > 0 ? $", tunnels {_tally.Counted} ({_tally.Tiles} dug)" : "")
                   + (_relocations > 0 ? $", relocations {_relocations} ({_relocated} tiles)" : "")
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
        if (_descend != null) { _descended += Dug(_descend); _descend.OnCancel(ctx); }
        if (_tunnel != null) { _tally.Record(Dug(_tunnel), Covered(_tunnel)); _tunnel.OnCancel(ctx); }
        if (_relocTask != null)
        {
            int dug = Dug(_relocTask), covered = Covered(_relocTask);
            if (_tally.Record(dug, covered)) { _relocations++; _relocated += covered; }
            _relocTask.OnCancel(ctx);
        }
        _tunnelWalk?.OnCancel(ctx);
        _descend = null;
        _tunnel = null;
        _relocTask = null;
        _tunnelWalk = null;
        _leg = null;
        _torch = null;
        _collect = null;
        ControlState.Pending = default;
    }

    private static TilePoint Here(Player p) =>
        NavMath.StandingTile(NavMath.FromPixels(p.position.X, p.position.Y, p.velocity.X, p.velocity.Y, p.height));
}
