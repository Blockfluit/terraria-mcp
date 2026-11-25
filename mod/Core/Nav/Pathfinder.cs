using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Nav;

/// <summary>
/// A* over standing positions of the 2x3 player body. A position (x,y) is the feet-left tile;
/// the body covers x..x+1, y-2..y and support comes from (x,y+1) or (x+1,y+1).
/// </summary>
public static class Pathfinder
{
    public static bool CanStand(ITileGrid grid, TilePoint p)
    {
        for (int dy = 0; dy < 3; dy++)
            for (int dx = 0; dx < 2; dx++)
                if (grid.Get(p.X + dx, p.Y - dy).BlocksBody) return false;
        return grid.Get(p.X, p.Y + 1).IsStandable || grid.Get(p.X + 1, p.Y + 1).IsStandable;
    }

    public static PathResult FindPath(ITileGrid grid, TilePoint start, TilePoint goal,
        MovementProfile profile, PathOptions options)
        => new Search(grid, start, goal, profile, options).Run();

    private struct Node
    {
        public int X, Y;
        public float G;
        public int Parent;
        public MoveKind Kind;
        public TilePoint? Work;
        public bool Closed;
    }

    private sealed class Search
    {
        private readonly TileCache _t;
        private readonly TilePoint _start, _goal;
        private readonly MovementProfile _p;
        private readonly PathOptions _o;
        private readonly List<Node> _nodes = new();
        private readonly Dictionary<long, int> _index = new();
        private readonly PriorityQueue<int, float> _open = new();
        private readonly int _maxFallScan;

        // Source body rectangle of the node being expanded: its tiles are known to be clear.
        private int _sx, _sy;

        public Search(ITileGrid grid, TilePoint start, TilePoint goal, MovementProfile p, PathOptions o)
        {
            _t = new TileCache(grid, start, goal);
            _start = start;
            _goal = goal;
            _p = p;
            _o = o;
            _maxFallScan = p.NoFallDamage ? 1024 : p.SafeFallTiles + 64;
        }

        public PathResult Run()
        {
            if (_start == _goal) return new PathResult(true, Array.Empty<PathStep>(), null, 0);

            _nodes.Add(new Node { X = _start.X, Y = _start.Y, Parent = -1 });
            _index[Key(_start.X, _start.Y)] = 0;
            _open.Enqueue(0, H(_start.X, _start.Y));

            int expanded = 0, fallback = -1, fallbackDeadline = 0;
            while (_open.TryDequeue(out int ni, out _))
            {
                if (_nodes[ni].Closed) continue;
                var n = _nodes[ni];
                n.Closed = true;
                _nodes[ni] = n;

                if (n.X == _goal.X && n.Y == _goal.Y) return Success(ni, expanded);
                if (fallback < 0 && Chebyshev(n.X, n.Y) <= _o.GoalTolerance)
                {
                    // Prefer the exact goal, but don't spend much more effort looking for it.
                    fallback = ni;
                    fallbackDeadline = expanded + 256;
                }
                if (fallback >= 0 && expanded >= fallbackDeadline) break;
                if (expanded >= _o.MaxNodes) break;

                expanded++;
                Expand(ni);
            }
            if (fallback >= 0) return Success(fallback, expanded);
            return new PathResult(false, Array.Empty<PathStep>(), $"unreachable: searched {expanded} nodes", expanded);
        }

        private PathResult Success(int ni, int expanded)
        {
            var steps = new List<PathStep>();
            for (int i = ni; _nodes[i].Parent >= 0; i = _nodes[i].Parent)
            {
                var n = _nodes[i];
                steps.Add(new PathStep(new TilePoint(n.X, n.Y), n.Kind, n.Work));
            }
            steps.Reverse();
            return new PathResult(true, steps, null, expanded);
        }

        private int Chebyshev(int x, int y) => Math.Max(Math.Abs(x - _goal.X), Math.Abs(y - _goal.Y));
        private float H(int x, int y) => Math.Abs(x - _goal.X) + Math.Abs(y - _goal.Y);

        private static long Key(int x, int y) => ((long)x << 32) | (uint)y;

