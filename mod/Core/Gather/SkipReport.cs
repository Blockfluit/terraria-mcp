using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Gather;

/// <summary>One tile an area task left alone, and why.</summary>
public readonly record struct SkippedTile(int X, int Y, string Reason);

/// <summary>
/// Tiles an area task (mine_area, build_platforms) skipped instead of failing the whole call: a count per reason
/// (<see cref="ByReason"/>) plus the first <see cref="MaxExamples"/> tiles with their reason (<see cref="Examples"/>).
/// Reasons listed in <c>quiet</c> (e.g. "air" for mine_area) are counted but never used as examples, so the examples
/// show the interesting skips.
/// </summary>
public sealed class SkipReport
{
    public const int MaxExamples = 10;

    private readonly HashSet<string> _quiet;

    public SkipReport(params string[] quiet) => _quiet = new HashSet<string>(quiet);

    public int Count { get; private set; }
    public Dictionary<string, int> ByReason { get; } = new();
    public List<SkippedTile> Examples { get; } = new();

    public void Add(TilePoint t, string reason)
    {
        Count++;
        ByReason[reason] = ByReason.GetValueOrDefault(reason) + 1;
        if (!_quiet.Contains(reason) && Examples.Count < MaxExamples) Examples.Add(new SkippedTile(t.X, t.Y, reason));
    }

    /// <summary>"3 protected, 2 occupied" (most frequent first, ties by name); "" when nothing was skipped.</summary>
    public string Summary() =>
        string.Join(", ", ByReason.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Value} {kv.Key}"));
}
