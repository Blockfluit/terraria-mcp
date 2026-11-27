using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Gather;

public enum TunnelDir { Down, Left, Right }

/// <summary>A tunnel explore_caves digs once its cave pocket is exhausted: walk to <see cref="Start"/>, then dig
/// <see cref="Dir"/> (a 2-wide shaft down, or a 3-tall tunnel sideways). <see cref="ToUnknown"/>: tiles of known
/// rock/open space before the first Unknown tile in the corridor; <see cref="UnknownMass"/>: Unknown tiles in the
/// window beyond it.</summary>
public sealed record TunnelChoice(TunnelDir Dir, TilePoint Start, int ToUnknown, int UnknownMass, int Score);

/// <summary>A direction (from a start) that was ruled out, and why (e.g. "lava near the tunnel at (10,20)").</summary>
public sealed record TunnelRefusal(TunnelDir Dir, TilePoint Start, string Why);

public sealed record TunnelDecision(TunnelChoice? Choice, IReadOnlyList<TunnelRefusal> Refused)
{
    /// <summary>One line per direction: its first refusal.</summary>
    public string Summary => Refused.Count == 0 ? "no stand to dig from"
        : string.Join("; ", Refused.GroupBy(r => r.Dir).Select(g => $"{g.Key.ToString().ToLowerInvariant()}: {g.First().Why}"));
}

/// <summary>
/// Where explore_caves tunnels when no reachable frontier is left (pure logic over the fair-vision grid).
/// <list type="bullet">
/// <item>Starts: standable, known spots of the current pocket (the <see cref="CaveFrontier.Flood"/> from the player):
/// for down the lowest ones, for left/right the outermost ones on that side (nearest to the player first), at most
/// <see cref="StartsPerDir"/> per direction, at least 3 tiles apart.</item>
/// <item>Corridor: the body's 2 columns below the stand (down) or its 3 rows beside it (left/right), up to
/// <c>maxTiles</c>. Up to the first Unknown cell every corridor cell must be air, a platform or rock the pickaxe can
/// break (not protected, unbreakable or too hard, no spikes, water or lava); water right beside the corridor would
/// flood it. Known lava within <see cref="LavaMargin"/> of the corridor anywhere along its length (also beyond the
/// first Unknown cell) refuses the direction. The digging itself re-checks every tile (<see cref="DigPlanner"/>).</item>
/// <item>Worth it: an Unknown cell within <c>maxTiles</c> and at least <see cref="MinUnknownMass"/> Unknown cells in
/// the window beyond it (<see cref="LookBeyond"/> deep, <see cref="LookSide"/> to each side): a large unrevealed
/// region, where the caves are.</item>
/// <item>Score (lowest wins): 2 x tiles to the unknown + half the walk to the start + <see cref="SidePenalty"/> for
/// sideways (deeper is better: Life Crystals and the cavern layer are below) − a bonus for a larger unknown mass.</item>
/// </list>
/// </summary>
public static class CaveTunnel
{
    public const int MaxTiles = 40, LavaMargin = 3, MinUnknownMass = 30, LookBeyond = 12, LookSide = 8;
    public const int SidePenalty = 8, StartsPerDir = 4;

    public static TunnelDecision Choose(ITileGrid g, TilePoint here, TilePoint origin, int radius, int pickPower,
        Func<TunnelDir, TilePoint, bool>? tried = null, int maxTiles = MaxTiles)
    {
        var refused = new List<TunnelRefusal>();
        if (pickPower <= 0) return new TunnelDecision(null, refused);
        var dist = CaveFrontier.Flood(g, here, origin, radius, pickPower);
        var stands = dist.Keys.Where(s => CaveFrontier.IsStand(g, s)).ToList();
        if (!stands.Contains(here) && CaveFrontier.IsStand(g, here)) { stands.Add(here); dist[here] = 0; }

        TunnelChoice? best = null;
        foreach (var dir in new[] { TunnelDir.Down, TunnelDir.Left, TunnelDir.Right })
        {
            Func<TilePoint, int> outward = dir switch
            {
                TunnelDir.Down => s => s.Y,
                TunnelDir.Left => s => -s.X,
                _ => s => s.X,
            };
            var starts = new List<TilePoint>();
            foreach (var s in stands.OrderByDescending(outward).ThenBy(s => dist.GetValueOrDefault(s)))
            {
                if (starts.Count >= StartsPerDir) break;
                if (starts.Any(o => CaveFrontier.Chebyshev(o, s) < 3)) continue;
                starts.Add(s);
            }
            foreach (var s in starts)
            {
                if (tried != null && tried(dir, s))
                {
                    refused.Add(new TunnelRefusal(dir, s, "already tried from here"));
                    continue;
                }
                var (choice, why) = Evaluate(g, s, dir, pickPower, maxTiles, dist.GetValueOrDefault(s));
                if (choice == null)
                {
                    refused.Add(new TunnelRefusal(dir, s, why!));
                    continue;
                }
                if (best == null || choice.Score < best.Score) best = choice;
                break; // the best start of this direction
            }
        }
        return new TunnelDecision(best, refused);
    }

