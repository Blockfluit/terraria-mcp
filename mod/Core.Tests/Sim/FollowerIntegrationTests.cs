using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;
using Xunit.Abstractions;

namespace LlmPlayer.Core.Tests.Sim;

/// <summary>
/// End-to-end: real Pathfinder output → PathFollower → MiniPhysics, every scenario also mirrored left-right.
/// A run passes when the player stands on the planned end position, unhurt, within the tick budget, having
/// never been reported Stuck / NeedsReplan, mined only planned tiles and never used the wrong tool.
/// </summary>
public class FollowerIntegrationTests(ITestOutputHelper output)
{
    private static MovementProfile Real(int pick = 0, int builds = 0) =>
        JumpModel.Apply(new MovementProfile(PickPower: pick, BuildItemCount: builds), PlayerPhysics.Vanilla);

    internal sealed record Scenario(string Name, GridBuilder Grid, MovementProfile Profile, PathOptions Options, int MaxTicks,
        int PickPower = 35, int Platforms = 100);

    private static readonly Dictionary<string, Scenario> Scenarios = new Scenario[]
    {
        new("flat_walk",
            new GridBuilder(40, 8).Fill(0, 6, 39, 7, '#').Put(2, 5, 'S').Put(30, 5, 'G'),
            Real(), new(), 400),
        new("one_step",
            new GridBuilder(40, 10).Fill(0, 8, 39, 9, '#').Fill(12, 7, 39, 7, '#').Put(2, 7, 'S').Put(25, 6, 'G'),
            Real(), new(), 400),
        new("stairs_up_and_down",
            Stairs(), Real(), new(AllowDig: false, AllowBuild: false), 900),
        new("cliff_5_up",
            new GridBuilder(40, 14).Fill(0, 12, 39, 13, '#').Fill(14, 7, 39, 11, '#').Put(2, 11, 'S').Put(25, 6, 'G'),
            Real(), new(AllowDig: false, AllowBuild: false), 500),
        new("cliff_5_down",
            new GridBuilder(40, 14).Fill(0, 12, 39, 13, '#').Fill(0, 7, 14, 11, '#').Put(4, 6, 'S').Put(30, 11, 'G'),
            Real(), new(AllowDig: false, AllowBuild: false), 500),
        new("gap_6",
            new GridBuilder(24, 40).Fill(0, 10, 5, 10, '#').Fill(12, 10, 23, 10, '#').Put(1, 9, 'S').Put(17, 9, 'G'),
            Real(), new(AllowDig: false, AllowBuild: false), 400),
        new("gap_6_standing_start",
            new GridBuilder(24, 40).Fill(0, 10, 5, 10, '#').Fill(12, 10, 23, 10, '#').Put(4, 9, 'S').Put(17, 9, 'G'),
            Real(), new(AllowDig: false, AllowBuild: false), 400),
        new("gap_to_lower_ledge",
            new GridBuilder(24, 40).Fill(0, 10, 5, 10, '#').Fill(12, 12, 23, 12, '#').Put(1, 9, 'S').Put(16, 11, 'G'),
            Real(), new(AllowDig: false, AllowBuild: false), 400),
        new("platform_above",
            new GridBuilder(14, 10).Fill(0, 9, 13, 9, '#').Fill(4, 5, 9, 5, '=').Put(0, 8, 'S').Put(6, 4, 'G'),
            Real(), new(AllowDig: false, AllowBuild: false), 400),
        new("platform_drop_through",
            new GridBuilder(12, 10).Fill(0, 4, 11, 4, '=').Fill(0, 9, 11, 9, '#').Put(1, 3, 'S').Put(7, 8, 'G'),
            Real(), new(AllowDig: false, AllowBuild: false), 300),
        new("dig_through_wall",
            new GridBuilder(16, 14).Fill(0, 13, 15, 13, '#').Fill(7, 0, 7, 12, '#').Put(1, 12, 'S').Put(12, 12, 'G'),
            Real(pick: 35), new(AllowBuild: false), 900),
        new("dig_down_shaft",
            new GridBuilder(20, 20).Fill(0, 6, 19, 19, '#').Fill(0, 0, 0, 19, 'X').Fill(19, 0, 19, 19, 'X')
                .Fill(7, 12, 13, 14, '.').Put(9, 5, 'S').Put(10, 14, 'G'),
            Real(pick: 35), new(AllowBuild: false), 1500),
        new("climb_shaft_by_building",
            new GridBuilder(14, 18).Fill(0, 5, 13, 17, '#').Fill(5, 5, 6, 14, '.').Put(5, 14, 'S').Put(9, 4, 'G'),
            Real(builds: 20), new(AllowDig: false), 1500, PickPower: 0),
        new("bridge_gap",
            new GridBuilder(26, 40).Fill(0, 10, 5, 10, '#').Fill(16, 10, 25, 10, '#').Put(1, 9, 'S').Put(19, 9, 'G'),
            Real(builds: 50), new(AllowDig: false), 1500, PickPower: 0),
        new("swim_across",
            new GridBuilder(20, 16).Fill(0, 6, 19, 15, '#').Fill(9, 0, 10, 5, '#').Fill(6, 6, 13, 10, '~')
                .Put(2, 5, 'S').Put(16, 5, 'G'),
            Real(), new(AllowDig: false, AllowBuild: false), 1200),
        new("wade_out_of_shallow_pool",
            new GridBuilder(16, 8).Fill(0, 6, 15, 7, '#').Fill(0, 5, 3, 5, '#').Fill(12, 5, 15, 5, '#')
                .Fill(4, 5, 11, 5, '~').Put(7, 5, 'S').Put(13, 4, 'G'),
            Real(), new(AllowDig: false, AllowBuild: false), 600),
        new("pool_edge_step_off_drop",
            new GridBuilder(14, 16).Fill(0, 15, 13, 15, '#').Fill(7, 6, 13, 14, '#').Fill(7, 5, 13, 5, '~')
                .Put(6, 5, 'S').Put(1, 14, 'G'),
            Real(), new(AllowDig: false, AllowBuild: false), 600),
        new("jump_out_of_puddle_pit",
            new GridBuilder(14, 10).Fill(0, 6, 13, 9, '#').Fill(6, 6, 7, 8, '.').Fill(6, 8, 7, 8, '~')
                .Put(6, 8, 'S').Put(11, 5, 'G'),
            Real(), new(AllowDig: false, AllowBuild: false), 600),
        new("two_deep_pool_by_wall",
            new GridBuilder(14, 8).Fill(0, 0, 0, 7, 'X').Fill(0, 6, 13, 7, '#').Fill(7, 4, 13, 5, '#')
                .Fill(1, 4, 6, 5, '~').Put(1, 5, 'S').Put(10, 3, 'G'),
            Real(), new(AllowDig: false, AllowBuild: false), 800),
        new("submerged_swim_out",
            new GridBuilder(14, 10).Fill(0, 0, 0, 9, 'X').Fill(0, 8, 13, 9, '#').Fill(7, 3, 13, 7, '#')
                .Fill(1, 3, 6, 7, '~').Put(2, 7, 'S').Put(10, 2, 'G'),
            Real(), new(AllowDig: false, AllowBuild: false), 1200),
        // No Flippers: walk in, sink to the bottom of a 3-deep pool (too wide to jump, pillar over the middle), walk
        // along it and jump off the bottom onto the far shore.
        new("pool_3_deep_no_flippers",
            new GridBuilder(24, 12).Fill(0, 6, 23, 11, '#').Fill(7, 6, 16, 8, '~').Fill(11, 0, 12, 4, '#')
                .Put(2, 5, 'S').Put(20, 5, 'G'),
            Real(), new(AllowDig: false, AllowBuild: false), 1500),
        new("puddle_2_deep_wade",
            new GridBuilder(30, 12).Fill(0, 6, 29, 11, '#').Fill(10, 6, 17, 7, '~').Put(3, 5, 'S').Put(25, 5, 'G'),
            Real(), new(AllowDig: false, AllowBuild: false), 1200),
        new("door_in_wall",
            new GridBuilder(30, 10).Fill(0, 8, 29, 9, '#').Fill(14, 0, 14, 4, 'X').Fill(14, 5, 14, 7, '|')
                .Put(2, 7, 'S').Put(24, 7, 'G'),
            Real(), new(AllowDig: false, AllowBuild: false), 600),
        new("out_of_house_through_door",
            new GridBuilder(34, 12).Fill(0, 9, 33, 11, '#').Fill(5, 0, 20, 3, 'X')
                .Fill(5, 4, 5, 5, 'X').Fill(5, 6, 5, 8, '|').Fill(20, 4, 20, 5, 'X').Fill(20, 6, 20, 8, '|')
                .Put(10, 8, 'S').Put(27, 8, 'G'),
            Real(), new(AllowDig: false, AllowBuild: false), 600),
        // World gen smooths hills into 45° slopes: walk up one (no jumps, the slopes lift the hitbox), across the
        // top and down the other side.
        new("slope_hill",
            SlopeHill(), Real(), new(AllowDig: false, AllowBuild: false), 600),
        // Half blocks: a half-tile step up, a full block on top of one (another half tile), and down again.
        new("half_block_steps",
            new GridBuilder(40, 10).Fill(0, 8, 39, 9, '#').Fill(10, 7, 12, 7, '_').Fill(13, 7, 15, 7, '#').Fill(13, 6, 14, 6, '_')
                .Put(2, 7, 'S').Put(30, 7, 'G'),
            Real(), new(AllowDig: false, AllowBuild: false), 600),
        // The in-game hillside: a 6-row stone diagonal (rows 772-777 there) with slopes on every step, walked up.
        new("in_game_diagonal_up",
            InGameDiagonal().Put(2, 13, 'S').Put(30, 7, 'G'), Real(), new(AllowDig: false, AllowBuild: false), 600),
        new("in_game_diagonal_down",
            InGameDiagonal().Put(30, 7, 'S').Put(2, 13, 'G'), Real(), new(AllowDig: false, AllowBuild: false), 600),
    }.ToDictionary(s => s.Name);

