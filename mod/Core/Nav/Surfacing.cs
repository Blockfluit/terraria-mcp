using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Nav;

/// <summary>Where to go for air: <see cref="Path"/> runs from the body's position (exclusive) to <see cref="Air"/>,
/// the nearest position whose head row is known and dry. Dx/Up summarise the next few tiles of it.</summary>
public sealed record SurfaceChoice(TilePoint? Air, IReadOnlyList<TilePoint> Path, int Dx, bool Up)
{
    public bool Found => Air != null;
}

/// <summary>
/// Picks the way to the nearest air for a player whose head is under water (pure; fair-vision grid). Searches body
/// positions (2x3, feet-left like the pathfinder) through every cell that isn't solid, lava or a hazard, 8-way with
/// no corner cutting, within <see cref="MaxRadius"/> tiles. Unknown cells may be crossed at a higher cost (they are
/// likely rock) but are never the air target. The target is a position whose head row (both cells) is known and
/// holds no water/honey. Ties go up first, then sideways: the cost of a step up is the lowest.
/// </summary>
public static class SurfaceDirection
{
    public const int MaxRadius = 24;
    /// <summary>How many path tiles <see cref="SurfaceChoice.Dx"/> / <see cref="SurfaceChoice.Up"/> look at.</summary>
    public const int Lookahead = 3;
    private const int UpCost = 9, SideCost = 10, DownCost = 11, DiagExtra = 4, UnknownExtra = 20;

    public static bool IsLiquid(TileKind k) => k is TileKind.Water or TileKind.Honey;

    /// <summary>Either head cell (top body row) holds water/honey.</summary>
    public static bool HeadInLiquid(ITileGrid g, TilePoint body) =>
        IsLiquid(g.Get(body.X, body.Y - 2).Kind) || IsLiquid(g.Get(body.X + 1, body.Y - 2).Kind);

    private static bool Breathable(ITileGrid g, TilePoint body)
    {
        for (int x = body.X; x <= body.X + 1; x++)
        {
            var k = g.Get(x, body.Y - 2).Kind;
            if (k == TileKind.Unknown || IsLiquid(k)) return false;
        }
        return true;
    }

    /// <summary>-1 blocked, 0 open, 1 open but some cell unknown.</summary>
    private static int Body(ITileGrid g, TilePoint p)
    {
        int unknown = 0;
        for (int y = p.Y - 2; y <= p.Y; y++)
            for (int x = p.X; x <= p.X + 1; x++)
            {
                var k = g.Get(x, y).Kind;
                if (k is TileKind.Solid or TileKind.Lava or TileKind.Hazard) return -1;
                if (k == TileKind.Unknown) unknown = 1;
            }
        return unknown;
    }

    public static SurfaceChoice Choose(ITileGrid g, TilePoint body, int maxRadius = MaxRadius)
    {
        if (Breathable(g, body)) return new SurfaceChoice(body, Array.Empty<TilePoint>(), 0, false);

        var dist = new Dictionary<TilePoint, int> { [body] = 0 };
        var parent = new Dictionary<TilePoint, TilePoint>();
        var open = new PriorityQueue<TilePoint, (int, int)>();
        int seq = 0;
        open.Enqueue(body, (0, seq++));
        TilePoint? found = null;
        while (open.TryDequeue(out var p, out var pr))
        {
            if (pr.Item1 != dist[p]) continue; // stale
            if (p != body && Breathable(g, p))
            {
                found = p;
                break;
            }
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var t = new TilePoint(p.X + dx, p.Y + dy);
                    if (Math.Abs(t.X - body.X) > maxRadius || Math.Abs(t.Y - body.Y) > maxRadius) continue;
                    int b = Body(g, t);
                    if (b < 0) continue;
                    if (dx != 0 && dy != 0 && Body(g, new TilePoint(t.X, p.Y)) < 0 && Body(g, new TilePoint(p.X, t.Y)) < 0)
                        continue; // no corner cutting
                    int c = (dy < 0 ? UpCost : dy > 0 ? DownCost : SideCost) + (dx != 0 && dy != 0 ? DiagExtra : 0)
                            + b * UnknownExtra;
                    int nd = dist[p] + c;
                    if (dist.TryGetValue(t, out int od) && od <= nd) continue;
                    dist[t] = nd;
                    parent[t] = p;
                    open.Enqueue(t, (nd, seq++));
                }
        }

        if (found is not { } air) return new SurfaceChoice(null, Array.Empty<TilePoint>(), 0, true); // just try up
        var path = new List<TilePoint>();
        for (var q = air; q != body; q = parent[q]) path.Add(q);
        path.Reverse();

        var look = path[Math.Min(Lookahead, path.Count) - 1];
        bool up = false;
        for (int i = 0; i < Math.Min(Lookahead, path.Count); i++) up |= path[i].Y < body.Y;
        return new SurfaceChoice(air, path, Math.Sign(look.X - body.X), up);
    }
}

/// <summary>
/// Steers a submerged player to air with <see cref="SurfaceDirection"/> (no Flippers: rising means jumping off the
/// bottom, so like the follower's Swim step: press jump on the ground after releasing it, hold it while rising).
/// The choice is re-made whenever the body moves to another tile, and at least every <see cref="RechooseTicks"/>.
/// </summary>
public sealed class Surfacer
{
    public const int RechooseTicks = 15;
    private const float Tolerance = 0.2f;

    private SurfaceChoice? _choice;
    private TilePoint _at;
    private int _age;
    private bool _jumpHeld;

    public SurfaceChoice? Choice => _choice;

    public ControlIntent Tick(ITileGrid grid, PlayerKinematics k)
    {
        var body = NavMath.StandingTile(k);
        if (_choice == null || body != _at || ++_age >= RechooseTicks)
        {
            _choice = SurfaceDirection.Choose(grid, body);
            _at = body;
            _age = 0;
        }

        var intent = default(ControlIntent);
        var c = _choice;
        if (c.Path.Count > 0)
        {
            var look = c.Path[Math.Min(SurfaceDirection.Lookahead, c.Path.Count) - 1];
            float tx = look.X + PlayerPhysics.CenterOffset;
            if (k.X < tx - Tolerance) intent.Right = true;
            else if (k.X > tx + Tolerance) intent.Left = true;
        }
        if (c.Up) intent.Jump = k.OnGround ? !_jumpHeld : _jumpHeld && k.VelY < 0;
        _jumpHeld = intent.Jump;
        return intent;
    }

    public void Reset()
    {
        _choice = null;
        _age = 0;
        _jumpHeld = false;
    }
}
