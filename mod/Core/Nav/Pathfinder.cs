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
/// <item>Walk: move horizontally one tile onto supported ground; also one tile up a floor slope climbing that way or
/// onto a half block, or one tile down where a slope / half block keeps the feet on the surface (vanilla's slope
/// collision and StepDown: no jump, no fall).</item>
/// <item>StepUp: move one tile sideways and one up (Terraria auto-steps 1-tile ledges; onto a platform only while
/// holding Up, and not onto a platform with another one right above it).</item>
/// <item>Jump: from the previous position jump and steer to Pos. Clearance was checked for a
/// conservative "rise, then across, then down" box path, so rising first is always safe.</item>
/// <item>Fall: walk sideways off an edge (or, at a mid-air start, just fall) and land at Pos.</item>
/// <item>DropThrough: hold down on the platform under the feet and land at Pos.</item>
/// <item>Swim: Pos is adjacent (8-neighbourhood) to the previous position; the body is in or
/// leaving water/honey. Steps with the head in liquid (submerged: either cell of the head row) cost extra and a path
/// never has more than <see cref="MovementProfile.MaxSubmergedSteps"/> of them in a row (breath), unless
/// <see cref="MovementProfile.WaterBreathing"/>. Without <see cref="MovementProfile.CanSwim"/> (vanilla: no
/// swimming without Flippers; the player sinks and can only jump off the bottom) a body in liquid that nothing
/// holds is either rising in a water jump that started on ground (at most
/// <see cref="MovementProfile.MaxWaterJumpUp"/> tiles above it: up, diagonally up, or sideways at the top) or
/// sinking (down, diagonally down, or sideways onto ground). Entering liquid from above (walking off a shore, falling,
/// dropping) sinks. So a non-swimmer crosses water only along its bottom, and a lake deeper than breath and the water
/// jump allow is impassable except over a platform bridge (platforms may be placed into liquid).</item>
/// <item>Dig: stand still at Pos (= the previous position) and mine WorkTile. A run of Dig steps
/// is always followed by the move whose body needs those tiles cleared.</item>
/// <item>BuildPlatform: place a platform at WorkTile, which ends up directly under Pos. Either
/// bridging (Pos is one tile sideways, WorkTile under it at the current feet+1 row) or climbing
/// (Pos is straight up 1..3 tiles: jump, place WorkTile below the feet near the apex, land).</item>
/// <item>BuildDown: descend a drop too deep to fall safely (e.g. a chasm with walls the pickaxe can't dig). Standing at
/// the previous position, place a platform at WorkTile (1..2 rows under Pos's feet row + 1, i.e. at most 3 rows below
/// the current feet: vanilla placement reach), then either drop through the platform underfoot (Pos straight below:
/// hold Down) or step one tile sideways off the edge (Pos one column over), and land on it. Only generated where the
/// plain fall/drop from there is not safe (or finds no floor), so real routes and safe falls always win.</item>
/// <item>OpenDoor: stand at Pos (= the previous position) and open the closed door at WorkTile (any of its tiles);
/// always followed by the Walk into the door's column. Only plain walks pass doors (no digging, jumping or
/// falling through a door column).</item>
/// <item>CloseDoor: stand at Pos (= the previous position, the body clear of the door column) and close the door
/// at WorkTile that an earlier OpenDoor opened (<see cref="PathOptions.CloseDoorsBehind"/>). Best effort.</item>
/// </list>
/// </summary>
public static class Pathfinder
{
    private const float WalkCost = 1f, StepUpCost = 1.5f, JumpBase = 2f, DropCost = 1.5f;
    private const float FallBase = 1f, FallPerTile = 0.2f, DigPerTile = 3f, BuildCost = 4f;
    private const float SwimCost = 2f, SwimDiagCost = 2.5f, UnknownPenalty = 2f, DoorCost = 2f;
    /// <summary>Extra cost of a step that ends with the head in liquid (drowning risk; a dry detour of a few tiles
    /// is preferred).</summary>
    private const float SubmergedPenalty = 4f;
    /// <summary>A CloseDoor step is only added while the door is at most this many tiles from the body.</summary>
    private const int CloseDoorReach = 3;
    /// <summary>A goal the body can't occupy snaps to the nearest standable cell within this many tiles.</summary>
    public const int GoalSearchRadius = 3;
    private const int MaxBuildUp = 3, MaxBuildsTracked = 511;
    /// <summary>BuildDown lands at most this many rows lower: its platform then sits 3 rows below the source feet,
    /// the deepest vanilla's placement reach allows (tileRangeY 4: (bottom/16) + 4 - 2 rows below the feet).</summary>
    public const int MaxBuildDown = 2;
    // Jump evaluation works on 64-row column bitmasks: 2*MaxJumpUp + 5 rows must fit.
    private const int MaxJumpUpModelled = 29, MaxJumpGapModelled = 32;