    /// <summary>A 45° hill of slopes on flat ground: up 6 rows ('/' high at the right), a 4-wide top, down 6 rows.</summary>
    internal static GridBuilder SlopeHill()
    {
        var b = new GridBuilder(50, 20).Fill(0, 16, 49, 19, '#');
        for (int i = 0; i < 6; i++)
        {
            b.Put(10 + i, 15 - i, '/').Fill(10 + i, 16 - i, 10 + i, 15, '#');
            b.Put(25 - i, 15 - i, '\\').Fill(25 - i, 16 - i, 25 - i, 15, '#');
        }
        b.Fill(16, 10, 19, 15, '#');
        return b.Put(3, 15, 'S').Put(35, 15, 'G');
    }

    /// <summary>Flat stone at row 14, a 6-row diagonal of sloped stone (columns 8-13) up to a flat top at row 8.</summary>
    internal static GridBuilder InGameDiagonal()
    {
        var b = new GridBuilder(40, 18).Fill(0, 14, 39, 17, '#').Fill(14, 8, 39, 13, '#');
        for (int i = 0; i < 6; i++) b.Put(8 + i, 13 - i, '/').Fill(8 + i, 14 - i, 8 + i, 13, '#');
        return b;
    }

    /// <summary>Rolling terrain: 1-, 2- and 3-tile steps up, then down again.</summary>
    private static GridBuilder Stairs()
    {
        var b = new GridBuilder(60, 16).Fill(0, 14, 59, 15, '#');
        int[] heights = { 0, 0, 0, 1, 1, 3, 3, 3, 4, 6, 6, 6, 6, 5, 5, 3, 3, 2, 0, 0, 0 };
        for (int i = 0; i < heights.Length; i++)
            if (heights[i] > 0) b.Fill(i * 2 + 6, 14 - heights[i], i * 2 + 7, 13, '#');
        return b.Put(1, 13, 'S').Put(52, 13, 'G');
    }

