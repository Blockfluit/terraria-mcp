using System.Buffers;
using System.Runtime.InteropServices;
using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Nav;

/// <summary>
/// A* over positions of the 2x3 player body. A position (x,y) is the feet-left tile; the body
/// covers x..x+1, y-2..y and support comes from (x,y+1) or (x+1,y+1).
///
/// Step semantics (what a follower should do to execute each step):
/// <list type="bullet">
/// <item>Walk: move horizontally one tile onto supported ground.</item>
/// <item>StepUp: move one tile sideways and one up (Terraria auto-steps 1-tile ledges).</item>
/// <item>Jump: from the previous position jump and steer to Pos. Clearance was checked for a
/// conservative "rise, then across, then down" box path, so rising first is always safe.</item>
/// <item>Fall: walk sideways off an edge (or, at a mid-air start, just fall) and land at Pos.</item>
/// <item>DropThrough: hold down on the platform under the feet and land at Pos.</item>
/// <item>Swim: Pos is adjacent (8-neighbourhood) to the previous position; the body is in or
/// leaving water/honey.</item>
/// <item>Dig: stand still at Pos (= the previous position) and mine WorkTile. A run of Dig steps
/// is always followed by the move whose body needs those tiles cleared.</item>
/// <item>BuildPlatform: place a platform at WorkTile, which ends up directly under Pos. Either
/// bridging (Pos is one tile sideways, WorkTile under it at the current feet+1 row) or climbing
/// (Pos is straight up 1..3 tiles: jump, place WorkTile below the feet near the apex, land).</item>
/// </list>
/// </summary>
public static class Pathfinder
{
    private const float WalkCost = 1f, StepUpCost = 1.5f, JumpBase = 2f, DropCost = 1.5f;
    private const float FallBase = 1f, FallPerTile = 0.2f, DigPerTile = 3f, BuildCost = 4f;
    private const float SwimCost = 2f, SwimDiagCost = 2.5f, UnknownPenalty = 2f;
    private const int MaxBuildUp = 3, GoalSearchRadius = 3, MaxBuildsTracked = 511;
    // Jump evaluation works on 64-row column bitmasks: 2*MaxJumpUp + 5 rows must fit.
    private const int MaxJumpUpModelled = 29, MaxJumpGapModelled = 32;

    public static bool CanStand(ITileGrid grid, TilePoint p)
    {
        for (int dy = 0; dy < 3; dy++)
            for (int dx = 0; dx < 2; dx++)
                if (grid.Get(p.X + dx, p.Y - dy).BlocksBody) return false;
        return grid.Get(p.X, p.Y + 1).IsStandable || grid.Get(p.X + 1, p.Y + 1).IsStandable;
    }

    public static PathResult FindPath(ITileGrid grid, TilePoint start, TilePoint goal,
        MovementProfile profile, PathOptions options)
    {
        if (start == goal) return new PathResult(true, Array.Empty<PathStep>(), null, 0);
        var s = new Search(grid, start, goal, profile, options);
        try { return s.Run(); }
        finally { s.Dispose(); }
    }

    private struct Node
    {
        public int X, Y, Parent, Builds, DigStart, DigCount, WX, WY;
        public float G;
        public bool Built, Closed, HasWork;
        public MoveKind Kind;
    }

    private sealed class Search : IDisposable
    {
        private readonly TileCache _t;
        private readonly TilePoint _start, _requestedGoal;
        private TilePoint _goal;
        private readonly MovementProfile _p;
        private readonly PathOptions _o;
        private readonly bool _canDig, _canBuild;
        private readonly int _buildCap, _maxFallScan;

        private Node[] _nodes = new Node[1024];
        private int _count;
        private readonly int[] _flatIndex;   // window-indexed node index + 1
        private readonly int[] _fallMemo;    // window-indexed fall result (see FallScan)
        private readonly Dictionary<long, int> _dict = new(); // states outside the window
        private readonly PriorityQueue<int, float> _open = new();
        private readonly List<TilePoint> _digPool = new();
        private readonly List<TilePoint> _digs = new();

        // Body rectangle of the node being expanded: its tiles are known to be clear (it may
        // have been dug out by this very path).
        private int _sx = int.MinValue / 2, _sy = int.MinValue / 2;

