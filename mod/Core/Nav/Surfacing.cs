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
/// <para>That free 8-way search is for swimmers (<see cref="MovementProfile.CanSwim"/>). Without swimming (vanilla
/// default: the player sinks and can only jump off the bottom) the search follows the player's real options, like
/// the pathfinder's non-swimmer water model: walk along the bottom (stepping up or down it), rise only in a water jump
/// from ground (at most <c>waterJumpUp</c> tiles), sink everywhere else. So from the bottom of a lake too deep to jump
/// the head out of, it walks along the floor toward the shallow end (the shore slope) or to where a jump reaches air.
/// Standing with the head out beats a head out only at a jump's apex (<see cref="ApexPenalty"/>).</para>
/// </summary>
public static class SurfaceDirection
{
    public const int MaxRadius = 24;
    /// <summary>How many path tiles <see cref="SurfaceChoice.Dx"/> / <see cref="SurfaceChoice.Up"/> look at.</summary>
    public const int Lookahead = 3;
    private const int UpCost = 9, SideCost = 10, DownCost = 11, DiagExtra = 4, UnknownExtra = 20;
    /// <summary>Non-swimmers: extra cost of air reached only at the top of a water jump (the player sinks back
    /// after a few breaths) over air it can stand in.</summary>
    public const int ApexPenalty = 80;
    private const int Sinking = -1;

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

    private static bool Supported(ITileGrid g, TilePoint p) =>
        g.Get(p.X, p.Y + 1).IsStandable || g.Get(p.X + 1, p.Y + 1).IsStandable;

    /// <param name="canSwim">The player can swim up anywhere (<see cref="MovementProfile.CanSwim"/>); otherwise only
    /// the bottom and water jumps from it are used.</param>
    /// <param name="waterJumpUp">Tiles one jump off the bottom rises (<see cref="MovementProfile.MaxWaterJumpUp"/>).</param>
    public static SurfaceChoice Choose(ITileGrid g, TilePoint body, int maxRadius = MaxRadius, bool canSwim = false,
        int waterJumpUp = JumpModel.VanillaWaterJumpUp)
    {
        if (Breathable(g, body)) return new SurfaceChoice(body, Array.Empty<TilePoint>(), 0, false);
        if (!canSwim) return ChooseWading(g, body, maxRadius, waterJumpUp);

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

    /// <summary>Non-swimmer search over (position, water-jump state): 0 = on ground, 1..waterJumpUp = tiles risen
    /// in a jump from ground, <see cref="Sinking"/>. Up is set only when the very next step rises (jump now: a jump
    /// started early drifts into whatever is above the bottom on the way).</summary>
    private static SurfaceChoice ChooseWading(ITileGrid g, TilePoint body, int maxRadius, int waterJumpUp)
    {
        var startKey = (body, Supported(g, body) ? 0 : Sinking);
        var dist = new Dictionary<(TilePoint P, int R), int> { [startKey] = 0 };
        var parent = new Dictionary<(TilePoint P, int R), (TilePoint P, int R)>();
        var open = new PriorityQueue<(TilePoint P, int R), (int, int)>();
        int seq = 0, bestCost = int.MaxValue;
        (TilePoint P, int R)? best = null;
        open.Enqueue(startKey, (0, seq++));
        while (open.TryDequeue(out var s, out var pr))
        {
            if (pr.Item1 != dist[s]) continue; // stale
            if (pr.Item1 >= bestCost) break;
            var p = s.P;
            if (p != body && Breathable(g, p))
            {
                int c = pr.Item1 + (s.R == 0 ? 0 : ApexPenalty);
                if (c < bestCost)
                {
                    bestCost = c;
                    best = s;
                }
                continue;
            }
            int r = s.R;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nr = Sinking;
                    if (dy < 0)
                    {
                        if (r < 0 || r >= waterJumpUp) continue;
                        nr = r + 1;
                    }
                    var t = new TilePoint(p.X + dx, p.Y + dy);
                    if (Math.Abs(t.X - body.X) > maxRadius || Math.Abs(t.Y - body.Y) > maxRadius) continue;
                    int b = Body(g, t);
                    if (b < 0) continue;
                    if (dx != 0 && dy != 0 && Body(g, new TilePoint(t.X, p.Y)) < 0 && Body(g, new TilePoint(p.X, t.Y)) < 0)
                        continue; // no corner cutting
                    bool supported = Supported(g, t);
                    if (dy == 0 && r < 0 && !supported) continue; // no drifting sideways while sinking
                    if (supported) nr = 0;
                    int c = (dy < 0 ? UpCost : dy > 0 ? DownCost : SideCost) + (dx != 0 && dy != 0 ? DiagExtra : 0)
                            + b * UnknownExtra;
                    int nd = pr.Item1 + c;
                    var key = (t, nr);
                    if (dist.TryGetValue(key, out int od) && od <= nd) continue;
                    dist[key] = nd;
                    parent[key] = s;
                    open.Enqueue(key, (nd, seq++));
                }
        }

        if (best is not { } air) return new SurfaceChoice(null, Array.Empty<TilePoint>(), 0, true); // just try up
        var path = new List<TilePoint>();
        for (var q = air; q != startKey; q = parent[q]) path.Add(q.P);
        path.Reverse();
        var look = path[Math.Min(Lookahead, path.Count) - 1];
        return new SurfaceChoice(air.P, path, Math.Sign(look.X - body.X), path[0].Y < body.Y);
    }
}

