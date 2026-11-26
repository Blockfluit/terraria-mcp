namespace LlmPlayer.Core.Nav;

/// <summary>
/// Detects a move that keeps "arriving" at the end of partial plans without getting closer to its goal
/// (goal beyond the planning window and no way forward), so it can fail instead of replanning forever.
/// </summary>
public sealed class ProgressGuard(int maxStalls = 3)
{
    private int _best = int.MaxValue, _stalls;

    /// <summary>Record reaching the end of a plan at this distance from the goal. True = give up.</summary>
    public bool RecordArrival(int distanceToGoal)
    {
        if (distanceToGoal < _best)
        {
            _best = distanceToGoal;
            _stalls = 0;
            return false;
        }
        return ++_stalls >= maxStalls;
    }
}