    /// <summary>Seeded random rolling terrain: steps of up to ±4, shallow pits, floating platforms.</summary>
    private static GridBuilder RandomTerrain(int seed)
    {
        var rng = new Random(seed);
        const int w = 70, h = 30;
        var b = new GridBuilder(w, h);
        int ground = 20;
        int[] top = new int[w];
        for (int x = 0; x < w; x++)
        {
            if (x > 4 && x < w - 6 && x % 2 == 0)
            {
                int r = rng.Next(10);
                if (r < 3) ground += rng.Next(-4, 5);
                ground = Math.Clamp(ground, 12, 26);
            }
            top[x] = ground;
            b.Fill(x, ground, x, h - 1, '#');
        }
        for (int i = 0; i < 3; i++)
        {
            int px = rng.Next(8, w - 14), pw = rng.Next(2, 5);
            if (rng.Next(2) == 0) b.Fill(px, top[px] - 1, px + pw - 1, h - 2, '.'); // a pit, keeping a floor
            else b.Fill(px, top[px] - rng.Next(4, 6), px + pw + 2, top[px] - rng.Next(4, 6), '=');
        }
        // start/goal on the surface
        int gx = w - 4;
        int gy = 0;
        while (gy < h - 1 && b.Rows[gy + 1][gx] != '#' && b.Rows[gy + 1][gx + 1] != '#') gy++;
        int sy = 0;
        while (sy < h - 1 && b.Rows[sy + 1][1] != '#' && b.Rows[sy + 1][2] != '#') sy++;
        return b.Put(1, sy, 'S').Put(gx, gy, 'G');
    }