/// <summary>
/// Steers a submerged player to air with <see cref="SurfaceDirection"/>. No Flippers (<c>canSwim</c> false): rising
/// means jumping off the bottom, so like the follower's Swim step: press jump on the ground after releasing it, hold
/// it while rising; the choice is re-made (whenever the body moves to another tile, and at least every
/// <see cref="RechooseTicks"/>) only while not rising, since mid-jump the search would see a sinking body, and
/// while rising it steers for the chosen air. With swimming, jump is pressed again whenever the player stops rising.
/// </summary>
public sealed class Surfacer
{
    private readonly bool _canSwim;
    private readonly int _waterJumpUp;

    public bool CanSwim => _canSwim;

    public Surfacer(bool canSwim = false, int waterJumpUp = JumpModel.VanillaWaterJumpUp)
    {
        _canSwim = canSwim;
        _waterJumpUp = waterJumpUp;
    }

    public const int RechooseTicks = 15;
    private const float Tolerance = 0.2f;

    private SurfaceChoice? _choice;
    private TilePoint _at;
    private int _age;
    private bool _jumpHeld;

    public SurfaceChoice? Choice => _choice;

    public ControlIntent Tick(ITileGrid grid, PlayerKinematics k)
    {
        var body = NavMath.StandingTile(k, grid);
        bool rising = !_canSwim && !k.OnGround && k.VelY < 0 && _jumpHeld;
        if (_choice == null || (!rising && (body != _at || ++_age >= RechooseTicks)))
        {
            _choice = SurfaceDirection.Choose(grid, body, canSwim: _canSwim, waterJumpUp: _waterJumpUp);
            _at = body;
            _age = 0;
        }

        var intent = default(ControlIntent);
        var c = _choice;
        if (c.Path.Count > 0)
        {
            var look = rising && c.Air is { } air ? air : c.Path[Math.Min(SurfaceDirection.Lookahead, c.Path.Count) - 1];
            float tx = look.X + PlayerPhysics.CenterOffset;
            if (k.X < tx - Tolerance) intent.Right = true;
            else if (k.X > tx + Tolerance) intent.Left = true;
        }
        if (rising) intent.Jump = true; // keep holding to the top of the water jump
        else if (c.Up)
        {
            if (k.OnGround) intent.Jump = !_jumpHeld; // a new jump needs the key released first
            else if (_canSwim) intent.Jump = k.VelY < 0 ? _jumpHeld : !_jumpHeld; // swim stroke again
        }
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
