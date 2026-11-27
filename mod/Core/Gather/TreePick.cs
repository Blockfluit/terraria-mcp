using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Gather;

/// <summary>Trees chop_tree may fell, nearest first, and how many visible trees it passed over and why.</summary>
/// <param name="Bases">Trunk bases, nearest first (at most the requested count).</param>
/// <param name="SkippedProtected">Trees in an NPC house or near spawn (never chopped).</param>
/// <param name="SkippedHidden">Trees whose trunk base isn't visible (fair vision).</param>
public sealed record TreeChoice(IReadOnlyList<TilePoint> Bases, int SkippedProtected, int SkippedHidden)
{
    public int Skipped => SkippedProtected + SkippedHidden;

    /// <summary>"3 protected, 1 not visible" (empty when nothing was skipped).</summary>
    public string SkipText()
    {
        var parts = new List<string>();
        if (SkippedProtected > 0) parts.Add($"{SkippedProtected} protected (NPC house or near spawn)");
        if (SkippedHidden > 0) parts.Add($"{SkippedHidden} with the trunk base not visible");
        return string.Join(", ", parts);
    }
}

/// <summary>Which tree chop_tree takes when no coordinates are given. Pure.</summary>
public static class TreePick
{
    /// <summary>
    /// Every tree tile found (trunk bases or roots, any order) → distinct trunk bases (<paramref name="baseOf"/>),
    /// protected and hidden ones skipped and counted, the rest nearest to <paramref name="from"/> first (Euclidean,
    /// ties by x then y so the choice is stable), at most <paramref name="max"/>.
    /// </summary>
    public static TreeChoice Rank(IEnumerable<TilePoint> found, TilePoint from, Func<TilePoint, TilePoint?> baseOf,
        Func<TilePoint, bool> isProtected, Func<TilePoint, bool> isVisible, int max = 8)
    {
        var bases = new HashSet<TilePoint>();
        foreach (var f in found)
            if (baseOf(f) is { } b) bases.Add(b);
        int prot = 0, hidden = 0;
        var ok = new List<TilePoint>();
        foreach (var b in bases)
        {
            if (isProtected(b)) prot++;
            else if (!isVisible(b)) hidden++;
            else ok.Add(b);
        }
        long D(TilePoint t) => (long)(t.X - from.X) * (t.X - from.X) + (long)(t.Y - from.Y) * (t.Y - from.Y);
        var ranked = ok.OrderBy(D).ThenBy(t => t.X).ThenBy(t => t.Y).Take(max).ToList();
        return new TreeChoice(ranked, prot, hidden);
    }

    /// <summary>Did the chop fail because the tree couldn't be reached (so the next tree is worth a try)?</summary>
    public static bool IsReachFailure(string? reason) =>
        reason != null && (reason.StartsWith("can't reach", StringComparison.Ordinal)
                           || reason.StartsWith("couldn't get within reach", StringComparison.Ordinal));
}
