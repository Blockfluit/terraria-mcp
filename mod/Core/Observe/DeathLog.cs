namespace LlmPlayer.Core.Observe;

/// <summary>One death: vanilla's death text, what killed the player (NPC / projectile / fall / drowning / lava …),
/// the tile it happened at (feet), the in-game clock and the game tick it happened on.</summary>
public sealed record DeathRecord(string Text, string? Killer, int X, int Y, string Time, long Tick);

/// <summary>A death as get_state shows it (<c>lastDeaths</c>): the record plus how long ago, in seconds of play.</summary>
public sealed record DeathView(string Text, string? Killer, int X, int Y, string Time, long SecondsAgo);

/// <summary>
/// The last few deaths, kept apart from the recent-events log. Seen in game: after two deaths get_state's recent
/// events had no "died" entry at all — the 20-entry log is drained by every get_state and its oldest entries were
/// pushed out by reflex/damage lines between the death and the next read. This list is never drained and nothing
/// else can evict it; only <see cref="Capacity"/> newer deaths, or a different world, replace an entry. Pure.
/// </summary>
public sealed class DeathLog(int capacity = 5)
{
    private readonly List<DeathRecord> _items = new();

    public int Capacity => capacity;
    public int Count => _items.Count;

    public void Add(DeathRecord d)
    {
        _items.Add(d);
        while (_items.Count > capacity) _items.RemoveAt(0);
    }

    /// <summary>Newest first. A tick counter that went backwards (world reloaded) shows 0 seconds ago.</summary>
    public List<DeathView> View(long nowTick) =>
        _items.AsEnumerable().Reverse()
            .Select(d => new DeathView(d.Text, d.Killer, d.X, d.Y, d.Time, Math.Max(0, nowTick - d.Tick) / 60))
            .ToList();

    public void Clear() => _items.Clear();
}
