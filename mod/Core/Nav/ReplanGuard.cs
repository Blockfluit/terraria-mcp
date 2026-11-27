using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Nav;

/// <summary>
/// Catches a move that keeps failing the same way from the same spot. MoveToTask resets its consecutive-failure count
/// whenever a plan completed at least one step, so a plan that walks one step (e.g. back onto a ledge) and then fails
/// again, forever, never ran out of retries: seen in the real-terrain fuzz as 100+ replans until the tick budget ran out
/// ("path changed at Jump" from the bottom of a pool, every ~60 ticks). Counting each (standing tile, failure) pair
/// gives up after <c>maxRepeats</c> identical failures however much the plans in between progressed.
/// </summary>
public sealed class ReplanGuard(int maxRepeats = 4)
{
    private readonly Dictionary<(TilePoint, string), int> _seen = new();

    /// <summary>Record a Stuck/NeedsReplan at <paramref name="here"/> described by <paramref name="failure"/> (e.g.
    /// "stuck at Jump (12, 40)"). True = it happened <c>maxRepeats</c> times: give up.</summary>
    public bool Record(TilePoint here, string failure)
    {
        var key = (here, failure);
        int n = _seen.GetValueOrDefault(key) + 1;
        _seen[key] = n;
        return n >= maxRepeats;
    }
}
