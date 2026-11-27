using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Gather;

/// <summary>A relocation: travel <see cref="Distance"/> tiles <see cref="Dir"/> from <see cref="Start"/> (walking
/// through known caves, a 3-tall tunnel / 2-wide shaft through rock) into the large unexplored region that begins
/// <see cref="RegionAt"/> tiles out (<see cref="UnknownShare"/> percent of its window is Unknown).</summary>
public sealed record RelocateChoice(TunnelDir Dir, TilePoint Start, int Distance, int RegionAt, int UnknownShare, int Score);

public sealed record RelocateDecision(RelocateChoice? Choice, IReadOnlyList<TunnelRefusal> Refused)
{
    public string Summary => Refused.Count == 0 ? "no direction to try"
        : string.Join("; ", Refused.GroupBy(r => r.Dir).Select(g => $"{g.Key.ToString().ToLowerInvariant()}: {g.First().Why}"));
}

/// <summary>
/// Where explore_caves goes when its whole neighbourhood is exhausted (no reachable frontier within the radius, no
/// short tunnel worth digging): the nearest large unexplored region by map knowledge (pure logic over the fair-vision
/// grid, whose Unknown cells are exactly the '?' of get_map).
/// <list type="bullet">
/// <item>Directions: left/right at the current depth (the body's 3 rows, a long tunnel) and down (a 2-wide shaft).</item>
/// <item>Region: a window <see cref="Along"/> tiles deep (along the direction) by <see cref="Across"/> wide, centred on
/// the corridor, scanned outward from the player; the first window at least <see cref="MinShare"/>% Unknown is the
/// region. (Explored cave country stays partly Unknown — rock between caves is never lit — so the bar is high: only
/// country nobody has been to qualifies.) Scanned up to <c>maxScan</c> tiles out.</item>
/// <item>Travel: to the region's middle (<see cref="Along"/>/2 into it), at most <see cref="MaxTravel"/> tiles.</item>
/// <item>Safety (known cells along the whole corridor; the digging re-checks every tile with DigPlanner): no known
/// lava within <see cref="CaveTunnel.LavaMargin"/>, no lava/spikes in it, no protected, unbreakable or too-hard tile,
/// no water/honey where rock is to be dug (it would flood the tunnel). Open caves on the way are fine (walked).</item>
/// <item>Score (lowest wins): travel distance − a bonus for a more unknown region. <c>blocked</c>: (direction, start)
/// pairs already tried.</item>
/// </list>
/// </summary>
public static class CaveRelocate
{
    public const int Along = 40, Across = 30, MinShare = 85, MaxScan = 240, MaxTravel = 250, MinAway = 20;

    public static RelocateDecision Choose(ITileGrid g, TilePoint here, int pickPower,
        Func<TunnelDir, TilePoint, bool>? blocked = null, int maxScan = MaxScan)
    {
        var refused = new List<TunnelRefusal>();
        RelocateChoice? best = null;
        if (pickPower <= 0) return new RelocateDecision(null, refused);
        foreach (var dir in new[] { TunnelDir.Left, TunnelDir.Right, TunnelDir.Down })
        {
            if (blocked != null && blocked(dir, here))
            {
                refused.Add(new TunnelRefusal(dir, here, "already tried from here"));
                continue;
            }
            var (choice, why) = Evaluate(g, here, dir, pickPower, maxScan);
            if (choice == null)
            {
                refused.Add(new TunnelRefusal(dir, here, why!));
                continue;
            }
            if (best == null || choice.Score < best.Score) best = choice;
        }
        return new RelocateDecision(best, refused);
    }

