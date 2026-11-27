using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Gather;

/// <summary>
/// What explore_caves learned the hard way, kept across calls (one static instance in the mod, per world, entries
/// expire after <c>ttlTicks</c> game ticks). Seen in game: six explore_caves calls in a row from the same spot each
/// walked at the same unreachable ledge ((3944..3947, 1162..1164)) because failed stands were only remembered within
/// one call.
/// <list type="bullet">
/// <item>Failed walks: the stand is blocked (and stands within <see cref="StandRadius"/>); once
/// <see cref="FrontierStrikes"/> walks toward frontiers within <see cref="FrontierRadius"/> of each other failed, frontier
/// cells around there are ignored too (the next pick would otherwise be a neighbouring stand on the same ledge).</item>
/// <item>Failed tunnels/relocations: (direction, start) pairs that couldn't be walked to or dug at all.</item>
/// <item>Reported stops: a valuable that already ended a call (stop_on) never stops a later call again. Seen in game:
/// four calls in a row stopped at the same unreachable underwater Water Chest, and loot_chest failed each time.</item>
/// <item>Opened chests (no expiry): chests the player has had open; one of them found empty again is known to be
/// looted (fair: the player saw inside), so it doesn't stop the explorer (<see cref="SkipStop"/>).</item>
/// <item>A new world (or the game's tick counter going backwards, i.e. a reload) forgets everything.</item>
/// </list>
/// </summary>
public sealed class ExploreMemory
{
    public const long DefaultTtlTicks = 20L * 60 * 60; // 20 minutes
    public const int StandRadius = 2, FrontierRadius = 4, FrontierStrikes = 2, TunnelRadius = 2, MaxEntries = 400;

    private readonly long _ttl;
    private string? _world;
    private long _lastNow = long.MinValue;
    private readonly List<(TilePoint Stand, TilePoint Frontier, long Expires)> _walks = new();
    private readonly List<(TunnelDir Dir, TilePoint Start, long Expires)> _tunnels = new();
    private List<TilePoint>? _blockedFrontiers; // cache: centres of frontier areas with enough strikes
    private readonly List<(string Kind, TilePoint At, long Expires)> _reported = new();
    private readonly HashSet<TilePoint> _openedChests = new();

    public ExploreMemory(long ttlTicks = DefaultTtlTicks) => _ttl = ttlTicks;

    public int FailedWalks => _walks.Count;
    public int FailedTunnels => _tunnels.Count;

    /// <summary>Call at the start of every task: a different world forgets everything.</summary>
    public void UseWorld(string world)
    {
        if (_world == world) return;
        _world = world;
        Clear();
    }

    public void Clear()
    {
        _walks.Clear();
        _tunnels.Clear();
        _reported.Clear();
        _openedChests.Clear();
        _blockedFrontiers = null;
        _lastNow = long.MinValue;
    }

    public void WalkFailed(TilePoint stand, TilePoint frontier, long now)
    {
        Tick(now);
        _walks.Add((stand, frontier, now + _ttl));
        if (_walks.Count > MaxEntries) _walks.RemoveAt(0);
        _blockedFrontiers = null;
    }

    public bool StandBlocked(TilePoint s, long now)
    {
        Tick(now);
        foreach (var w in _walks)
            if (CaveFrontier.Chebyshev(w.Stand, s) <= StandRadius) return true;
        return false;
    }

    /// <summary>True when the frontier cell lies near <see cref="FrontierStrikes"/> or more failed walks' frontiers.</summary>
    public bool FrontierBlocked(TilePoint f, long now)
    {
        Tick(now);
        _blockedFrontiers ??= _walks
            .Where(w => _walks.Count(o => CaveFrontier.Chebyshev(o.Frontier, w.Frontier) <= FrontierRadius) >= FrontierStrikes)
            .Select(w => w.Frontier).Distinct().ToList();
        foreach (var c in _blockedFrontiers)
            if (CaveFrontier.Chebyshev(c, f) <= FrontierRadius) return true;
        return false;
    }