        public Search(ITileGrid grid, TilePoint start, TilePoint goal, MovementProfile p, PathOptions o)
        {
            _canDig = o.AllowDig && p.PickPower > 0;
            _canBuild = o.AllowBuild && p.BuildItemCount > 0;
            _buildCap = Math.Min(p.BuildItemCount, MaxBuildsTracked);
            _t = new TileCache(grid, start, goal, _canDig ? p.PickPower : 0);
            _flatIndex = ArrayPool<int>.Shared.Rent(_t.Size);
            Array.Clear(_flatIndex, 0, _t.Size);
            _fallMemo = ArrayPool<int>.Shared.Rent(_t.Size);
            Array.Clear(_fallMemo, 0, _t.Size);
            _start = start;
            _requestedGoal = goal;
            _p = p;
            _o = o;
            _maxFallScan = p.NoFallDamage ? 1024 : p.SafeFallTiles + 64;
        }

        public void Dispose()
        {
            ArrayPool<int>.Shared.Return(_flatIndex);
            ArrayPool<int>.Shared.Return(_fallMemo);
            _t.Dispose();
        }

        public PathResult Run()
        {
            if (!ResolveGoal(out _goal))
                return new PathResult(false, Array.Empty<PathStep>(),
                    $"goal_not_standable: no standable position within {GoalSearchRadius} tiles of {_requestedGoal}", 0);

            BodyHas(_start.X, _start.Y, 0xFF); // classify the start body (seeds TileCache.Seen)
            _count = 1;
            _nodes[0] = new Node { X = _start.X, Y = _start.Y, Parent = -1 };
            Slot(_start.X, _start.Y) = 1;
            _open.Enqueue(0, H(_start.X, _start.Y));

            int expanded = 0, fallback = -1, fallbackDeadline = 0;
            while (_open.TryDequeue(out int ni, out _))
            {
                ref var n = ref _nodes[ni];
                if (n.Closed) continue;
                n.Closed = true;

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

        /// <summary>Keep the goal if the body can be there; otherwise the nearest such cell within 3.</summary>
        private bool ResolveGoal(out TilePoint goal)
        {
            goal = _requestedGoal;
            if (GoalOk(goal.X, goal.Y)) return true;
            int best = int.MaxValue;
            for (int dy = -GoalSearchRadius; dy <= GoalSearchRadius; dy++)
                for (int dx = -GoalSearchRadius; dx <= GoalSearchRadius; dx++)
                {
                    int d = dx * dx + dy * dy;
                    if (d >= best || !GoalOk(_requestedGoal.X + dx, _requestedGoal.Y + dy)) continue;
                    best = d;
                    goal = new TilePoint(_requestedGoal.X + dx, _requestedGoal.Y + dy);
                }
            return best != int.MaxValue;
        }

        private bool GoalOk(int x, int y) => BodyClear(x, y) && (HasSupport(x, y) || BodyHas(x, y, TileCache.Liquid));

        private PathResult Success(int ni, int expanded)
        {
            var chain = new List<int>();
            for (int i = ni; _nodes[i].Parent >= 0; i = _nodes[i].Parent) chain.Add(i);
            chain.Reverse();
            var steps = new List<PathStep>(chain.Count);
            foreach (int i in chain)
            {
                ref var n = ref _nodes[i];
                ref var parent = ref _nodes[n.Parent];
                var from = new TilePoint(parent.X, parent.Y);
                for (int d = 0; d < n.DigCount; d++)
                    steps.Add(new PathStep(from, MoveKind.Dig, _digPool[n.DigStart + d]));
                steps.Add(new PathStep(new TilePoint(n.X, n.Y), n.Kind,
                    n.HasWork ? new TilePoint(n.WX, n.WY) : null));
            }
            return new PathResult(true, steps, null, expanded);
        }

        private int Chebyshev(int x, int y) => Math.Max(Math.Abs(x - _goal.X), Math.Abs(y - _goal.Y));
        private float H(int x, int y) => Math.Abs(x - _goal.X) + Math.Abs(y - _goal.Y);

        /// <summary>
        /// States are keyed by position only. Builds used are carried in the node (and capped when
        /// generating edges) but are not part of the key: keying on them multiplies the state
        /// space by the build cap and made budget-exhausting searches ~4x slower. "Built" needs no
        /// key either: an unsupported, dry position is only ever reachable by building under it.
        /// Trade-off: a cheaper route using more platforms can shadow a dearer one using fewer.
        /// </summary>
        private ref int Slot(int x, int y)
        {
            if (_t.TryIndex(x, y, out int i)) return ref _flatIndex[i];
            long key = ((long)x << 32) | (uint)y;
            return ref CollectionsMarshal.GetValueRefOrAddDefault(_dict, key, out _);
        }

        private void Add(int parent, int x, int y, int builds, bool built, float cost, MoveKind kind,
            bool withDigs = false, int wx = 0, int wy = 0, bool hasWork = false)
        {
            float g = _nodes[parent].G + cost;
            if (BodyHas(x, y, TileCache.Unknown)) g += UnknownPenalty;
            ref int slot = ref Slot(x, y);
            int idx = slot - 1;
            if (idx >= 0)
            {
                ref var old = ref _nodes[idx];
                if (old.Closed || g >= old.G) return;
            }
            else
            {
                idx = _count++;
                if (idx == _nodes.Length) Array.Resize(ref _nodes, _nodes.Length * 2);
                slot = idx + 1;
            }
            int digStart = 0, digCount = 0;
            if (withDigs && _digs.Count > 0)
            {
                digStart = _digPool.Count;
                digCount = _digs.Count;
                _digPool.AddRange(_digs);
            }
            _nodes[idx] = new Node
            {
                X = x, Y = y, G = g, Parent = parent, Builds = builds, Built = built, Kind = kind,
                DigStart = digStart, DigCount = digCount, HasWork = hasWork, WX = wx, WY = wy,
            };
            _open.Enqueue(idx, g + H(x, y));
        }

        // ---- tile predicates -------------------------------------------------------------

        private bool InSource(int x, int y) => x >= _sx && x <= _sx + 1 && y >= _sy - 2 && y <= _sy;

        /// <summary>Body may occupy this tile (not solid, not lava/hazard).</summary>
        private bool Passable(int x, int y) =>
            InSource(x, y) || (_t.F(x, y) & (TileCache.Block | TileCache.Deadly)) == 0;

        private bool HasSupport(int x, int y) => ((_t.F(x, y + 1) | _t.F(x + 1, y + 1)) & TileCache.Stand) != 0;

        private bool BodyClear(int x, int y)
        {
            for (int r = y - 2; r <= y; r++)
                if (!Passable(x, r) || !Passable(x + 1, r)) return false;
            return true;
        }

        private bool BodyHas(int x, int y, byte flag)
        {
            // Rare kinds (liquid, unknown) can't be in a body if the search never classified one.
            // Every move classifies its target body before asking, so this is exact in practice.
            if ((_t.Seen & flag) == 0) return false;
            int f = 0;
            for (int r = y - 2; r <= y; r++) f |= _t.F(x, r) | _t.F(x + 1, r);
            return (f & flag) != 0;
        }

        /// <summary>1 = must dig (recorded in _digs), 0 = clear, -1 = impassable.</summary>
        private int Classify(int x, int y)
        {
            if (InSource(x, y)) return 0;
            byte f = _t.F(x, y);
            if ((f & TileCache.Deadly) != 0) return -1;
            if ((f & TileCache.Block) == 0) return 0;
            if (!_canDig || (f & TileCache.Diggable) == 0) return -1;
            var p = new TilePoint(x, y);
            if (!_digs.Contains(p)) _digs.Add(p);
            return 1;
        }

        // ---- expansion -------------------------------------------------------------------

        private void Expand(int ni)
        {
            ref var n = ref _nodes[ni];
            int x = n.X, y = n.Y, builds = n.Builds;
            bool built = n.Built;
            _sx = x;
            _sy = y;

            if (BodyHas(x, y, TileCache.Liquid))
            {
                Swim(ni, x, y, builds);
                Jumps(ni, x, y, builds);
                return;
            }

            if (!built && !HasSupport(x, y))
            {
                // Mid-air (e.g. the start while jumping): the only thing that can happen is falling.
                if (FallScan(x, y, out int ly, out bool lq) && (lq || FallOk(ly - y)))
                    Add(ni, x, ly, builds, false, FallCost(ly - y), MoveKind.Fall);
                return;
            }

            Horizontal(ni, x, y, builds, -1);
            Horizontal(ni, x, y, builds, +1);
            StepUp(ni, x, y, builds, -1);
            StepUp(ni, x, y, builds, +1);
            Jumps(ni, x, y, builds);
            if (!built) DropThrough(ni, x, y, builds);
            if (!built) DigDown(ni, x, y, builds);
            BuildUp(ni, x, y, builds);
        }

        private void Horizontal(int ni, int x, int y, int builds, int dx)
        {
            int tx = x + dx;
            int col = dx > 0 ? x + 2 : x - 1;
            _digs.Clear();
            for (int r = y - 2; r <= y; r++)
                if (Classify(col, r) < 0) return;

            if (BodyHas(tx, y, TileCache.Liquid))
            {
                if (_digs.Count == 0) Add(ni, tx, y, builds, false, SwimCost, MoveKind.Swim);
                return;
            }
            if (HasSupport(tx, y))
            {
                Add(ni, tx, y, builds, false, WalkCost + DigPerTile * _digs.Count, MoveKind.Walk, withDigs: true);
                return;
            }
            if (_digs.Count > 0) return;

            if (FallScan(tx, y, out int ly, out bool lq) && (lq || FallOk(ly - y)))
                Add(ni, tx, ly, builds, false, FallCost(ly - y), MoveKind.Fall);

            // Bridge: place a platform under the target's inner column, next to our support.
            if (_canBuild && builds < _buildCap)
            {
                int wx = dx > 0 ? x + 1 : x, wy = y + 1;
                if (_t.F(wx, wy) == TileCache.Computed)
                    Add(ni, tx, y, builds + 1, true, BuildCost, MoveKind.BuildPlatform, wx: wx, wy: wy, hasWork: true);
            }
        }

        private void StepUp(int ni, int x, int y, int builds, int dx)
        {
            int tx = x + dx, ty = y - 1;
            _digs.Clear();
            // Head room above the current body, then the target body.
            if (Classify(x, y - 3) < 0 || Classify(x + 1, y - 3) < 0) return;
            for (int r = ty - 2; r <= ty; r++)
                if (Classify(tx, r) < 0 || Classify(tx + 1, r) < 0) return;
            if (BodyHas(tx, ty, TileCache.Liquid) || !HasSupport(tx, ty)) return;
            Add(ni, tx, ty, builds, false, StepUpCost + DigPerTile * _digs.Count, MoveKind.StepUp, withDigs: true);
        }

        /// <summary>
        /// Jump targets within MaxJumpUp vertically and MaxJumpGap horizontally (and, across a gap
        /// of at least 2, down to MaxJumpUp lower). Clearance is the conservative box path: straight
        /// up from the source to one above the higher feet row, across, then straight down.
        /// </summary>
        private void Jumps(int ni, int x, int y, int builds)
        {
            int maxUp = Math.Clamp(_p.MaxJumpUp, 0, MaxJumpUpModelled);
            int maxGap = Math.Clamp(_p.MaxJumpGap, 0, MaxJumpGapModelled);
            int levels = maxUp + 1;

            // Cheap early out (the common case underground): no head room at all.
            if (!Passable(x, y - 3) || !Passable(x + 1, y - 3)) return;

            // Column bitmasks over rows top..top+63 (bit i = row top+i), for every column a jump
            // from here can touch. Rows needed: y-maxUp-3 (apex head) .. y+maxUp+1 (lowest support).
            int top = y - maxUp - 3;
            int c0 = x - maxGap - 1, nCols = 2 * maxGap + 4;
            Span<ulong> pass = stackalloc ulong[nCols], stand = stackalloc ulong[nCols],
                desc = stackalloc ulong[nCols], liq = stackalloc ulong[nCols];
            for (int i = 0; i < nCols; i++) _t.Column(c0 + i, top, out pass[i], out stand[i], out desc[i], out liq[i]);
            ulong src = Bits(y - 2, y, top); // the source body is clear (this path may have dug it)
            pass[x - c0] |= src;
            pass[x + 1 - c0] |= src;

            // rise = how many tiles the body can move straight up.
            int rise = 0;
            ulong srcCols = pass[x - c0] & pass[x + 1 - c0];
            while (rise < levels && ((srcCols >> (y - 3 - rise - top)) & 1) != 0) rise++;
            if (rise == 0) return;

            Span<int> across = stackalloc int[levels + 1];
            ulong bodyAtY = Bits(y - 2, y, top), supportRow = 1UL << (y + 1 - top);
            for (int dir = -1; dir <= 1; dir += 2)
            {
                // across[a] = how far the body can move horizontally with feet at row y-a.
                for (int a = 1; a <= rise; a++)
                {
                    ulong body = Bits(y - a - 2, y - a, top);
                    int k = 0;
                    while (k < maxGap)
                    {
                        int col = dir > 0 ? x + 2 + k : x - 1 - k;
                        if ((pass[col - c0] & body) != body) break;
                        k++;
                    }
                    across[a] = k;
                }

                // Same-level jumps that plain walking also reaches are never better; skip them.
                int walkReach = 0;
                while (walkReach < maxGap)
                {
                    int k = walkReach + 1, col = dir > 0 ? x + 1 + k : x - k, tx = x + dir * k;
                    if ((pass[col - c0] & bodyAtY) != bodyAtY) break;
                    if (((stand[tx - c0] | stand[tx + 1 - c0]) & supportRow) == 0) break;
                    walkReach = k;
                }

                for (int k = dir > 0 ? 0 : 1; k <= maxGap; k++)
                {
                    int tx = x + dir * k, i0 = tx - c0;
                    ulong pp = pass[i0] & pass[i0 + 1], dd = desc[i0] & desc[i0 + 1];
                    ulong ss = stand[i0] | stand[i0 + 1], ll = liq[i0] | liq[i0 + 1];
                    for (int ty = y - maxUp; ty <= y + maxUp; ty++)
                    {
                        if (ty >= y && k < 2) continue; // walking/falling covers these
                        if (ty == y && k <= walkReach) continue;
                        int a = y - Math.Min(y, ty) + 1;
                        if (a > rise || across[a] < k) continue;
                        if (ty >= y)
                        {
                            // Straight down from row y-1 to ty; platforms would catch the body.
                            ulong need = Bits(y, ty, top);
                            if ((dd & need) != need) break; // deeper targets are blocked too
                        }
                        else if (((pp >> (ty - top)) & 1) == 0) continue; // rows above ty: across[a]
                        if (((ss >> (ty + 1 - top)) & 1) == 0) continue;
                        if ((ll & Bits(ty - 2, ty, top)) != 0) continue;
                        // 2 + |dx|: a jump never undercuts walking the same distance.
                        Add(ni, tx, ty, builds, false, JumpBase + k, MoveKind.Jump);
                    }
                }
            }
        }

        /// <summary>Mask of rows lo..hi (inclusive) relative to `top`.</summary>
        private static ulong Bits(int lo, int hi, int top) => ((1UL << (hi - lo + 1)) - 1) << (lo - top);

        private void DropThrough(int ni, int x, int y, int builds)
        {
            byte a = _t.F(x, y + 1), b = _t.F(x + 1, y + 1);
            if (((a | b) & TileCache.Block) != 0) return;
            if (((a | b) & TileCache.Plat) == 0) return;
            if (!Passable(x, y + 1) || !Passable(x + 1, y + 1)) return;
            if (!FallScan(x, y + 1, out int ly, out bool lq)) return;
            int d = ly - y;
            if (!lq && !FallOk(d)) return;
            Add(ni, x, ly, builds, false, DropCost + FallPerTile * (d - 1), MoveKind.DropThrough);
        }

        /// <summary>Mine the support tiles and drop one tile onto the next support down.</summary>
        private void DigDown(int ni, int x, int y, int builds)
        {
            if (!_canDig) return;
            _digs.Clear();
            if (Classify(x, y + 1) < 0 || Classify(x + 1, y + 1) < 0 || _digs.Count == 0) return;
            if (!HasSupport(x, y + 1) || BodyHas(x, y + 1, TileCache.Liquid)) return;
            Add(ni, x, y + 1, builds, false, FallCost(1) + DigPerTile * _digs.Count, MoveKind.Fall, withDigs: true);
        }

        /// <summary>Climb straight up by jumping and placing a platform under the feet.</summary>
        private void BuildUp(int ni, int x, int y, int builds)
        {
            if (!_canBuild || builds >= _buildCap) return;
            int maxK = Math.Min(MaxBuildUp, _p.MaxJumpUp);
            for (int k = 1; k <= maxK; k++)
            {
                int r = y - 2 - k; // new head row
                if (!Passable(x, r) || !Passable(x + 1, r)) return;
                int ty = y - k, wy = ty + 1;
                if (_t.F(x, wy) != TileCache.Computed) continue; // must place into plain air
                if (HasSupport(x, ty) || BodyHas(x, ty, TileCache.Liquid)) continue;
                Add(ni, x, ty, builds + 1, true, BuildCost, MoveKind.BuildPlatform, wx: x, wy: wy, hasWork: true);
            }
        }

        private void Swim(int ni, int x, int y, int builds)
        {
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int tx = x + dx, ty = y + dy;
                    if (!BodyClear(tx, ty)) continue;
                    if (!BodyHas(tx, ty, TileCache.Liquid) && !HasSupport(tx, ty)) continue;
                    Add(ni, tx, ty, builds, false, dx != 0 && dy != 0 ? SwimDiagCost : SwimCost, MoveKind.Swim);
                }
        }

        /// <summary>From an unsupported, clear body position, find where the player lands.</summary>
        private bool FallScan(int x, int y, out int landY, out bool liquid)
        {
            landY = y;
            liquid = false;
            if (BodyHas(x, y, TileCache.Liquid)) { liquid = true; return true; }

            // Memoised per start cell: the result only depends on the tiles below it.
            int memoIdx = -1;
            if (_t.TryIndex(x, y, out int wi) && !InSource(x, y))
            {
                memoIdx = wi;
                int m = _fallMemo[wi];
                if (m != 0)
                {
                    if (m == FallFails) return false;
                    liquid = m < 0;
                    landY = y + Math.Abs(m) - 1;
                    return true;
                }
            }

            bool ok = false;
            for (int yy = y; yy - y <= _maxFallScan; yy++)
            {
                // The row under the feet is both the support row and the next feet row.
                int f = _t.F(x, yy + 1) | _t.F(x + 1, yy + 1);
                if ((f & TileCache.Stand) != 0) { landY = yy; ok = true; break; }
                if ((f & TileCache.Deadly) != 0) break;
                if ((f & TileCache.Liquid) != 0) { landY = yy + 1; liquid = true; ok = true; break; }
            }
            if (memoIdx >= 0)
                _fallMemo[memoIdx] = !ok ? FallFails : (liquid ? -(landY - y + 1) : landY - y + 1);
            return ok;
        }

        private const int FallFails = int.MinValue;

        private bool FallOk(int d) => _p.NoFallDamage || d <= _p.SafeFallTiles;
        private static float FallCost(int d) => FallBase + FallPerTile * d;
    }

