namespace LlmPlayer.Core.Observe;

/// <summary>Terraria's clock: day starts 4:30 AM and lasts 54000 ticks, night starts 7:30 PM and lasts 32400.
/// One tick = one in-game second.</summary>
public static class GameClock
{
    /// <returns>e.g. "day 6:30 AM" or "night 12:00 AM".</returns>
    public static string Format(bool dayTime, double time)
    {
        double hours = time / 3600.0 + (dayTime ? 4.5 : 19.5);
        int totalMinutes = (int)Math.Floor(hours * 60) % (24 * 60);
        int h24 = totalMinutes / 60, m = totalMinutes % 60;
        int h12 = h24 % 12 == 0 ? 12 : h24 % 12;
        return $"{(dayTime ? "day" : "night")} {h12}:{m:00} {(h24 < 12 ? "AM" : "PM")}";
    }
}

/// <summary>Depth layer from a tile row, matching vanilla Player zone heights.</summary>
public static class DepthLayer
{
    public static string Classify(int y, double worldSurface, double rockLayer, int underworldLayer)
    {
        if (y > underworldLayer) return "underworld";
        if (y > rockLayer) return "cavern";
        if (y > worldSurface) return "underground";
        if (y > worldSurface * 0.3499999940395355) return "surface";
        return "space";
    }
}

/// <summary>Inclusive tile rectangle covering the screen plus a margin.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public static ScreenRect FromPixels(float screenX, float screenY, int widthPx, int heightPx, int marginTiles)
    {
        int left = (int)Math.Floor(screenX / 16f), top = (int)Math.Floor(screenY / 16f);
        int right = (int)Math.Floor((screenX + widthPx - 1) / 16f), bottom = (int)Math.Floor((screenY + heightPx - 1) / 16f);
        return new ScreenRect(left - marginTiles, top - marginTiles, right + marginTiles, bottom + marginTiles);
    }

    public bool Contains(int x, int y) => x >= Left && x <= Right && y >= Top && y <= Bottom;
}

public static class Nearest
{
    /// <summary>Max entities returned by get_entities (keeps observations small).</summary>
    public const int EntityCap = 30;

    /// <summary>Stable sort by distance, then take at most <paramref name="max"/>.</summary>
    public static List<T> Take<T>(IEnumerable<T> items, Func<T, double> distance, int max) =>
        items.OrderBy(distance).Take(Math.Max(0, max)).ToList();
}

public readonly record struct FindCandidate(string Name, int X, int Y);

public sealed record FindResult(string Name, int X, int Y, double Distance);

public static class FindSelector
{
    public const int MaxResults = 10;

    /// <summary>
    /// Nearest candidates to (<paramref name="originX"/>, <paramref name="originY"/>), at most <paramref name="max"/>.
    /// A candidate is skipped if an already-picked one with the same name lies within
    /// <paramref name="clusterRadius"/> tiles (Chebyshev), so one ore vein counts once.
    /// <paramref name="name"/>, if given, filters by case-insensitive substring.
    /// </summary>
    public static List<FindResult> Select(IEnumerable<FindCandidate> candidates, int originX, int originY,
        int max = MaxResults, int clusterRadius = 0, string? name = null)
    {
        var picked = new List<FindResult>();
        var filtered = string.IsNullOrWhiteSpace(name)
            ? candidates
            : candidates.Where(c => c.Name.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase));
        foreach (var c in filtered.OrderBy(c => Dist(c.X - originX, c.Y - originY)))
        {
            if (picked.Count >= max) break;
            if (clusterRadius > 0 && picked.Any(p => p.Name == c.Name
                    && Math.Abs(p.X - c.X) <= clusterRadius && Math.Abs(p.Y - c.Y) <= clusterRadius))
                continue;
            picked.Add(new FindResult(c.Name, c.X, c.Y, Math.Round(Dist(c.X - originX, c.Y - originY), 1)));
        }
        return picked;
    }

    private static double Dist(int dx, int dy) => Math.Sqrt((double)dx * dx + (double)dy * dy);
}

public readonly record struct ItemName(string Display, string Internal);

public static class ItemMatcher
{
    /// <summary>Index of the first slot whose display name equals <paramref name="query"/> (case-insensitive),
    /// else the first whose internal name does; -1 if none. Null entries are empty slots.</summary>
    public static int Find(IReadOnlyList<ItemName?> slots, string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return -1;
        for (int i = 0; i < slots.Count; i++)
            if (slots[i] is { } s && string.Equals(s.Display, q, StringComparison.OrdinalIgnoreCase)) return i;
        for (int i = 0; i < slots.Count; i++)
            if (slots[i] is { } s && string.Equals(s.Internal, q, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}

/// <summary>Small ring buffer of human-readable events (damage, kills, pickups) for get_state.
/// Consecutive repeats collapse into "msg (xN)".</summary>
public sealed class EventLog(int capacity = 20)
{
    private readonly LinkedList<(string Msg, int Count)> _items = new();

    public void Add(string message)
    {
        if (_items.Last is { } last && last.Value.Msg == message)
        {
            last.Value = (message, last.Value.Count + 1);
            return;
        }
        _items.AddLast((message, 1));
        while (_items.Count > capacity) _items.RemoveFirst();
    }

    public List<string> Recent() =>
        _items.Select(e => e.Count > 1 ? $"{e.Msg} (x{e.Count})" : e.Msg).ToList();

    public List<string> Drain()
    {
        var r = Recent();
        _items.Clear();
        return r;
    }

    public void Clear() => _items.Clear();
}
