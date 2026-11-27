using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Nav;

/// <summary>
/// Travel memory: a sparse graph of tiles the player actually stood on (one node every <see cref="Spacing"/> tiles of
/// walking), linked in the order they were walked. Seen in game: move_to from home (4195,550) to (7600,676), 3400 tiles
/// east, ended "unreachable: no progress" in an underground dead end four times, while walking the same trip through
/// surface spots walked before worked. Long trips route over this graph first (<see cref="Route"/>) and use its nodes
/// as intermediate targets (<see cref="TrailTravel"/>).
/// <list type="bullet">
/// <item>Fair: only positions the player's own body stood on (in game the map reveals them anyway).</item>
/// <item>A sample joins the node within <see cref="MergeRadius"/> if there is one (walking the same road twice adds no
/// nodes, and crossing an old trail links the two), else it becomes a new node.</item>
/// <item>A jump in position (teleport, respawn, recall: more than <see cref="BreakDistance"/> tiles since the last
/// sample) or <see cref="Break"/> starts a new chain: nothing links across it.</item>
/// <item>Edges remember which way they were walked; going against that costs more (a fall may not climb back), and
/// edges below the surface cost more than surface ones (long trips prefer surface trails).</item>
/// </list>
/// Pure; one instance per world in the mod (memory only, for the session).
/// </summary>
public sealed class TrailGraph
{
    public const int Spacing = 8, MergeRadius = 4, MergeRows = 2, BreakDistance = 12, MaxNodes = 50000;
    /// <summary>Cost factor of an edge with an end below the surface (surface edges cost their length).</summary>
    public const double UndergroundFactor = 1.3;
    /// <summary>Cost factor for walking an edge against the way it was walked.</summary>
    public const double ReverseFactor = 1.5;
    /// <summary>Extra cost per tile climbed against the walked direction (a fall the player may have to build back up).</summary>
    public const double ReverseClimbCost = 2;

    private readonly List<TilePoint> _nodes = new();
    private readonly List<bool> _surface = new();
    // adjacency: node -> (neighbour -> walked from node to neighbour)
    private readonly List<Dictionary<int, bool>> _adj = new();
    private readonly Dictionary<(int, int), List<int>> _cells = new();
    private int _last = -1;
    private TilePoint? _lastSample;

    public int Count => _nodes.Count;
    public TilePoint Node(int i) => _nodes[i];
    public bool IsSurface(int i) => _surface[i];
    public IEnumerable<int> Neighbours(int i) => _adj[i].Keys;
    public bool Walked(int from, int to) => _adj[from].TryGetValue(to, out bool w) && w;

    /// <summary>Forget everything (new world).</summary>
    public void Clear()
    {
        _nodes.Clear();
        _surface.Clear();
        _adj.Clear();
        _cells.Clear();
        _last = -1;
        _lastSample = null;
    }

    /// <summary>The next sample starts a new chain (death, teleport).</summary>
    public void Break()
    {
        _last = -1;
        _lastSample = null;
    }

    /// <summary>
    /// The player stands on <paramref name="feet"/> (on the ground). Call every tick or every few; samples closer than
    /// <see cref="Spacing"/> to the last node are ignored. <paramref name="surface"/>: at or above the world's surface
    /// layer.
    /// </summary>
    public void Record(TilePoint feet, bool surface)
    {
        if (_lastSample is { } ls && Chebyshev(ls, feet) > BreakDistance) _last = -1;
        _lastSample = feet;
        if (_last >= 0 && Chebyshev(_nodes[_last], feet) < Spacing) return;
        int n = MergeTarget(feet);
        if (n < 0)
        {
            if (_nodes.Count >= MaxNodes) { _last = -1; return; }
            n = Add(feet, surface);
        }
        if (_last >= 0 && _last != n) Link(_last, n);
        _last = n;
    }