        private void Add(int parent, int x, int y, float cost, MoveKind kind, TilePoint? work = null)
        {
            float g = _nodes[parent].G + cost;
            long key = Key(x, y);
            var node = new Node { X = x, Y = y, G = g, Parent = parent, Kind = kind, Work = work };
            if (_index.TryGetValue(key, out int idx))
            {
                var old = _nodes[idx];
                if (old.Closed || g >= old.G) return;
                _nodes[idx] = node;
            }
            else
            {
                idx = _nodes.Count;
                _nodes.Add(node);
                _index[key] = idx;
            }
            _open.Enqueue(idx, g + H(x, y));
        }

        // ---- tile predicates -------------------------------------------------------------

        private bool InSource(int x, int y) => x >= _sx && x <= _sx + 1 && y >= _sy - 2 && y <= _sy;

        /// <summary>Body may occupy this tile (not solid, not lava/hazard).</summary>
        private bool Passable(int x, int y) => InSource(x, y) || (_t.F(x, y) & (TileCache.Block | TileCache.Deadly)) == 0;

        private bool Descendable(int x, int y) => InSource(x, y) || (_t.F(x, y) & (TileCache.Stand | TileCache.Deadly)) == 0;

        private bool HasSupport(int x, int y) =>
            ((_t.F(x, y + 1) | _t.F(x + 1, y + 1)) & TileCache.Stand) != 0;

        private bool BodyClear(int x, int y)
        {
            for (int r = y - 2; r <= y; r++)
                if (!Passable(x, r) || !Passable(x + 1, r)) return false;
            return true;
        }

        // ---- expansion -------------------------------------------------------------------

        private void Expand(int ni)
        {
            var n = _nodes[ni];
            int x = n.X, y = n.Y;
            _sx = x;
            _sy = y;

            if (!HasSupport(x, y))
            {
                // Mid-air start: the only thing that can happen is falling.
                if (FallScan(x, y, out int ly) && FallOk(ly - y))
                    Add(ni, x, ly, FallCost(ly - y), MoveKind.Fall);
                return;
            }

            Horizontal(ni, x, y, -1);
            Horizontal(ni, x, y, +1);
            StepUp(ni, x, y, -1);
            StepUp(ni, x, y, +1);
            Jumps(ni, x, y);
            DropThrough(ni, x, y);
        }

        private void Horizontal(int ni, int x, int y, int dx)
        {
            int tx = x + dx;
            int col = dx > 0 ? x + 2 : x - 1;
            for (int r = y - 2; r <= y; r++)
                if (!Passable(col, r)) return;

            if (HasSupport(tx, y))
            {
                Add(ni, tx, y, 1f, MoveKind.Walk);
                return;
            }
            if (FallScan(tx, y, out int ly) && FallOk(ly - y))
                Add(ni, tx, ly, FallCost(ly - y), MoveKind.Fall);
        }

        private void StepUp(int ni, int x, int y, int dx)
        {
            int tx = x + dx, ty = y - 1;
            // Head room above the current body, then the target body.
            if (!Passable(x, y - 3) || !Passable(x + 1, y - 3)) return;
            if (!BodyClear(tx, ty) || !HasSupport(tx, ty)) return;
            Add(ni, tx, ty, 1.5f, MoveKind.StepUp);
        }

        /// <summary>
        /// Jump targets within MaxJumpUp vertically and MaxJumpGap horizontally. Clearance is the
        /// conservative box path: straight up from the source to one above the higher of the two
        /// feet rows, across at that height, then straight down to the target.
        /// </summary>
        private void Jumps(int ni, int x, int y)
        {
            int maxUp = Math.Max(0, _p.MaxJumpUp), maxGap = Math.Max(0, _p.MaxJumpGap);
            int levels = maxUp + 1;

            // rise = how many tiles the body can move straight up.
            int rise = 0;
            while (rise < levels && Passable(x, y - 3 - rise) && Passable(x + 1, y - 3 - rise)) rise++;
            if (rise == 0) return;

            Span<int> across = stackalloc int[levels + 1];
            for (int dir = -1; dir <= 1; dir += 2)
            {
                // across[a] = how far the body can move horizontally with feet at row y-a.
                for (int a = 1; a <= rise; a++)
                {
                    int h = y - a, k = 0;
                    while (k < maxGap)
                    {
                        int col = dir > 0 ? x + 2 + k : x - 1 - k;
                        if (!Passable(col, h) || !Passable(col, h - 1) || !Passable(col, h - 2)) break;
                        k++;
                    }
                    across[a] = k;
                }

                for (int k = dir > 0 ? 0 : 1; k <= maxGap; k++)
                {
                    int tx = x + dir * k;
                    int downOk = y - 1; // rows y..downOk at columns tx..tx+1 are known clear
                    for (int ty = y - maxUp; ty <= y + maxUp; ty++)
                    {
                        if (ty >= y && k < 2) continue; // walking/falling covers these
                        int a = y - Math.Min(y, ty) + 1;
                        if (a > rise || across[a] < k) continue;
                        if (ty >= y)
                        {
                            // Descending onto a platform row would land on it, so platforms stop the descent.
                            while (downOk < ty && Descendable(tx, downOk + 1) && Descendable(tx + 1, downOk + 1)) downOk++;
                            if (downOk < ty) break; // deeper targets are blocked too
                        }
                        else if (!Passable(tx, ty) || !Passable(tx + 1, ty)) continue;

                        if (!HasSupport(tx, ty)) continue;
                        // 2 + |dx|: a jump never undercuts walking the same distance.
                        Add(ni, tx, ty, 2f + k, MoveKind.Jump);
                    }
                }
            }
        }