    public static bool CanStand(ITileGrid grid, TilePoint p)
    {
        for (int dy = 0; dy < 3; dy++)
            for (int dx = 0; dx < 2; dx++)
                if (grid.Get(p.X + dx, p.Y - dy).BlocksBody) return false;
        return grid.Get(p.X, p.Y + 1).IsStandable || grid.Get(p.X + 1, p.Y + 1).IsStandable;
    }

    /// <summary>
    /// How many tiles the feet may rise above <paramref name="s"/> on a jump to <paramref name="t"/> without the arc
    /// ending up over a platform that lies above both feet rows (it would be risen through and then catch the
    /// player on the way down): the feet must stay strictly below that many tiles. int.MaxValue when there is none within <paramref name="maxLevels"/> + 3 rows.
    /// Same rule the search applies to jump edges; the follower uses it to release jump early.
    /// </summary>
    public static int JumpPlatformCeiling(ITileGrid grid, TilePoint s, TilePoint t, int maxLevels)
    {
        int x0 = Math.Min(s.X, t.X), x1 = Math.Max(s.X, t.X) + 1, lim = Math.Min(s.Y, t.Y);
        for (int r = lim - 1; r >= s.Y - maxLevels - 3; r--)
            for (int x = x0; x <= x1; x++)
                if (grid.Get(x, r).Kind == TileKind.Platform) return s.Y - r;
        return int.MaxValue;
    }

    /// <summary>
    /// The 2-tile-wide body model can put a start cell inside a block the real 1.25-tile hitbox doesn't touch (seen in
    /// game: standing left of a 1-tile sand step, the feet-left tile's body covered the sand; the search treats start
    /// cells as clear and planned to walk through it). If the start body overlaps a blocking tile, use a neighbouring
    /// cell where the body fits: one row up when only floor slopes / half blocks in the feet row are in the way (the
    /// feet ride on them, sunk into their row), else the column to the left, then the right, then those one row up
    /// (again only past floor slopes / half blocks).
    /// </summary>
    public static TilePoint NormalizeStart(ITileGrid grid, TilePoint start)
    {
        if (BodyFits(grid, start)) return start;
        bool lowFeet = OnlyLowTopsInFeetRow(grid, start);
        var up = new TilePoint(start.X, start.Y - 1);
        if (lowFeet && BodyFits(grid, up)) return up;
        foreach (int dx in new[] { -1, 1 })
        {
            var c = new TilePoint(start.X + dx, start.Y);
            if (BodyFits(grid, c)) return c;
        }
        if (lowFeet)
            foreach (int dx in new[] { -1, 1 })
            {
                var c = new TilePoint(start.X + dx, start.Y - 1);
                if (BodyFits(grid, c)) return c;
            }
        return start;
    }

    private static bool BodyFits(ITileGrid grid, TilePoint p) => NavMath.BodyClear(grid, p);

    /// <summary>Every blocking tile of the body at p is a floor slope or half block in its feet row.</summary>
    private static bool OnlyLowTopsInFeetRow(ITileGrid grid, TilePoint p)
    {
        for (int dy = 0; dy < 3; dy++)
            for (int dx = 0; dx < 2; dx++)
            {
                var t = grid.Get(p.X + dx, p.Y - dy);
                if (t.BlocksBody && !(dy == 0 && t.IsLowTop)) return false;
            }
        return true;
    }

    public static PathResult FindPath(ITileGrid grid, TilePoint start, TilePoint goal,
        MovementProfile profile, PathOptions options)
    {
        start = NormalizeStart(grid, start);
        if (start == goal) return new PathResult(true, Array.Empty<PathStep>(), null, 0, goal);
        var s = new Search(grid, start, goal, profile, options);
        try { return s.Run(); }
        finally { s.Dispose(); }
    }

    private struct Node
    {
        public int X, Y, Parent, Builds, DigStart, DigCount, WX, WY;
        /// <summary>Consecutive steps up to here that ended with the head in liquid (0 = head dry).</summary>
        public int Sub;
        /// <summary>Non-swimmers in liquid with nothing under the feet: tiles risen in the current water jump
        /// (1..MaxWaterJumpUp) or <see cref="Sinking"/>. 0 everywhere else (ground, dry, swimmers).</summary>
        public int Rise;
        public float G;
        public bool Built, Closed, HasWork, Door;
        public MoveKind Kind;
    }

    private const int Sinking = -1;

    private sealed class Search : IDisposable
    {
        private readonly TileCache _t;
        private readonly TilePoint _start, _requestedGoal;
        private TilePoint _goal;
        private readonly MovementProfile _p;
        private readonly PathOptions _o;
        private readonly bool _canDig, _canBuild;
        private readonly int _buildCap, _maxFallScan, _maxSub;
        private readonly bool _trackBreath, _swim;
        private readonly int _waterRise;