    public static (RelocateChoice? Choice, string? Why) Evaluate(ITileGrid g, TilePoint here, TunnelDir dir, int pickPower,
        int maxScan = MaxScan)
    {
        // Unknown cells per slice k tiles out (a slice: Across cells across the corridor).
        var slice = new int[maxScan + Along + 1];
        for (int k = 1; k < slice.Length; k++)
            for (int l = 0; l < Across; l++)
                if (g.Get(Cell(here, dir, k, l)).Kind == TileKind.Unknown) slice[k]++;

        int window = 0, regionAt = -1, share = 0;
        for (int k = 1; k <= Along; k++) window += slice[k];
        for (int k = 1; k <= maxScan; k++)
        {
            if (k > 1) window += slice[k + Along - 1] - slice[k - 1];
            int pct = window * 100 / (Along * Across);
            if (pct >= MinShare) { regionAt = k; share = pct; break; }
        }
        if (regionAt < 0) return (null, $"no large unexplored region within {maxScan} tiles");

        int distance = Math.Min(regionAt + Along / 2, MaxTravel);
        if (CorridorProblem(g, here, dir, distance, pickPower) is { } problem) return (null, problem);
        int score = distance - (share - MinShare) / 3;
        return (new RelocateChoice(dir, here, distance, regionAt, share, score), null);
    }

    /// <summary>Why the corridor can't be travelled (known cells only), or null.</summary>
    public static string? CorridorProblem(ITileGrid g, TilePoint here, TunnelDir dir, int distance, int pickPower)
    {
        for (int k = 1; k <= distance; k++)
        {
            var section = CaveTunnel.Section(here, dir, k);
            bool digHere = false;
            foreach (var c in section)
            {
                if (CaveFrontier.LavaWithin(g, c, CaveTunnel.LavaMargin)) return $"lava near the way at {LavaAt(g, c)}";
                var t = g.Get(c);
                switch (t.Kind)
                {
                    case TileKind.Lava: return $"lava in the way at {c}";
                    case TileKind.Hazard: return $"hazard (spikes) at {c}";
                    case TileKind.Solid:
                        if (t.Protected || t.IsClosedDoor) return $"protected tile at {c}";
                        if (t.PickPowerRequired == int.MaxValue) return $"unbreakable tile at {c}";
                        if (t.PickPowerRequired > pickPower) return $"needs pick power {t.PickPowerRequired} at {c}";
                        digHere = true;
                        break;
                }
            }
            if (!digHere) continue;
            // Rock to dig here: liquid in or right beside this stretch would pour in.
            var near = section.Concat(CaveTunnel.Beside(here, dir, k))
                .Concat(k > 1 ? CaveTunnel.Section(here, dir, k - 1) : Array.Empty<TilePoint>())
                .Concat(CaveTunnel.Section(here, dir, k + 1));
            foreach (var c in near)
                if (g.Get(c).Kind is TileKind.Water or TileKind.Honey) return $"water at {c} would flood the tunnel";
        }
        return null;
    }

    /// <summary>True when a frontier cell lies far enough along the relocation to count as arriving somewhere new
    /// (frontiers near the start belong to the exhausted area we're leaving).</summary>
    public static bool Beyond(RelocateChoice r, TilePoint c)
    {
        int need = Math.Max(1, Math.Min(MinAway, r.RegionAt));
        return r.Dir switch
        {
            TunnelDir.Left => r.Start.X - c.X >= need,
            TunnelDir.Right => c.X - (r.Start.X + 1) >= need,
            _ => c.Y - r.Start.Y >= need,
        };
    }

    /// <summary>Cell l (0..Across-1) of the slice k tiles out, the window centred on the body.</summary>
    private static TilePoint Cell(TilePoint s, TunnelDir dir, int k, int l) => dir switch
    {
        TunnelDir.Left => new TilePoint(s.X - k, s.Y - 1 - Across / 2 + l),
        TunnelDir.Right => new TilePoint(s.X + 1 + k, s.Y - 1 - Across / 2 + l),
        _ => new TilePoint(s.X - Across / 2 + 1 + l, s.Y + k),
    };

    private static TilePoint LavaAt(ITileGrid g, TilePoint c)
    {
        int r = CaveTunnel.LavaMargin;
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
                if (g.Get(c.X + dx, c.Y + dy).Kind == TileKind.Lava) return new TilePoint(c.X + dx, c.Y + dy);
        return c;
    }
}