    /// <summary>
    /// Per-search cache of classified tiles over a window around start/goal, so the hot loops
    /// don't repeatedly hit the (possibly slow) game-backed grid. Outside the window it reads through.
    /// </summary>
    private sealed class TileCache : IDisposable
    {
        public const byte Computed = 1, Block = 2, Stand = 4, Plat = 8, Liquid = 16, Deadly = 32, Unknown = 64, Diggable = 128;
        private const int Margin = 128, MaxDim = 1024;

        private readonly ITileGrid _g;
        private readonly int _x0, _y0, _w, _h, _pick;
        private readonly byte[] _f;

        /// <summary>OR of the flags of every tile classified so far.</summary>
        public int Seen;

        public TileCache(ITileGrid g, TilePoint start, TilePoint goal, int pickPower)
        {
            _g = g;
            _pick = pickPower;
            _w = Math.Min(MaxDim, Math.Abs(goal.X - start.X) + 2 * Margin);
            _h = Math.Min(MaxDim, Math.Abs(goal.Y - start.Y) + 2 * Margin);
            int cx = (int)(((long)start.X + goal.X) / 2), cy = (int)(((long)start.Y + goal.Y) / 2);
            if (_w == MaxDim) cx = start.X;
            if (_h == MaxDim) cy = start.Y;
            _x0 = cx - _w / 2;
            _y0 = cy - _h / 2;
            _f = ArrayPool<byte>.Shared.Rent(Size);
            Array.Clear(_f, 0, Size);
            _blocks = (_h + 63) / 64;
            _cols = ArrayPool<ColMask>.Shared.Rent(_w * _blocks);
            Array.Clear(_cols, 0, _w * _blocks);
        }