        private Node[] _nodes = new Node[1024];
        private int _count;
        private readonly int[] _flatIndex;   // window-indexed node index + 1
        private readonly int[] _fallMemo;    // window-indexed fall result (see FallScan)
        private readonly Dictionary<long, int> _dict = new(); // states outside the window
        private readonly Dictionary<(int X, int Y, int Sub, int Rise), int> _wetDict = new(); // see Slot
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
            _trackBreath = !p.WaterBreathing;
            _maxSub = Math.Max(0, p.MaxSubmergedSteps);
            _swim = p.CanSwim;
            _waterRise = Math.Clamp(p.MaxWaterJumpUp, 0, 64);
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
            {
                if (!_o.BestEffort)
                    return new PathResult(false, Array.Empty<PathStep>(),
                        $"goal_not_standable: no standable position within {GoalSearchRadius} tiles of {_requestedGoal}", 0);
                _goal = _requestedGoal; // heuristic target only
            }

            BodyHas(_start.X, _start.Y, 0xFF); // classify the start body (seeds TileCache.Seen)
            _count = 1;
            // Already under water: the start counts as the first submerged step (never rejected: the path should
            // still lead out; the surfacing reflex covers the breath).
            int startSub = _trackBreath && HeadWet(_start.X, _start.Y) ? 1 : 0;
            // A non-swimmer starting in liquid with nothing underfoot is assumed to be sinking (conservative).
            int startRise = Afloat(_start.X, _start.Y) ? Sinking : 0;
            _nodes[0] = new Node { X = _start.X, Y = _start.Y, Parent = -1, Sub = startSub, Rise = startRise };
            Slot(_start.X, _start.Y, startSub, startRise) = 1;
            _open.Enqueue(0, H(_start.X, _start.Y));

            int expanded = 0, fallback = -1, fallbackDeadline = 0, closest = 0;
            float closestH = H(_start.X, _start.Y);
            while (_open.TryDequeue(out int ni, out _))
            {
                ref var n = ref _nodes[ni];
                if (n.Closed) continue;
                n.Closed = true;
                if (_o.BestEffort && !n.Built && CanEnd(n) && H(n.X, n.Y) < closestH)
                {
                    closestH = H(n.X, n.Y);
                    closest = ni;
                }

                if (n.X == _goal.X && n.Y == _goal.Y) return Success(ni, expanded);
                if (fallback < 0 && Chebyshev(n.X, n.Y) <= _o.GoalTolerance && CanEnd(n))
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
            if (_o.BestEffort && closest != 0)
            {
                var r = Success(closest, expanded);
                return r with { ResolvedGoal = new TilePoint(_nodes[closest].X, _nodes[closest].Y) };
            }
            return new PathResult(false, Array.Empty<PathStep>(), $"unreachable: searched {expanded} nodes", expanded);
        }

        /// <summary>A path may end here: a non-swimmer can't stay in liquid with nothing underfoot (it sinks) and,
        /// unless it breathes water, mustn't be left with the head under (only swimmers can come up anywhere).</summary>
        private bool CanEnd(in Node n) =>
            _swim || n.Rise == 0 && (!_trackBreath || !HeadWet(n.X, n.Y)) || n.Built;

        /// <summary>A non-swimmer's body is in liquid with no ground under it: it rises in a water jump or sinks.</summary>
        private bool Afloat(int x, int y) => !_swim && BodyHas(x, y, TileCache.Liquid) && !HasSupport(x, y);

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