    /// <summary>One direction from one start: a choice, or why not.</summary>
    public static (TunnelChoice? Choice, string? Why) Evaluate(ITileGrid g, TilePoint start, TunnelDir dir, int pickPower,
        int maxTiles = MaxTiles, int walk = 0)
    {
        int? firstUnknown = null;
        for (int k = 1; k <= maxTiles; k++)
        {
            var section = Section(start, dir, k);
            // Known lava near the corridor, anywhere along it.
            foreach (var c in section)
                if (CaveFrontier.LavaWithin(g, c, LavaMargin)) return (null, $"lava near the tunnel at {LavaNear(g, c)}");
            if (firstUnknown != null) continue;
            foreach (var c in section)
            {
                var t = g.Get(c);
                switch (t.Kind)
                {
                    case TileKind.Unknown:
                        firstUnknown ??= k;
                        continue;
                    case TileKind.Air or TileKind.Platform:
                        continue;
                    case TileKind.Water or TileKind.Honey:
                        return (null, $"liquid in the way at {c}");
                    case TileKind.Lava:
                        return (null, $"lava in the way at {c}");
                    case TileKind.Hazard:
                        return (null, $"hazard (spikes) at {c}");
                }
                if (t.Protected || t.IsClosedDoor) return (null, $"protected tile at {c}");
                if (t.PickPowerRequired == int.MaxValue) return (null, $"unbreakable tile at {c}");
                if (t.PickPowerRequired > pickPower) return (null, $"needs pick power {t.PickPowerRequired} at {c}");
            }
            if (firstUnknown != null) continue;
            foreach (var c in Beside(start, dir, k))
                if (g.Get(c).Kind is TileKind.Water or TileKind.Honey) return (null, $"water next to the tunnel at {c} would flood it");
        }
        if (firstUnknown is not { } fu) return (null, $"no unexplored space within {maxTiles} tiles");
        int mass = UnknownMass(g, start, dir, fu);
        if (mass < MinUnknownMass) return (null, $"only {mass} unknown tiles that way");
        int score = 2 * fu + walk / 2 + (dir == TunnelDir.Down ? 0 : SidePenalty) - Math.Min(mass, 200) / 20;
        return (new TunnelChoice(dir, start, fu - 1, mass, score), null);
    }

    /// <summary>The corridor cells k tiles out from the stand.</summary>
    public static TilePoint[] Section(TilePoint s, TunnelDir dir, int k) => dir switch
    {
        TunnelDir.Down => new[] { new TilePoint(s.X, s.Y + k), new TilePoint(s.X + 1, s.Y + k) },
        TunnelDir.Left => new[] { new TilePoint(s.X - k, s.Y - 2), new TilePoint(s.X - k, s.Y - 1), new TilePoint(s.X - k, s.Y) },
        _ => new[] { new TilePoint(s.X + 1 + k, s.Y - 2), new TilePoint(s.X + 1 + k, s.Y - 1), new TilePoint(s.X + 1 + k, s.Y) },
    };

    /// <summary>The cells right beside the corridor at step k (left/right of a shaft, above/below a tunnel).</summary>
    private static TilePoint[] Beside(TilePoint s, TunnelDir dir, int k) => dir switch
    {
        TunnelDir.Down => new[] { new TilePoint(s.X - 1, s.Y + k), new TilePoint(s.X + 2, s.Y + k) },
        TunnelDir.Left => new[] { new TilePoint(s.X - k, s.Y - 3), new TilePoint(s.X - k, s.Y + 1) },
        _ => new[] { new TilePoint(s.X + 1 + k, s.Y - 3), new TilePoint(s.X + 1 + k, s.Y + 1) },
    };

    /// <summary>True when <paramref name="c"/> lies in the corridor (or within <paramref name="margin"/> beside it),
    /// from the stand's own body up to <paramref name="length"/> tiles out.</summary>
    public static bool InCorridor(TilePoint start, TunnelDir dir, int length, TilePoint c, int margin = 1) => dir switch
    {
        TunnelDir.Down => c.X >= start.X - margin && c.X <= start.X + 1 + margin && c.Y >= start.Y - 2 && c.Y <= start.Y + length,
        TunnelDir.Left => c.Y >= start.Y - 2 - margin && c.Y <= start.Y + margin && c.X <= start.X + 1 && c.X >= start.X - length,
        _ => c.Y >= start.Y - 2 - margin && c.Y <= start.Y + margin && c.X >= start.X && c.X <= start.X + 1 + length,
    };

    private static int UnknownMass(ITileGrid g, TilePoint s, TunnelDir dir, int from)
    {
        int n = 0;
        for (int k = from; k < from + LookBeyond; k++)
            for (int l = -LookSide; l <= LookSide + 1; l++)
            {
                var c = dir switch
                {
                    TunnelDir.Down => new TilePoint(s.X + l, s.Y + k),
                    TunnelDir.Left => new TilePoint(s.X - k, s.Y - 1 + l),
                    _ => new TilePoint(s.X + 1 + k, s.Y - 1 + l),
                };
                if (g.Get(c).Kind == TileKind.Unknown) n++;
            }
        return n;
    }

    private static TilePoint LavaNear(ITileGrid g, TilePoint c)
    {
        for (int dy = -LavaMargin; dy <= LavaMargin; dy++)
            for (int dx = -LavaMargin; dx <= LavaMargin; dx++)
                if (g.Get(c.X + dx, c.Y + dy).Kind == TileKind.Lava) return new TilePoint(c.X + dx, c.Y + dy);
        return c;
    }

    /// <summary>Tiles explore_caves digs/walks down from <paramref name="feetY"/> to get below the surface layer
    /// (<paramref name="worldSurface"/> = Main.worldSurface) by <paramref name="margin"/> tiles; 0 when already
    /// underground.</summary>
    public static int DescentDistance(int feetY, double worldSurface, int margin = 10) =>
        feetY > worldSurface ? 0 : (int)Math.Ceiling(worldSurface) - feetY + margin;
}
