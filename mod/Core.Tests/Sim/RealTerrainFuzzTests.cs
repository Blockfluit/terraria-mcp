using System.Collections.Concurrent;
using System.Text;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;
using Xunit.Abstractions;

namespace LlmPlayer.Core.Tests.Sim;

/// <summary>
/// Navigation fuzz on real terrain captured from the playtest world (Fixtures/RealTerrain, dev_dump): seeded random
/// start/goal pairs among standable cells (near, far and vertical), planned and walked exactly like move_to does
/// (<see cref="MoveToSim"/>: PlanJob windows and partial plans, PathFollower, replanning) in <see cref="MiniPhysics"/>
/// with a realistic early-game kit (copper pickaxe, platforms, vanilla jump and safe fall, no swimming).
/// Every outcome is classified; the test asserts aggregate arrival rates and prints a summary.
/// Debug one case: FUZZ_CASE="west_caves:17" dotnet test --filter RealTerrainFuzzTests.Single
/// </summary>
[Collection(RealTerrainCollection.Name)]
public class RealTerrainFuzzTests(ITestOutputHelper output)
{
    public static readonly string[] Captured = { "base_underground", "west_caves", "corruption_chasm", "desert_surface" };

    /// <summary>Every capture as is and mirrored left-right ("…/m": slopes swap sides), which doubles the left/right
    /// asymmetries the follower and planner get to see.</summary>
    public static readonly string[] Fixtures = Captured.Concat(Captured.Select(f => f + "/m")).ToArray();
    private const int Near = 40, Far = 20, Vertical = 20;

    public enum Category { Near, Far, Vertical }

    public enum Verdict
    {
        Arrived,
        /// <summary>The oracle finds no route inside the widest planning window (PlanJob snapshot) either: the trip needs a
        /// detour the in-game planner can't see in one plan. Reported, not counted against navigation.</summary>
        BeyondWindow,
        /// <summary>The very first plan fails though the oracle connects start and goal.</summary>
        PlannerUnreachable,
        /// <summary>A plan was found but following / replanning never arrived.</summary>
        FollowerFailed,
        Hurt,
        Drowning,
    }

    public sealed record Case(string Fixture, int Index, Category Cat, TilePoint Start, TilePoint Goal);

    public sealed record Result(Case C, Verdict V, string? Reason, int Ticks, int Plans, int Replans, int FailedPlans,
        IReadOnlyList<string> Events);

    internal static MovementProfile Profile() =>
        JumpModel.Apply(new MovementProfile(PickPower: 35, BuildItemCount: Platforms), PlayerPhysics.Vanilla);

    private const int Platforms = 150;

    // ---- fixtures --------------------------------------------------------------------------------------------------

    private static readonly ConcurrentDictionary<string, string[]> RowCache = new();