        public int Size => _w * _h;

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_f);
            ArrayPool<ColMask>.Shared.Return(_cols);
        }

        /// <summary>Per-column bitmasks for 64 rows (bit i = row y0+i).</summary>
        private struct ColMask
        {
            public ulong Pass, Stand, Desc, Liq;
            public bool Done;
        }

        private readonly int _blocks;
        private readonly ColMask[] _cols;

        /// <summary>
        /// Bitmasks for rows top..top+63 of column x: Pass = body may occupy (not solid/lava/hazard),
        /// Stand = solid or platform, Desc = a descending body passes (Pass and not a platform),
        /// Liq = water/honey.
        /// </summary>
        public void Column(int x, int top, out ulong pass, out ulong stand, out ulong desc, out ulong liq)
        {
            int lx = x - _x0, rel = top - _y0;
            if ((uint)lx >= (uint)_w || rel < 0 || (rel >> 6) + 1 >= _blocks)
            {
                var m = default(ColMask);
                Fill(ref m, x, top);
                (pass, stand, desc, liq) = (m.Pass, m.Stand, m.Desc, m.Liq);
                return;
            }
            int b = rel >> 6, off = rel & 63;
            ref var m0 = ref MaskBlock(lx, b);
            if (off == 0)
            {
                (pass, stand, desc, liq) = (m0.Pass, m0.Stand, m0.Desc, m0.Liq);
                return;
            }
            ref var m1 = ref MaskBlock(lx, b + 1);
            int back = 64 - off;
            pass = (m0.Pass >> off) | (m1.Pass << back);
            stand = (m0.Stand >> off) | (m1.Stand << back);
            desc = (m0.Desc >> off) | (m1.Desc << back);
            liq = (m0.Liq >> off) | (m1.Liq << back);
        }