    /// <summary>Seeded underground: solid rock with random-walk tunnels (2-4 high), a few caves, some water.</summary>
    private static GridBuilder RandomCave(int seed)
    {
        var rng = new Random(seed * 7919);
        const int w = 60, h = 40;
        var b = new GridBuilder(w, h).Fill(0, 0, w - 1, h - 1, '#');
        int x = 2, y = 20;
        int sx = x, sy = y;
        while (x < w - 5)
        {
            int height = rng.Next(3, 5);
            b.Fill(x, y - height + 1, Math.Min(w - 2, x + 1), y, '.');
            int r = rng.Next(10);
            if (r < 2 && y > 8) y -= 1;
            else if (r < 4 && y < h - 4) y += 1;
            else if (r == 4 && y < h - 8) y += rng.Next(2, 5); // a drop
            x += 1;
        }
        int gx = w - 5, gy = y;
        for (int i = 0; i < 3; i++)
        {
            int cx = rng.Next(5, w - 10), cy = rng.Next(8, h - 8);
            b.Fill(cx, cy, cx + rng.Next(3, 7), cy + rng.Next(2, 5), '.');
        }
        if (rng.Next(3) == 0)
        {
            int px = rng.Next(10, w - 15);
            // a water pocket in the tunnel floor
            for (int i = 0; i < 4; i++)
            {
                var rows = b.Rows;
                int fy = 0;
                while (fy < h - 1 && rows[fy][px + i] == '#') fy++;
                while (fy < h - 1 && rows[fy + 1][px + i] == '.') fy++;
                if (fy < h - 2) b.Fill(px + i, fy, px + i, fy, '~');
            }
        }
        // S and G stand on the tunnel floor
        b.Fill(sx, sy - 2, sx + 1, sy, '.');
        b.Fill(gx, gy - 2, gx + 1, gy, '.');
        b.Fill(sx, sy + 1, sx + 1, sy + 1, '#');
        b.Fill(gx, gy + 1, gx + 1, gy + 1, '#');
        return b.Put(sx, sy, 'S').Put(gx, gy, 'G');
    }