        private void DropThrough(int ni, int x, int y)
        {
            byte a = _t.F(x, y + 1), b = _t.F(x + 1, y + 1);
            if (((a | b) & TileCache.Block) != 0) return;
            if (((a | b) & TileCache.Plat) == 0) return;
            if (!Passable(x, y + 1) || !Passable(x + 1, y + 1)) return;
            if (!FallScan(x, y + 1, out int ly)) return;
            int d = ly - y;
            if (!FallOk(d)) return;
            Add(ni, x, ly, 1.5f + 0.2f * (d - 1), MoveKind.DropThrough);
        }

        /// <summary>From an unsupported, clear body position, find where the player lands.</summary>
        private bool FallScan(int x, int y, out int landY)
        {
            landY = y;
            for (int yy = y; yy - y <= _maxFallScan; yy++)
            {
                if (HasSupport(x, yy)) { landY = yy; return true; }
                // Next feet row must be enterable (it isn't solid, else we'd have support).
                if (!Passable(x, yy + 1) || !Passable(x + 1, yy + 1)) return false;
            }
            return false;
        }

        private bool FallOk(int d) => _p.NoFallDamage || d <= _p.SafeFallTiles;
        private static float FallCost(int d) => 1f + 0.2f * d;
    }

    /// <summary>
    /// Per-search cache of classified tiles over a window around start/goal, so the hot loops
    /// don't repeatedly hit the (possibly slow) game-backed grid. Outside the window it reads through.
    /// </summary>
    private sealed class TileCache
    {
        public const byte Computed = 1, Block = 2, Stand = 4, Plat = 8, Liquid = 16, Deadly = 32, Unknown = 64, Diggable = 128;
        private const int Margin = 128, MaxDim = 1024;

        private readonly ITileGrid _g;
        private readonly int _x0, _y0, _w, _h;
        private readonly byte[] _f;

        public TileCache(ITileGrid g, TilePoint start, TilePoint goal)
        {
            _g = g;
            _w = Math.Min(MaxDim, Math.Abs(goal.X - start.X) + 2 * Margin);
            _h = Math.Min(MaxDim, Math.Abs(goal.Y - start.Y) + 2 * Margin);
            int cx = (start.X + goal.X) / 2, cy = (start.Y + goal.Y) / 2;
            if (_w == MaxDim) cx = start.X;
            if (_h == MaxDim) cy = start.Y;
            _x0 = cx - _w / 2;
            _y0 = cy - _h / 2;
            _f = new byte[_w * _h];
        }

        public byte F(int x, int y)
        {
            uint lx = (uint)(x - _x0), ly = (uint)(y - _y0);
            if (lx >= (uint)_w || ly >= (uint)_h) return Classify(_g.Get(x, y));
            int i = (int)ly * _w + (int)lx;
            byte b = _f[i];
            if (b == 0) _f[i] = b = Classify(_g.Get(x, y));
            return b;
        }

        private static byte Classify(TileInfo t)
        {
            byte b = Computed;
            switch (t.Kind)
            {
                case TileKind.Solid: b |= Block | Stand; break;
                case TileKind.Platform: b |= Stand | Plat; break;
                case TileKind.Water:
                case TileKind.Honey: b |= Liquid; break;
                case TileKind.Lava:
                case TileKind.Hazard: b |= Deadly; break;
                case TileKind.Unknown: b |= Unknown; break;
            }
            return b;
        }
    }
}