        /// <summary>The body fits and something holds it: ground, liquid, or — when building is allowed — a plain,
        /// anchored air tile under the left foot where a platform can go (mid-shaft spots the search reaches by
        /// climbing with platforms; e.g. a stand to mine ore beside a shaft).
        /// Without <see cref="MovementProfile.CanSwim"/> liquid holds nobody, and a goal with the head under water is
        /// refused (the player would be left there to drown) unless it breathes water.</summary>
        private bool GoalOk(int x, int y)
        {
            if (!(BodyClear(x, y) || (_o.DigToGoal && _canDig && BodyDiggable(x, y)))) return false;
            bool wet = BodyHas(x, y, TileCache.Liquid);
            if (!_swim && wet && _trackBreath && HeadWet(x, y)) return false;
            return HasSupport(x, y) || (wet && _swim)
                   || (_canBuild && _buildCap > 0 && PlainAir(x, y + 1) && Anchored(x, y + 1));
        }

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
                if (n.Door)
                {
                    steps.Add(new PathStep(from, MoveKind.OpenDoor, new TilePoint(n.WX, n.WY)));
                    steps.Add(new PathStep(new TilePoint(n.X, n.Y), n.Kind));
                    continue;
                }
                steps.Add(new PathStep(new TilePoint(n.X, n.Y), n.Kind,
                    n.HasWork ? new TilePoint(n.WX, n.WY) : null));
            }
            if (_o.CloseDoorsBehind) AddDoorCloses(steps);
            return new PathResult(true, steps, null, expanded, _goal);
        }

        /// <summary>
        /// After each OpenDoor, close the door again at the first standing position whose body is clear of the door
        /// column (and within <see cref="CloseDoorReach"/> of it), unless the path still has to pass that column later.
        /// </summary>
        private static void AddDoorCloses(List<PathStep> steps)
        {
            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i].Kind != MoveKind.OpenDoor || steps[i].WorkTile is not { } door) continue;
                int j = i + 1;
                while (j < steps.Count && (!Standing(steps[j].Kind) || Covers(steps[j].Pos, door.X))) j++;
                if (j >= steps.Count) continue;
                var at = steps[j].Pos;
                int gap = at.X > door.X ? at.X - door.X : door.X - (at.X + 1);
                if (gap > CloseDoorReach || Math.Abs(at.Y - door.Y) > CloseDoorReach) continue;
                bool again = false;
                for (int k = j + 1; k < steps.Count && !again; k++) again = Covers(steps[k].Pos, door.X);
                if (again) continue;
                steps.Insert(j + 1, new PathStep(at, MoveKind.CloseDoor, door));
            }
        }

        private static bool Standing(MoveKind k) =>
            k is MoveKind.Walk or MoveKind.StepUp or MoveKind.Jump or MoveKind.Fall or MoveKind.DropThrough or MoveKind.BuildDown;

        private static bool Covers(TilePoint body, int column) => column == body.X || column == body.X + 1;

        private int Chebyshev(int x, int y) => Math.Max(Math.Abs(x - _goal.X), Math.Abs(y - _goal.Y));
        private float H(int x, int y) => Math.Abs(x - _goal.X) + Math.Abs(y - _goal.Y);

        /// <summary>
        /// States are keyed by position only. Builds used are carried in the node (and capped when
        /// generating edges) but are not part of the key: keying on them multiplies the state
        /// space by the build cap and made budget-exhausting searches ~4x slower. "Built" needs no
        /// key either: an unsupported, dry position is only ever reachable by building under it.
        /// Trade-off: a cheaper route using more platforms can shadow a dearer one using fewer.
        /// Submerged positions (head in liquid) are the exception: they are keyed with their run length of submerged
        /// steps (≤ MaxSubmergedSteps, so at most that many states per wet cell), so a cheap route that has been
        /// under water for long can't shadow one with air left. A cell is wet or dry for the whole search.
        /// Likewise a non-swimmer afloat is keyed with its water-jump state (rise so far, or sinking), so a cheap
        /// sinking arrival can't shadow a rising one that still gets out.
        /// </summary>
        private ref int Slot(int x, int y, int sub, int rise)
        {
            if (sub > 0 || rise != 0)
                return ref CollectionsMarshal.GetValueRefOrAddDefault(_wetDict, (x, y, sub, rise), out _);
            if (_t.TryIndex(x, y, out int i)) return ref _flatIndex[i];
            long key = ((long)x << 32) | (uint)y;
            return ref CollectionsMarshal.GetValueRefOrAddDefault(_dict, key, out _);
        }

        /// <param name="rise">For a non-swimmer ending afloat (in liquid, nothing underfoot): tiles risen in the
        /// current water jump, or <see cref="Sinking"/> (the default: falling or walking into liquid sinks).
        /// Ignored (0) anywhere else.</param>
        private void Add(int parent, int x, int y, int builds, bool built, float cost, MoveKind kind,
            bool withDigs = false, int wx = 0, int wy = 0, bool hasWork = false, bool door = false, int rise = Sinking)
        {
            if (built || !Afloat(x, y)) rise = 0;
            float g = _nodes[parent].G + cost;
            if (BodyHas(x, y, TileCache.Unknown)) g += UnknownPenalty;
            int sub = 0;
            if (_trackBreath && HeadWet(x, y))
            {
                sub = _nodes[parent].Sub + 1;
                if (sub > _maxSub) return; // out of breath
                g += SubmergedPenalty;
            }
            ref int slot = ref Slot(x, y, sub, rise);
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
                X = x, Y = y, G = g, Parent = parent, Builds = builds, Built = built, Kind = kind, Sub = sub, Rise = rise,
                DigStart = digStart, DigCount = digCount, HasWork = hasWork, WX = wx, WY = wy, Door = door,
            };
            _open.Enqueue(idx, g + H(x, y));
        }

        // ---- tile predicates -------------------------------------------------------------

        private bool InSource(int x, int y) => x >= _sx && x <= _sx + 1 && y >= _sy - 2 && y <= _sy;

        /// <summary>Body may occupy this tile (not solid, not lava/hazard).</summary>
        private bool Passable(int x, int y) =>
            InSource(x, y) || (_t.F(x, y) & (TileCache.Block | TileCache.Deadly)) == 0;

        /// <summary>The head (top body row, either column) is in water/honey: the player can't breathe.</summary>
        private bool HeadWet(int x, int y) => ((_t.F(x, y - 2) | _t.F(x + 1, y - 2)) & TileCache.Liquid) != 0;

        private bool HasSupport(int x, int y) => ((_t.F(x, y + 1) | _t.F(x + 1, y + 1)) & TileCache.Stand) != 0;

        /// <summary>Every body cell is open or a block the current pickaxe can dig (not deadly, not protected).</summary>
        private bool BodyDiggable(int x, int y)
        {
            for (int r = y - 2; r <= y; r++)
                for (int c = x; c <= x + 1; c++)
                {
                    if (Passable(c, r)) continue;
                    ushort f = _t.F(c, r);
                    if ((f & TileCache.Deadly) != 0 || (f & TileCache.Diggable) == 0) return false;
                }
            return true;
        }

        private bool BodyClear(int x, int y)
        {
            for (int r = y - 2; r <= y; r++)
                if (!Passable(x, r) || !Passable(x + 1, r)) return false;
            return true;
        }

        private bool BodyHas(int x, int y, ushort flag)
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
            ushort f = _t.F(x, y);
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
                if (_swim)
                {
                    Swim(ni, x, y, builds);
                    Jumps(ni, x, y, builds);
                }
                else
                {
                    Wade(ni, x, y, builds, n.Rise);
                    // Jumping needs the bottom under the feet (vanilla: velocity.Y == 0).
                    if (HasSupport(x, y)) Jumps(ni, x, y, builds);
                }
                // Standing on the bottom of shallow liquid (e.g. one body column in a pool's edge): the feet have real
                // ground, so every land move works as well. Without this a wet start whose only way on is walking off
                // an edge, stepping up, digging or building had no moves at all ("unreachable: searched 2 nodes").
                // (Wet positions are never Built: platforms only go under dry bodies.)
                if (!HasSupport(x, y)) return;
                Horizontal(ni, x, y, builds, -1);
                Horizontal(ni, x, y, builds, +1);
                StepUp(ni, x, y, builds, -1);
                StepUp(ni, x, y, builds, +1);
                DropThrough(ni, x, y, builds, false);
                DigDown(ni, x, y, builds);
                BuildUp(ni, x, y, builds);
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
            DropThrough(ni, x, y, builds, built);
            if (!built) DigDown(ni, x, y, builds);
            BuildUp(ni, x, y, builds);
            BuildDown(ni, x, y, builds, built);
        }

        private void Horizontal(int ni, int x, int y, int builds, int dx)
        {
            int tx = x + dx;
            int col = dx > 0 ? x + 2 : x - 1;
            _digs.Clear();
            int doorRow = int.MinValue;
            for (int r = y - 2; r <= y; r++)
            {
                if (_o.OpenDoors && !InSource(col, r) && (_t.F(col, r) & TileCache.Door) != 0)
                {
                    doorRow = r;
                    continue;
                }
                if (Classify(col, r) < 0) return;
            }

            if (doorRow != int.MinValue)
            {
                // Open the door, then walk into its column. Kept simple: no digging, falling or swimming with it.
                if (_digs.Count > 0 || BodyHas(tx, y, TileCache.Liquid) || !HasSupport(tx, y)) return;
                Add(ni, tx, y, builds, false, WalkCost + DoorCost, MoveKind.Walk, wx: col, wy: doorRow, door: true);
                return;
            }

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

            // A platform this path just placed under the target body holds it: that is no fall (bridging still works).
            if (!PlannedUnder(ni, tx, y) && FallScan(tx, y, out int ly, out bool lq) && (lq || FallOk(ly - y)))
            {
                // One row down along a slope or off / onto a half block: vanilla keeps the feet on the surface
                // (Collision.StepDown), so it's a walk the follower can flow through at full speed.
                if (!lq && ly == y + 1 && (LowTopUnder(x, y) || LowTopUnder(tx, ly)))
                    Add(ni, tx, ly, builds, false, WalkCost, MoveKind.Walk);
                else Add(ni, tx, ly, builds, false, FallCost(ly - y), MoveKind.Fall);
            }

            // Bridge: place a platform under the target's inner column, next to our support.
            if (_canBuild && builds < _buildCap)
            {
                int wx = dx > 0 ? x + 1 : x, wy = y + 1;
                if (PlainAirOrLiquid(wx, wy) && AnchoredFrom(ni, wx, wy) && !SunkBelowBridge(dx > 0 ? x : x + 1, wy, dx))
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
            // Vanilla auto-steps onto solid blocks; onto a platform only while Up is held (the follower does) and
            // never when another platform sits right above it.
            if (!StepSupport(tx, y) && !StepSupport(tx + 1, y)) return;
            // A rise made of a floor slope climbing our way or a half block is a plain walk in vanilla (the slope
            // lifts the hitbox as it moves; a half block is a half-tile step).
            if (_digs.Count == 0 && SmoothRise(tx, y, dx))
            {
                Add(ni, tx, ty, builds, false, WalkCost, MoveKind.Walk);
                return;
            }
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
            Span<ulong> plat = stackalloc ulong[nCols];
            for (int i = 0; i < nCols; i++)
            {
                _t.Column(c0 + i, top, out pass[i], out stand[i], out desc[i], out liq[i]);
                plat[i] = stand[i] & pass[i]; // standable but passable = platform
            }
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
                // across[a] = how far the body can move horizontally with feet at row y-a (a = 0: the source row).
                for (int a = 0; a <= rise; a++)
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

                // Platforms over the span the jump crosses (OR of the columns from the source body to the target body).
                ulong platSpan = plat[x - c0] | plat[x + 1 - c0];
                for (int k = dir > 0 ? 0 : 1; k <= maxGap; k++)
                {
                    int tx = x + dir * k, i0 = tx - c0;
                    platSpan |= plat[i0] | plat[i0 + 1];
                    ulong pp = pass[i0] & pass[i0 + 1], dd = desc[i0] & desc[i0 + 1];
                    ulong ss = stand[i0] | stand[i0 + 1], ll = liq[i0] | liq[i0 + 1];
                    for (int ty = y - maxUp; ty <= y + maxUp; ty++)
                    {
                        if (ty >= y && k < 2) continue; // walking/falling covers these
                        if (ty == y && k <= walkReach) continue;
                        int a = y - Math.Min(y, ty) + 1;
                        if (a > rise || across[a] < k) continue;
                        // The real arc goes higher than the box path. It bonks on anything lower than its apex, so
                        // the ceiling is the highest level the body can cross the whole distance at. A platform
                        // above both feet rows is risen through and then lands the player on top of it, so the
                        // apex must stay under it (the follower releases jump early: JumpPlatformCeiling).
                        int ceiling = rise;
                        while (ceiling > a && across[ceiling] < k) ceiling--;
                        int lim = Math.Min(y, ty);
                        ulong above = lim - top >= 64 ? ulong.MaxValue : (1UL << (lim - top)) - 1;
                        ulong plats = platSpan & above;
                        if (plats != 0)
                        {
                            int r = top + 63 - System.Numerics.BitOperations.LeadingZeroCount(plats);
                            ceiling = Math.Min(ceiling, y - r);
                            if (ceiling < a) continue;
                        }
                        if (_p.JumpReach != null)
                        {
                            // Launch level: the lowest level the body can cross the whole distance at. Anything
                            // lower blocks sideways motion until the feet clear it (worst case: right in front).
                            int launch = 0;
                            while (launch < a && across[launch] < k) launch++;
                            if (k > _p.JumpReachAt(ty - y, ceiling, launch)) continue;
                        }
                        if (ty >= y)
                        {
                            // Straight down from row y-1 to ty; platforms would catch the body.
                            ulong need = Bits(y, ty, top);
                            if ((dd & need) != need) break; // deeper targets are blocked too
                        }
                        // Higher target: across at feet row ty-1, then down one row into ty. A platform in
                        // t's feet row would catch the body one row early, so row ty must be descendable.
                        else if (((dd >> (ty - top)) & 1) == 0) continue;
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

        /// <param name="built">Standing on a platform this path placed (not in the grid yet).</param>
        private void DropThrough(int ni, int x, int y, int builds, bool built)
        {
            ushort a = _t.F(x, y + 1), b = _t.F(x + 1, y + 1);
            if (((a | b) & TileCache.Block) != 0) return;
            if (!built && ((a | b) & TileCache.Plat) == 0) return;
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
            // A platform above (or in) the body would be risen through and land the player on it instead: the
            // jump's apex must stay below it, yet get the feet above the new platform (follower: ApexAbove).
            for (int r = y - 1; r >= y - maxK - 1; r--)
                if (((_t.F(x, r) | _t.F(x + 1, r)) & TileCache.Plat) != 0)
                {
                    maxK = Math.Min(maxK, y - r - 1);
                    break;
                }
            for (int k = 1; k <= maxK; k++)
            {
                int r = y - 2 - k; // new head row
                if (!Passable(x, r) || !Passable(x + 1, r)) return;
                int ty = y - k, wy = ty + 1;
                if (!PlainAir(x, wy)) continue; // must place into plain air
                // Vanilla places a platform only next to a tile on one of its 4 sides or in front of a background wall.
                if (!AnchoredFrom(ni, x, wy)) continue;
                if (HasSupport(x, ty) || BodyHas(x, ty, TileCache.Liquid)) continue;
                Add(ni, x, ty, builds + 1, true, BuildCost, MoveKind.BuildPlatform, wx: x, wy: wy, hasWork: true);
            }
        }

        /// <summary>
        /// Descend where falling isn't safe: place a platform 2–3 rows under the feet (anchored to a known tile or the
        /// platform this path placed last), then drop through the platform underfoot or step off the edge onto it.
        /// Repeated, this walks a deep shaft down a platform at a time, every fall short.
        /// </summary>
        private void BuildDown(int ni, int x, int y, int builds, bool built)
        {
            if (!_canBuild || builds >= _buildCap) return;
            // Straight down: through a platform underfoot (one from the grid, or the one this path placed: built).
            ushort a = _t.F(x, y + 1), b = _t.F(x + 1, y + 1);
            bool platformOnly = built || (((a | b) & TileCache.Block) == 0 && ((a | b) & TileCache.Plat) != 0);
            if (platformOnly && Passable(x, y + 1) && Passable(x + 1, y + 1) && !SafeFall(x, y + 1, y))
                Descend(ni, y, x, builds);
            if (built) return; // stepping off a platform this path placed: the grid doesn't know where it ends
            for (int dx = -1; dx <= 1; dx += 2)
            {
                int tx = x + dx, col = dx > 0 ? x + 2 : x - 1;
                if (!Passable(col, y) || !Passable(col, y - 1) || !Passable(col, y - 2)) continue;
                if (HasSupport(tx, y) || BodyHas(tx, y, TileCache.Liquid) || SafeFall(tx, y, y)) continue;
                Descend(ni, y, tx, builds);
            }
        }

        /// <summary>Land 1..<see cref="MaxBuildDown"/> rows below source row <paramref name="y"/> in column tx on a
        /// platform placed under the landing body.</summary>
        private void Descend(int ni, int y, int tx, int builds)
        {
            for (int k = 1; k <= MaxBuildDown; k++)
            {
                int ty = y + k;
                if (!BodyClear(tx, ty) || BodyHas(tx, ty, TileCache.Liquid)) return;
                if (k > 1 && HasSupport(tx, ty - 1)) return; // something would catch the feet a row early
                if (HasSupport(tx, ty)) continue;            // the plain fall/drop covers it
                for (int wx = tx; wx <= tx + 1; wx++)
                {
                    int wy = ty + 1;
                    if (!PlainAir(wx, wy) || !AnchoredFrom(ni, wx, wy)) continue;
                    Add(ni, tx, ty, builds + 1, true, BuildCost + FallCost(k), MoveKind.BuildDown, wx: wx, wy: wy, hasWork: true);
                    break;
                }
            }
        }

        /// <summary>Falling from feet row <paramref name="fy"/> (column x) lands safely, measured from source row y.</summary>
        private bool SafeFall(int x, int fy, int y) => FallScan(x, fy, out int ly, out bool lq) && (lq || FallOk(ly - y));

        /// <summary>The node was reached by placing a platform that lies under the body at (tx, y).</summary>
        private bool PlannedUnder(int ni, int tx, int y)
        {
            ref var n = ref _nodes[ni];
            return n.Built && n.HasWork && n.WY == y + 1 && (n.WX == tx || n.WX == tx + 1);
        }

        /// <summary>Every block in row y under the body at column tx (stepping in direction dx) is a half block or a floor
        /// slope climbing in direction dx, and there is at least one.</summary>
        private bool SmoothRise(int tx, int y, int dx)
        {
            bool any = false;
            for (int c = tx; c <= tx + 1; c++)
            {
                ushort f = _t.F(c, y);
                if ((f & TileCache.Block) == 0) continue;
                if ((f & TileCache.LowTop) == 0) return false;
                bool slope = (f & (TileCache.RisesRight | TileCache.RisesLeft)) != 0;
                if (slope && (f & (dx > 0 ? TileCache.RisesRight : TileCache.RisesLeft)) == 0) return false;
                any = true;
            }
            return any;
        }

        /// <summary>The feet standing on block (x, y) sit below a platform placed next to it in direction dx: a half block,
        /// or a slope falling toward the gap. The platform's top is then above the feet and walking on doesn't climb
        /// it (seen in the sim: the player slid down the slope under the new platform and into the pit).</summary>
        private bool SunkBelowBridge(int x, int y, int dx)
        {
            ushort f = _t.F(x, y);
            if ((f & TileCache.LowTop) == 0) return false;
            if ((f & (TileCache.RisesRight | TileCache.RisesLeft)) == 0) return true; // half block
            return (f & (dx > 0 ? TileCache.RisesLeft : TileCache.RisesRight)) != 0;
        }

        /// <summary>A half block or floor slope in the support row of the body at (x, y).</summary>
        private bool LowTopUnder(int x, int y) => ((_t.F(x, y + 1) | _t.F(x + 1, y + 1)) & TileCache.LowTop) != 0;

        private bool StepSupport(int x, int y)
        {
            ushort f = _t.F(x, y);
            if ((f & TileCache.Block) != 0) return true;
            return (f & TileCache.Plat) != 0 && (_t.F(x, y - 1) & TileCache.Plat) == 0;
        }

        /// <summary>Vanilla lets a tile (platform) be placed only next to a tile on one of its 4 sides, or in front of a
        /// background wall. Diagonals don't count: seen in game, a climb whose platform was only diagonal to the floor
        /// never got placed and the player jumped and fell back forever.</summary>
        private bool Anchored(int x, int y) =>
            (_t.F(x, y) & TileCache.Wall) != 0
            || ((_t.F(x - 1, y) | _t.F(x + 1, y) | _t.F(x, y - 1) | _t.F(x, y + 1)) & TileCache.Stand) != 0;

        /// <summary><see cref="Anchored"/>, or orthogonally next to the platform this path placed in the step that
        /// reached node <paramref name="ni"/> (the search doesn't write its own platforms into the grid; chained
        /// bridge/climb platforms hang off the previous one).</summary>
        private bool AnchoredFrom(int ni, int x, int y)
        {
            if (Anchored(x, y)) return true;
            ref var n = ref _nodes[ni];
            return n.Kind is (MoveKind.BuildPlatform or MoveKind.BuildDown) && n.HasWork && Math.Abs(n.WX - x) + Math.Abs(n.WY - y) == 1;
        }

        /// <summary>Known empty air (a background wall behind it is fine).</summary>
        private bool PlainAir(int x, int y) => (_t.F(x, y) & ~TileCache.Wall) == TileCache.Computed;

        /// <summary><see cref="PlainAir"/> or water/honey with no tile in it: vanilla places platforms into liquid
        /// (a bridge over a lake's surface row).</summary>
        private bool PlainAirOrLiquid(int x, int y) => (_t.F(x, y) & ~(TileCache.Wall | TileCache.Liquid)) == TileCache.Computed;

        private void Swim(int ni, int x, int y, int builds)
        {
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int tx = x + dx, ty = y + dy;
                    if (!BodyClear(tx, ty)) continue;
                    // No corner cutting: a diagonal needs one of its two orthogonal neighbours clear too.
                    if (dx != 0 && dy != 0 && !BodyClear(tx, y) && !BodyClear(x, ty)) continue;
                    if (!BodyHas(tx, ty, TileCache.Liquid) && !HasSupport(tx, ty)) continue;
                    Add(ni, tx, ty, builds, false, dx != 0 && dy != 0 ? SwimDiagCost : SwimCost, MoveKind.Swim);
                }
        }

        /// <summary>
        /// Non-swimmer moves in liquid (vanilla without Flippers: the player sinks, and a jump needs the bottom). On
        /// ground: every direction (up starts a water jump). Rising (<paramref name="rise"/> tiles into a water jump):
        /// up and diagonally up while the jump lasts, sideways (it then sinks unless ground holds it), down. Sinking:
        /// down and diagonally down, sideways only onto ground. Targets must be in liquid or have ground under them
        /// (the shore). Breath is capped by <see cref="Add"/> as for any submerged step.
        /// </summary>
        private void Wade(int ni, int x, int y, int builds, int rise)
        {
            if (HasSupport(x, y)) rise = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nr = Sinking;
                    if (dy < 0)
                    {
                        if (rise < 0 || rise >= _waterRise) continue;
                        nr = rise + 1;
                    }
                    int tx = x + dx, ty = y + dy;
                    if (!BodyClear(tx, ty)) continue;
                    if (dx != 0 && dy != 0 && !BodyClear(tx, y) && !BodyClear(x, ty)) continue;
                    bool supported = HasSupport(tx, ty);
                    if (!BodyHas(tx, ty, TileCache.Liquid) && !supported) continue;
                    if (dy == 0 && rise < 0 && !supported) continue; // no drifting sideways while sinking
                    Add(ni, tx, ty, builds, false, dx != 0 && dy != 0 ? SwimDiagCost : SwimCost, MoveKind.Swim, rise: nr);
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
        public const ushort Computed = 1, Block = 2, Stand = 4, Plat = 8, Liquid = 16, Deadly = 32, Unknown = 64, Diggable = 128,
            Door = 256, Wall = 512, Occupied = 1024,
            // Shaped solid blocks (still Block|Stand): LowTop = half block or floor slope; RisesRight / RisesLeft = a
            // floor slope whose surface climbs toward the right (vanilla SlopeDownRight) / the left (SlopeDownLeft).
            LowTop = 2048, RisesRight = 4096, RisesLeft = 8192;
        private const int Margin = 128, MaxDim = 1024;

        private readonly ITileGrid _g;
        private readonly int _x0, _y0, _w, _h, _pick;
        private readonly ushort[] _f;

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
            _f = ArrayPool<ushort>.Shared.Rent(Size);
            Array.Clear(_f, 0, Size);
            _blocks = (_h + 63) / 64;
            _cols = ArrayPool<ColMask>.Shared.Rent(_w * _blocks);
            Array.Clear(_cols, 0, _w * _blocks);
        }

        public int Size => _w * _h;

        public void Dispose()
        {
            ArrayPool<ushort>.Shared.Return(_f);
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

        public ushort F(int x, int y)
        {
            if (!TryIndex(x, y, out int i)) return Classify(_g.Get(x, y));
            ushort b = _f[i];
            if (b == 0) _f[i] = b = Classify(_g.Get(x, y));
            return b;
        }

        private ushort Classify(TileInfo t)
        {
            int b = Computed;
            switch (t.Kind)
            {
                case TileKind.Solid:
                    b |= Block | Stand;
                    if (_pick > 0 && !t.Protected && t.PickPowerRequired <= _pick) b |= Diggable;
                    if (t.Door) b |= Door;
                    if (t.IsLowTop) b |= LowTop;
                    if (t.Slope == TileSlope.DownRight) b |= RisesRight;
                    else if (t.Slope == TileSlope.DownLeft) b |= RisesLeft;
                    break;
                case TileKind.Platform: b |= Stand | Plat; break;
                case TileKind.Water:
                case TileKind.Honey: b |= Liquid; break;
                case TileKind.Lava:
                case TileKind.Hazard: b |= Deadly; break;
                case TileKind.Unknown: b |= Unknown; break;
            }
            if (t.Wall) b |= Wall;
            if (t.Occupied) b |= Occupied;
            Seen |= b;
            return (ushort)b;
        }
    }
}