        private ref ColMask MaskBlock(int lx, int b)
        {
            ref var m = ref _cols[lx * _blocks + b];
            if (!m.Done) Fill(ref m, _x0 + lx, _y0 + b * 64);
            return ref m;
        }

        private void Fill(ref ColMask m, int x, int y0)
        {
            ulong pass = 0, stand = 0, desc = 0, liq = 0;
            for (int i = 0; i < 64; i++)
            {
                int f = F(x, y0 + i);
                ulong bit = 1UL << i;
                if ((f & (Block | Deadly)) == 0) pass |= bit;
                if ((f & Stand) != 0) stand |= bit;
                if ((f & (Stand | Deadly)) == 0) desc |= bit;
                if ((f & Liquid) != 0) liq |= bit;
            }
            m = new ColMask { Pass = pass, Stand = stand, Desc = desc, Liq = liq, Done = true };
        }

        public bool TryIndex(int x, int y, out int i)
        {
            uint lx = (uint)(x - _x0), ly = (uint)(y - _y0);
            if (lx >= (uint)_w || ly >= (uint)_h) { i = 0; return false; }
            i = (int)ly * _w + (int)lx;
            return true;
        }

        public byte F(int x, int y)
        {
            if (!TryIndex(x, y, out int i)) return Classify(_g.Get(x, y));
            byte b = _f[i];
            if (b == 0) _f[i] = b = Classify(_g.Get(x, y));
            return b;
        }

        private byte Classify(TileInfo t)
        {
            int b = Computed;
            switch (t.Kind)
            {
                case TileKind.Solid:
                    b |= Block | Stand;
                    if (_pick > 0 && !t.Protected && t.PickPowerRequired <= _pick) b |= Diggable;
                    break;
                case TileKind.Platform: b |= Stand | Plat; break;
                case TileKind.Water:
                case TileKind.Honey: b |= Liquid; break;
                case TileKind.Lava:
                case TileKind.Hazard: b |= Deadly; break;
                case TileKind.Unknown: b |= Unknown; break;
            }
            Seen |= b;
            return (byte)b;
        }
    }
}