    /// <summary>The node a sample joins: within <see cref="MergeRadius"/> columns and <see cref="MergeRows"/> rows
    /// (a tunnel a few rows under a road is a different place), nearest first; -1 = none.</summary>
    private int MergeTarget(TilePoint p)
    {
        int best = -1;
        double bd = double.MaxValue;
        foreach (int i in Within(p, MergeRadius))
        {
            if (Math.Abs(_nodes[i].Y - p.Y) > MergeRows) continue;
            double d = Euclid(_nodes[i], p);
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    private int Add(TilePoint p, bool surface)
    {
        int i = _nodes.Count;
        _nodes.Add(p);
        _surface.Add(surface);
        _adj.Add(new Dictionary<int, bool>());
        var key = Cell(p);
        if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<int>();
        list.Add(i);
        return i;
    }

    private void Link(int from, int to)
    {
        _adj[from][to] = true;
        if (!_adj[to].ContainsKey(from)) _adj[to][from] = false;
    }

    /// <summary>The node nearest <paramref name="p"/> within <paramref name="radius"/> (Chebyshev), or -1.</summary>
    public int Nearest(TilePoint p, int radius, out double distance)
    {
        int best = -1;
        distance = double.MaxValue;
        int r = radius / Spacing + 1;
        var (cx, cy) = Cell(p);
        for (int gy = cy - r; gy <= cy + r; gy++)
            for (int gx = cx - r; gx <= cx + r; gx++)
            {
                if (!_cells.TryGetValue((gx, gy), out var list)) continue;
                foreach (int i in list)
                {
                    if (Chebyshev(_nodes[i], p) > radius) continue;
                    double d = Euclid(_nodes[i], p);
                    if (d < distance) { distance = d; best = i; }
                }
            }
        return best;
    }

    /// <summary>Nodes within <paramref name="radius"/> (Chebyshev) of <paramref name="p"/>.</summary>
    public List<int> Within(TilePoint p, int radius)
    {
        var found = new List<int>();
        int r = radius / Spacing + 1;
        var (cx, cy) = Cell(p);
        for (int gy = cy - r; gy <= cy + r; gy++)
            for (int gx = cx - r; gx <= cx + r; gx++)
                if (_cells.TryGetValue((gx, gy), out var list))
                    foreach (int i in list)
                        if (Chebyshev(_nodes[i], p) <= radius) found.Add(i);
        return found;
    }

    /// <summary>Cost of walking the edge from → to (see the class summary).</summary>
    public double EdgeCost(int from, int to)
    {
        var a = _nodes[from];
        var b = _nodes[to];
        double c = Euclid(a, b);
        if (!_surface[from] || !_surface[to]) c *= UndergroundFactor;
        if (!Walked(from, to))
        {
            c *= ReverseFactor;
            int climb = a.Y - b.Y;
            if (climb > 0) c += climb * ReverseClimbCost;
        }
        return c;
    }

    /// <summary>How far from the player the first trail node may be (it has to be planned to directly).</summary>
    public const int StartRadius = 24;
    /// <summary>Cost factor for the unwalked leg from the player to the first node.</summary>
    public const double StartLegFactor = 2;

    /// <summary>
    /// The cheapest walk over the trail from a node near <paramref name="from"/> (within <see cref="StartRadius"/>) to the
    /// reachable node nearest <paramref name="goal"/> (ties: the cheaper one), as node positions in walking order
    /// (the first is the start node). Null when no node is near the start, or the trail gets no closer to the goal than
    /// <paramref name="minGain"/> tiles less than the player already is.
    /// </summary>
    public TrailRoute? Route(TilePoint from, TilePoint goal, double minGain = Spacing)
    {
        var starts = Within(from, StartRadius);
        if (starts.Count == 0) return null;
        int n = _nodes.Count;
        var dist = new double[n];
        var prev = new int[n];
        Array.Fill(dist, double.PositiveInfinity);
        Array.Fill(prev, -1);
        var open = new PriorityQueue<int, double>();
        foreach (int s in starts)
        {
            double c = Euclid(from, _nodes[s]) * StartLegFactor;
            if (c < dist[s]) { dist[s] = c; open.Enqueue(s, c); }
        }
        while (open.TryDequeue(out int u, out double du))
        {
            if (du > dist[u]) continue;
            foreach (int v in _adj[u].Keys)
            {
                double dv = du + EdgeCost(u, v);
                if (dv < dist[v])
                {
                    dist[v] = dv;
                    prev[v] = u;
                    open.Enqueue(v, dv);
                }
            }
        }
        int best = -1;
        double bestGoal = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            if (double.IsPositiveInfinity(dist[i])) continue;
            double g = Euclid(_nodes[i], goal);
            if (g < bestGoal - 0.5 || (Math.Abs(g - bestGoal) <= 0.5 && dist[i] < dist[best]))
            {
                best = i;
                bestGoal = g;
            }
        }
        if (best < 0 || bestGoal > Euclid(from, goal) - minGain) return null;
        var path = new List<TilePoint>();
        for (int i = best; i >= 0; i = prev[i]) path.Add(_nodes[i]);
        path.Reverse();
        return new TrailRoute(path, dist[best], bestGoal);
    }

    private static (int, int) Cell(TilePoint p) => (FloorDiv(p.X, Spacing), FloorDiv(p.Y, Spacing));
    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;
    internal static int Chebyshev(TilePoint a, TilePoint b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    internal static double Euclid(TilePoint a, TilePoint b) =>
        Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));
}

/// <summary>A walk over the trail: node positions in order, its cost, and how far its end is from the goal.</summary>
public sealed record TrailRoute(IReadOnlyList<TilePoint> Waypoints, double Cost, double EndToGoal);