    public void TunnelFailed(TunnelDir dir, TilePoint start, long now)
    {
        Tick(now);
        _tunnels.Add((dir, start, now + _ttl));
        if (_tunnels.Count > MaxEntries) _tunnels.RemoveAt(0);
    }

    public bool TunnelBlocked(TunnelDir dir, TilePoint start, long now)
    {
        Tick(now);
        foreach (var t in _tunnels)
            if (t.Dir == dir && CaveFrontier.Chebyshev(t.Start, start) <= TunnelRadius) return true;
        return false;
    }

    /// <summary>This valuable (kind + object origin) ended an explore_caves call.</summary>
    public void StopReported(string kind, TilePoint at, long now)
    {
        Tick(now);
        if (WasReported(kind, at, now)) return;
        _reported.Add((kind, at, now + _ttl));
        if (_reported.Count > MaxEntries) _reported.RemoveAt(0);
    }

    public bool WasReported(string kind, TilePoint at, long now)
    {
        Tick(now);
        foreach (var r in _reported)
            if (r.Kind == kind && r.At == at) return true;
        return false;
    }

    /// <summary>The player had this chest (its top-left tile) open.</summary>
    public void ChestOpened(TilePoint at)
    {
        if (_openedChests.Count < MaxEntries * 10) _openedChests.Add(at);
    }

    public bool ChestSeenOpened(TilePoint at) => _openedChests.Contains(at);

    /// <summary>
    /// Why a stop_on valuable must not stop explore_caves (it is still listed in <c>found</c>), or null to stop:
    /// reported by an earlier call; a rigged Dead Man's Chest (open_chest refuses it); a chest the player opened before
    /// that is empty now (<paramref name="emptyNow"/> is only consulted for those: an unopened chest's contents are
    /// unknown to the player).
    /// </summary>
    public static string? SkipStop(bool reported, bool isChest, bool rigged, bool openedBefore, bool emptyNow)
    {
        if (reported) return "already reported";
        if (!isChest) return null;
        if (rigged) return "rigged";
        if (openedBefore && emptyNow) return "looted";
        return null;
    }

    /// <summary>Drop expired entries; a clock that went backwards (world reloaded) forgets everything.</summary>
    private void Tick(long now)
    {
        if (now < _lastNow) Clear();
        _lastNow = now;
        if (_walks.RemoveAll(w => w.Expires <= now) > 0) _blockedFrontiers = null;
        _tunnels.RemoveAll(t => t.Expires <= now);
        _reported.RemoveAll(r => r.Expires <= now);
    }
}

/// <summary>
/// explore_caves' tunnel bookkeeping. A tunnel only counts (toward the per-area limit) when it dug something or moved
/// the player (<see cref="Counts"/>); an attempt that couldn't even start (its start unreachable, its first dig
/// refused) is "wasted": the caller remembers it and tries another direction/stand. Seen in game: "tunnels 10,
/// tunnelled 0" — ten walks to tunnel starts the flood thought reachable all failed, and the limit ended the task.
/// <see cref="NewArea"/> (after a relocation) resets the per-area counters.
/// </summary>
public sealed class TunnelTally
{
    public const int MaxWastedPerArea = 8, MinMoved = 2;

    public int Counted { get; private set; }
    public int Wasted { get; private set; }
    public int Tiles { get; private set; }
    public int CountedHere { get; private set; }
    public int WastedHere { get; private set; }

    public static bool Counts(int dug, int covered) => dug > 0 || covered >= MinMoved;

    /// <summary>Record a finished attempt; true when it counted.</summary>
    public bool Record(int dug, int covered)
    {
        Tiles += Math.Max(0, dug);
        if (Counts(dug, covered))
        {
            Counted++;
            CountedHere++;
            return true;
        }
        Wasted++;
        WastedHere++;
        return false;
    }

    public bool LimitReached(int maxPerArea) => CountedHere >= maxPerArea || WastedHere >= MaxWastedPerArea;

    public void NewArea()
    {
        CountedHere = 0;
        WastedHere = 0;
    }
}