    internal static string[] Rows(string fixture) => RowCache.GetOrAdd(fixture, f =>
    {
        bool mirror = f.EndsWith("/m");
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "RealTerrain", (mirror ? f[..^2] : f) + ".txt");
        var rows = File.ReadAllLines(path).Where(l => !l.StartsWith("# ")).Where(l => l.Length > 0);
        return (mirror ? rows.Select(FollowerIntegrationTests.Mirror) : rows).ToArray();
    });

    internal static ArrayTileGrid Grid(string fixture) => ArrayTileGrid.Parse(Rows(fixture)).Grid;

    // ---- sampling --------------------------------------------------------------------------------------------------

    /// <summary>Body clear, supported, no liquid or danger in it.</summary>
    internal static bool Standable(ITileGrid g, TilePoint p)
    {
        if (!Pathfinder.CanStand(g, p)) return false;
        for (int y = p.Y - 2; y <= p.Y; y++)
            for (int x = p.X; x <= p.X + 1; x++)
                if (g.Get(x, y).Kind is not TileKind.Air) return false;
        return true;
    }

    public static List<Case> Cases(string fixture)
    {
        var g = Grid(fixture);
        var oracle = new Oracle(g);
        var cells = new List<TilePoint>();
        for (int y = 3; y < g.Height - 2; y++)
            for (int x = 1; x < g.Width - 2; x++)
                if (Standable(g, new TilePoint(x, y))) cells.Add(new TilePoint(x, y));
        var rng = new Random(fixture.Aggregate(17, (h, c) => h * 31 + c));
        var cases = new List<Case>();
        void Pick(Category cat, int count, Func<TilePoint, TilePoint, bool> ok)
        {
            int made = 0;
            for (int attempt = 0; made < count && attempt < 200000; attempt++)
            {
                var s = cells[rng.Next(cells.Count)];
                var t = cells[rng.Next(cells.Count)];
                if (!ok(s, t) || oracle.Component(s) != oracle.Component(t)) continue;
                cases.Add(new Case(fixture, cases.Count, cat, s, t));
                made++;
            }
        }
        static double D(TilePoint a, TilePoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        Pick(Category.Near, Near, (s, t) => D(s, t) is >= 10 and <= 40);
        Pick(Category.Far, Far, (s, t) => D(s, t) is >= 60 and <= 150);
        Pick(Category.Vertical, Vertical, (s, t) => Math.Abs(s.Y - t.Y) >= 12 && Math.Abs(s.X - t.X) <= 8);
        return cases;
    }

    /// <summary>
    /// Generous reachability: 2x3 body positions whose tiles are open or diggable with the copper pickaxe (no lava,
    /// hazard, protected or too-hard tiles), connected orthogonally (digging, building and jumping make every such
    /// move possible in principle). Falls, breath and swimming are ignored.
    /// </summary>
    internal sealed class Oracle
    {
        private readonly int[] _comp;
        private readonly int _w;

        public Oracle(ArrayTileGrid g)
        {
            _w = g.Width;
            int h = g.Height;
            var okTile = new bool[_w, h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < _w; x++)
                {
                    var t = g.Get(x, y);
                    okTile[x, y] = t.Kind switch
                    {
                        TileKind.Lava or TileKind.Hazard => false,
                        TileKind.Solid => !t.Protected && t.PickPowerRequired <= 35,
                        _ => true,
                    };
                }
            bool Body(int x, int y)
            {
                if (x < 0 || x + 1 >= _w || y - 2 < 0 || y >= h) return false;
                for (int r = y - 2; r <= y; r++)
                    if (!okTile[x, r] || !okTile[x + 1, r]) return false;
                return true;
            }
            _comp = new int[_w * h];
            int next = 0;
            var q = new Queue<int>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < _w; x++)
                {
                    if (_comp[y * _w + x] != 0 || !Body(x, y)) continue;
                    _comp[y * _w + x] = ++next;
                    q.Enqueue(y * _w + x);
                    while (q.Count > 0)
                    {
                        int i = q.Dequeue(), cx = i % _w, cy = i / _w;
                        foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                        {
                            int nx = cx + dx, ny = cy + dy;
                            if (!Body(nx, ny) || _comp[ny * _w + nx] != 0) continue;
                            _comp[ny * _w + nx] = next;
                            q.Enqueue(ny * _w + nx);
                        }
                    }
                }
        }

        public int Component(TilePoint p) => _comp[p.Y * _w + p.X];
    }

    /// <summary>The generous oracle (see <see cref="Oracle"/>) restricted to the widest PlanJob window of the trip, minus
    /// its sealed outer ring. A goal beyond that window (partial plans) gets the trip's bounding box plus
    /// <see cref="PlanJob.Margin"/> instead: a route that needs a detour further out than that isn't one the player
    /// "obviously" has.</summary>
    internal static bool ReachableInWindow(ArrayTileGrid g, TilePoint s, TilePoint t)
    {
        var (x0, y0, w, h) = PlanJob.WidestWindow(s, t);
        int xa = Math.Max(1, x0 + 1), ya = Math.Max(0, y0 + 1), xb = Math.Min(g.Width - 1, x0 + w - 2), yb = Math.Min(g.Height - 1, y0 + h - 2);
        if (t.X < xa || t.X + 1 > xb || t.Y - 2 < ya || t.Y > yb)
        {
            xa = Math.Max(1, Math.Min(s.X, t.X) - PlanJob.Margin);
            xb = Math.Min(g.Width - 1, Math.Max(s.X, t.X) + 1 + PlanJob.Margin);
            ya = Math.Max(0, Math.Min(s.Y, t.Y) - 2 - PlanJob.Margin);
            yb = Math.Min(g.Height - 1, Math.Max(s.Y, t.Y) + PlanJob.Margin);
        }
        bool Ok(int x, int y)
        {
            if (x < xa || x > xb || y < ya || y > yb) return false;
            var k = g.Get(x, y);
            return k.Kind switch
            {
                TileKind.Lava or TileKind.Hazard => false,
                TileKind.Solid => !k.Protected && k.PickPowerRequired <= 35,
                _ => true,
            };
        }
        bool Body(int x, int y) => Ok(x, y) && Ok(x + 1, y) && Ok(x, y - 1) && Ok(x + 1, y - 1) && Ok(x, y - 2) && Ok(x + 1, y - 2);
        var seen = new HashSet<TilePoint> { s };
        var q = new Queue<TilePoint>();
        q.Enqueue(s);
        while (q.Count > 0)
        {
            var p = q.Dequeue();
            if (p == t) return true;
            foreach (var d in new[] { new TilePoint(1, 0), new TilePoint(-1, 0), new TilePoint(0, 1), new TilePoint(0, -1) })
            {
                var n = p + d;
                if (!Body(n.X, n.Y) || !seen.Add(n)) continue;
                q.Enqueue(n);
            }
        }
        return false;
    }

    // ---- running ---------------------------------------------------------------------------------------------------

    public static Result RunCase(Case c, Action<string>? log = null, Queue<string>? trace = null)
    {
        var g = Grid(c.Fixture);
        var sim = new MiniPhysics(g, c.Start) { PickPower = 35, Platforms = Platforms };
        var move = new MoveToSim(g, sim, c.Goal, Profile(), new PathOptions(), log: log, fog: Fog(g, c.Start, c.Goal))
        {
            LogTicks = log != null && Environment.GetEnvironmentVariable("FUZZ_TICKS") == "1",
            Commit = Environment.GetEnvironmentVariable("FUZZ_NOCOMMIT") != "1",
        };
        int dist = Math.Abs(c.Start.X - c.Goal.X) + Math.Abs(c.Start.Y - c.Goal.Y);
        var outcome = move.Run(4000 + 100 * dist);
        if (trace != null) foreach (var l in move.Trace) trace.Enqueue(l);
        var v = outcome switch
        {
            MoveOutcome.Arrived => Verdict.Arrived,
            _ when !ReachableInWindow(Grid(c.Fixture), c.Start, c.Goal) => Verdict.BeyondWindow,
            MoveOutcome.Hurt => Verdict.Hurt,
            MoveOutcome.Drowning => Verdict.Drowning,
            _ when move.FirstPlanFailure != null && move.Plans == move.FailedPlans => Verdict.PlannerUnreachable,
            _ => Verdict.FollowerFailed,
        };
        return new Result(c, v, outcome == MoveOutcome.Arrived ? null : move.Reason, sim.Ticks, move.Plans, move.Replans,
            move.FailedPlans, move.Events);
    }

    /// <summary>FUZZ_FOG=r (diagnostic, not asserted): plan and follow with only the tiles within r of the start and of
    /// the goal revealed at first (move_to targets something the player has seen), the rest coming into view within
    /// <see cref="FogGrid.Radius"/> as the player moves, like in game. FUZZ_NOCOMMIT=1 turns the route commitment off.</summary>
    private static FogGrid? Fog(ArrayTileGrid g, TilePoint start, TilePoint goal)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("FUZZ_FOG"), out int r)) return null;
        var known = new bool[g.Width, g.Height];
        for (int y = 0; y < g.Height; y++)
            for (int x = 0; x < g.Width; x++)
                known[x, y] = Math.Max(Math.Abs(x - start.X), Math.Abs(y - start.Y)) <= r
                              || Math.Max(Math.Abs(x - goal.X), Math.Abs(y - goal.Y)) <= r;
        return new FogGrid(g, known);
    }

    [Fact]
    public void Summary()
    {
        var cases = Fixtures.SelectMany(Cases).ToList();
        var results = new Result[cases.Count];
        // Half the cores: each run's PlanJob searches on the thread pool while the run spins waiting for it.
        var par = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Parallel.For(0, cases.Count, par, i => results[i] = RunCase(cases[i]));

        var sb = new StringBuilder();
        sb.AppendLine($"real-terrain fuzz: {results.Length} cases in {sw.Elapsed.TotalSeconds:F0} s, " +
                      $"{results.Sum(r => r.Ticks) / 60.0 / 60.0:F0} game minutes");
        foreach (var grp in results.GroupBy(r => r.C.Fixture))
            sb.AppendLine($"  {grp.Key,-18} " + Line(grp));
        foreach (var grp in results.GroupBy(r => r.C.Cat))
            sb.AppendLine($"  {grp.Key,-18} " + Line(grp));
        sb.AppendLine($"  {"TOTAL",-18} " + Line(results));
        sb.AppendLine("verdicts: " + string.Join(", ", results.GroupBy(r => r.V).Select(g => $"{g.Key} {g.Count()}")));
        sb.AppendLine("events (Stuck/NeedsReplan/plan failures, by kind):");
        foreach (var e in results.SelectMany(r => r.Events).GroupBy(Kind).OrderByDescending(g => g.Count()))
            sb.AppendLine($"  {e.Count(),4} {e.Key}");
        sb.AppendLine("failures:");
        foreach (var r in results.Where(r => r.V != Verdict.Arrived))
            sb.AppendLine($"  {r.C.Fixture}:{r.C.Index} {r.C.Cat} {r.C.Start}->{r.C.Goal} {r.V}: {r.Reason} " +
                          $"(plans {r.Plans}, failed {r.FailedPlans}, replans {r.Replans}, ticks {r.Ticks})");
        output.WriteLine(sb.ToString());

        int reachable = results.Count(r => r.V != Verdict.BeyondWindow);
        double arrival = results.Count(r => r.V == Verdict.Arrived) / (double)reachable;
        int hurt = results.Count(r => r.V is Verdict.Hurt or Verdict.Drowning);
        Assert.True(arrival >= MinArrival, $"arrival {arrival:P1} < {MinArrival:P0}\n{sb}");
        Assert.True(hurt <= MaxHurt, $"{hurt} hurt/drowning runs\n{sb}");
    }

    /// <summary>Aggregate floors the fuzz holds (raise them as navigation improves).</summary>
    private const double MinArrival = 0.97;
    private const int MaxHurt = 0;

    private static string Line(IEnumerable<Result> rs)
    {
        var l = rs.ToList();
        int n = l.Count(r => r.V != Verdict.BeyondWindow), ok = l.Count(r => r.V == Verdict.Arrived);
        return $"{ok}/{n} arrived ({ok * 100.0 / Math.Max(1, n):F0}%), planner-unreachable {l.Count(r => r.V == Verdict.PlannerUnreachable)}, " +
               $"follower {l.Count(r => r.V == Verdict.FollowerFailed)}, hurt {l.Count(r => r.V == Verdict.Hurt)}, " +
               $"drown {l.Count(r => r.V == Verdict.Drowning)}, beyond-window {l.Count(r => r.V == Verdict.BeyondWindow)}, replans {l.Sum(r => r.Replans)}";
    }

    /// <summary>"stuck at Jump (12, 40) (player …)" → "stuck at Jump".</summary>
    private static string Kind(string e)
    {
        int paren = e.IndexOf(" (", StringComparison.Ordinal);
        var head = paren > 0 ? e[..paren] : e;
        if (head.StartsWith("plan failed")) return head.Split(':').Take(2).Aggregate((a, b) => a + ":" + b).Split(" searched")[0];
        return string.Join(' ', head.Split(' ').Take(head.StartsWith("path") ? 4 : 3));
    }

    /// <summary>Run one case verbosely (FUZZ_CASE="fixture:index").</summary>
    [Fact]
    public void Single()
    {
        var spec = Environment.GetEnvironmentVariable("FUZZ_CASE");
        if (string.IsNullOrEmpty(spec)) return;
        var parts = spec.Split(':');
        var c = Cases(parts[0])[int.Parse(parts[1])];
        output.WriteLine($"{c}");
        var trace = new Queue<string>();
        var r = RunCase(c, output.WriteLine, trace);
        foreach (var l in trace) output.WriteLine(l);
        output.WriteLine($"{r.V}: {r.Reason} ticks {r.Ticks} plans {r.Plans} replans {r.Replans}");
        var g = Grid(c.Fixture);
        int x0 = Math.Max(0, Math.Min(c.Start.X, c.Goal.X) - 6), x1 = Math.Min(g.Width - 1, Math.Max(c.Start.X, c.Goal.X) + 7);
        int y0 = Math.Max(0, Math.Min(c.Start.Y, c.Goal.Y) - 8), y1 = Math.Min(g.Height - 1, Math.Max(c.Start.Y, c.Goal.Y) + 4);
        var rows = Rows(c.Fixture);
        output.WriteLine($"crop x={x0}..{x1} y={y0}..{y1}");
        for (int y = y0; y <= y1; y++)
        {
            var row = rows[y].Substring(x0, x1 - x0 + 1).ToCharArray();
            if (y == c.Start.Y) row[c.Start.X - x0] = 'S';
            if (y == c.Goal.Y) row[c.Goal.X - x0] = 'G';
            output.WriteLine($"{y,4} {new string(row)}");
        }
    }
}

/// <summary>The real-terrain runs are CPU-heavy (the fuzz uses half the cores): run them on their own so they don't
/// skew the timing assertions of the pathfinder performance tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealTerrainCollection
{
    public const string Name = "real terrain";
}