    public static IEnumerable<object[]> CaveSeeds() => Enumerable.Range(1, 100).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(CaveSeeds))]
    public void RandomCave_Arrives(int seed)
    {
        var b = RandomCave(seed);
        output.WriteLine(string.Join("\n", b.Rows));
        var sc = new Scenario($"cave_{seed}", b, Real(pick: 35, builds: 30), new(), 4000);
        Run(sc, false, maxReplans: 3);
        Run(sc, true, maxReplans: 3);
    }

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 100).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RandomTerrain_Arrives(int seed)
    {
        var b = RandomTerrain(seed);
        output.WriteLine(string.Join("\n", b.Rows));
        var sc = new Scenario($"random_{seed}", b, Real(pick: 35, builds: 30), new(), 3000);
        Run(sc, false, maxReplans: 3);
        Run(sc, true, maxReplans: 3);
    }

    /// <summary>World-gen-like smoothing: a block with air above, air on one side and a block on the other (and a block
    /// below) becomes a floor slope high toward the block; the same upside down makes ceiling slopes.</summary>
    internal static GridBuilder Smooth(GridBuilder b)
    {
        var rows = b.Rows;
        int h = rows.Length, w = rows[0].Length;
        char At(int x, int y) => x < 0 || y < 0 || x >= w || y >= h ? '#' : rows[y][x];
        bool Air(int x, int y) => At(x, y) is '.' or 'S' or 'G';
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (rows[y][x] != '#') continue;
                bool l = Air(x - 1, y), r = Air(x + 1, y);
                if (l == r) continue;
                if (Air(x, y - 1) && At(x, y + 1) == '#') b.Put(x, y, l ? '/' : '\\');
                else if (Air(x, y + 1) && At(x, y - 1) == '#') b.Put(x, y, l ? 'q' : 'r');
            }
        return b;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RandomSlopedTerrain_Arrives(int seed)
    {
        var b = Smooth(RandomTerrain(seed));
        output.WriteLine(string.Join("\n", b.Rows));
        var sc = new Scenario($"sloped_{seed}", b, Real(pick: 35, builds: 30), new(), 3000);
        Run(sc, false, maxReplans: 3);
        Run(sc, true, maxReplans: 3);
    }

    [Theory]
    [MemberData(nameof(CaveSeeds))]
    public void RandomSlopedCave_Arrives(int seed)
    {
        var b = Smooth(RandomCave(seed));
        output.WriteLine(string.Join("\n", b.Rows));
        var sc = new Scenario($"sloped_cave_{seed}", b, Real(pick: 35, builds: 30), new(), 4000);
        Run(sc, false, maxReplans: 3);
        Run(sc, true, maxReplans: 3);
    }

    public static IEnumerable<object[]> Cases() =>
        Scenarios.Keys.SelectMany(n => new[] { new object[] { n, false }, new object[] { n, true } });

    [Theory]
    [MemberData(nameof(Cases))]
    public void ArrivesWithoutHelp(string name, bool mirrored) => Run(Scenarios[name], mirrored);

    /// <param name="maxReplans">0 for the hand-written scenarios (the plan must just work). The fuzz allows a few,
    /// replanning from where the player stands exactly like MoveToTask does, to exercise that loop too.</param>
    private void Run(Scenario sc, bool mirrored, int maxReplans = 0)
    {
        var rows = sc.Grid.Rows;
        var (g, m) = ArrayTileGrid.Parse(mirrored ? rows.Select(Mirror).ToArray() : rows);
        TilePoint start = m['S'], goal = m['G'];
        if (mirrored)
        {
            // markers sit on the feet-left tile; mirrored, the char is on the feet-right tile
            start = start with { X = start.X - 1 };
            goal = goal with { X = goal.X - 1 };
        }
        var protectedTiles = AllTiles(g).Where(p => g.Get(p).Protected).ToHashSet();

        var plan = Plan(g, start, goal, sc);
        var plannedDigs = new HashSet<TilePoint>();
        bool doorsPlanned = false;
        void NotePlan(PathResult p)
        {
            foreach (var s in p.Steps.Where(s => s.Kind == MoveKind.Dig)) plannedDigs.Add(s.WorkTile!.Value);
            doorsPlanned |= p.Steps.Any(s => s.Kind == MoveKind.OpenDoor);
        }
        NotePlan(plan);

        var sim = new MiniPhysics(g, start) { PickPower = sc.PickPower, Platforms = sc.Platforms };
        var follower = new PathFollower(plan.Steps, start: start);
        var trace = new Queue<string>();
        FollowerOutput o = default;
        int tick = 0, replans = 0;
        for (; tick < sc.MaxTicks; tick++)
        {
            o = follower.Tick(sim.Kinematics, g);
            if (o.Status == FollowerStatus.Arrived) break;
            Remember(trace, $"{sim} | {Describe(o)}");
            if (o.Status != FollowerStatus.Running)
            {
                if (replans++ >= maxReplans) Fail(trace, $"follower reported {o.Status} at step {Describe(o)}");
                output.WriteLine($"replan #{replans} at tick {tick} from {sim.StandingTile}: {Describe(o)}");
                plan = Plan(g, sim.StandingTile, goal, sc);
                NotePlan(plan);
                follower = new PathFollower(plan.Steps, start: sim.StandingTile);
                o = follower.Tick(sim.Kinematics, g);
                if (o.Status == FollowerStatus.Arrived) break;
            }
            var tool = o.Current?.Kind switch
            {
                MoveKind.Dig => SimTool.Pickaxe,
                MoveKind.BuildPlatform or MoveKind.BuildDown => SimTool.Platform,
                _ => SimTool.None,
            };
            sim.Step(o.Intent, tool);
            if (sim.Hurt != null) Fail(trace, sim.Hurt);
        }
        if (o.Status != FollowerStatus.Arrived) Fail(trace, $"timed out after {sc.MaxTicks} ticks");
        output.WriteLine($"arrived in {tick} ticks; mined {sim.Mined.Count}, placed {sim.Placed.Count}, max fall {sim.MaxFallTiles}, replans {replans}");

        // Let the player settle: it must stay where the plan ends (no sliding off an edge).
        for (int i = 0; i < 40; i++) sim.Step(default);
        Assert.Null(sim.Hurt);
        Assert.Equal(plan.Steps.Count > 0 ? plan.Steps[^1].Pos : sim.StandingTile, sim.StandingTile);
        Assert.Equal(0, sim.Misuse);
        Assert.All(sim.Mined, t => Assert.Contains(t, plannedDigs));
        Assert.All(sim.Mined, t => Assert.DoesNotContain(t, protectedTiles));
        if (doorsPlanned)
        {
            output.WriteLine($"doors opened {sim.DoorsOpened}, closed {sim.DoorsClosed}");
            Assert.True(sim.DoorsOpened > 0);
            if (sc.Options.CloseDoorsBehind) Assert.False(sim.AnyDoorOpen, "a door the path opened was left open");
        }
    }

    /// <summary>A row read right to left; floor slopes swap their high side.</summary>
    internal static string Mirror(string row) =>
        new(row.Reverse().Select(c => c switch { '/' => '\\', '\\' => '/', 'r' => 'q', 'q' => 'r', _ => c }).ToArray());

    private PathResult Plan(ArrayTileGrid g, TilePoint from, TilePoint goal, Scenario sc)
    {
        var plan = Pathfinder.FindPath(g, from, goal, sc.Profile, sc.Options);
        Assert.True(plan.Success, plan.FailureReason);
        output.WriteLine($"plan ({plan.Steps.Count}): " + string.Join(" ", plan.Steps.Select(s =>
            $"{s.Kind}{s.Pos}{(s.WorkTile is { } w ? "w" + w : "")}")));
        return plan;
    }

    private static IEnumerable<TilePoint> AllTiles(ArrayTileGrid g)
    {
        for (int y = 0; y < g.Height; y++)
            for (int x = 0; x < g.Width; x++)
                yield return new TilePoint(x, y);
    }

    private static string Describe(FollowerOutput o)
    {
        var i = o.Intent;
        string keys = (i.Left ? "L" : "") + (i.Right ? "R" : "") + (i.Jump ? "J" : "") + (i.Down ? "D" : "") +
                      (i.UseItem ? $"U{i.CursorTile}" : "") + (i.Interact is { } d ? $"I{d}" : "");
        return $"{o.Status} {o.Current?.Kind}{o.Current?.Pos} [{keys}]";
    }

    private static void Remember(Queue<string> trace, string line)
    {
        trace.Enqueue(line);
        if (trace.Count > 60) trace.Dequeue();
    }

    private void Fail(Queue<string> trace, string why)
    {
        foreach (var l in trace) output.WriteLine(l);
        Assert.Fail(why);
    }
}
