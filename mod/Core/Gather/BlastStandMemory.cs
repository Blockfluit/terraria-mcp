using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Gather;

/// <summary>
/// Throwing spots blast couldn't walk to, kept across calls (one static instance in the mod, per world; entries expire
/// after <c>ttlTicks</c> game ticks, so terrain that changed, e.g. after a blast, gets another chance). Seen in game:
/// eight blast calls in a row each picked the same spot ("can't reach the throwing spot (7533,675): unreachable:
/// searched 450 nodes"): the planner chooses stands by distance and knew nothing of the failed walks.
/// A failed stand also excludes its neighbours within <see cref="StandRadius"/> (the next pick would otherwise be a
/// neighbouring stand on the same unreachable ledge). A new world, or the game's tick counter going backwards (a
/// reload), forgets everything.
/// </summary>
public sealed class BlastStandMemory
{
    public const long DefaultTtlTicks = 5L * 60 * 60; // 5 minutes
    public const int StandRadius = 2, MaxEntries = 200;

    private readonly long _ttl;
    private readonly List<(TilePoint Stand, long Expires)> _failed = new();
    private string? _world;
    private long _lastNow = long.MinValue;

    public BlastStandMemory(long ttlTicks = DefaultTtlTicks) => _ttl = ttlTicks;

    /// <summary>Remembered failed stands (after expiry at the last call).</summary>
    public int Count => _failed.Count;

    /// <summary>Call at the start of every task: a different world forgets everything.</summary>
    public void UseWorld(string world)
    {
        if (_world == world) return;
        _world = world;
        Clear();
    }

    public void Clear()
    {
        _failed.Clear();
        _lastNow = long.MinValue;
    }

    /// <summary>The walk to this throwing spot failed.</summary>
    public void WalkFailed(TilePoint stand, long now)
    {
        Tick(now);
        _failed.Add((stand, now + _ttl));
        if (_failed.Count > MaxEntries) _failed.RemoveAt(0);
    }

    /// <summary>Is this stand (or one within <see cref="StandRadius"/> of it) a recently failed throwing spot?</summary>
    public bool Blocked(TilePoint s, long now)
    {
        Tick(now);
        foreach (var f in _failed)
            if (Math.Max(Math.Abs(f.Stand.X - s.X), Math.Abs(f.Stand.Y - s.Y)) <= StandRadius) return true;
        return false;
    }

    private void Tick(long now)
    {
        if (now < _lastNow) Clear();
        _lastNow = now;
        _failed.RemoveAll(f => f.Expires <= now);
    }
}
