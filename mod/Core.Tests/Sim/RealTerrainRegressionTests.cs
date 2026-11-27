using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;
using Xunit.Abstractions;

namespace LlmPlayer.Core.Tests.Sim;

/// <summary>
/// Failures the real-terrain fuzz (<see cref="RealTerrainFuzzTests"/>) found, each pinned on a crop of the fixture it
/// happened in, walked with the move_to loop (<see cref="MoveToSim"/>) in <see cref="MiniPhysics"/>.
/// Coordinates are the fixture's (the crop keeps them: everything outside it reads as unbreakable rock).
/// </summary>
[Collection(RealTerrainCollection.Name)]
public class RealTerrainRegressionTests(ITestOutputHelper output)
{
    /// <summary>The fixture with everything outside [x0..x1] x [y0..y1] turned to unbreakable rock.</summary>
    private static ArrayTileGrid Crop(string fixture, int x0, int y0, int x1, int y1)
    {
        var rows = RealTerrainFuzzTests.Rows(fixture)
            .Select((r, y) => new string(r.Select((c, x) => x >= x0 && x <= x1 && y >= y0 && y <= y1 ? c : 'X').ToArray()))
            .ToArray();
        return ArrayTileGrid.Parse(rows).Grid;
    }

    private MoveToSim Walk(ArrayTileGrid g, TilePoint start, TilePoint goal, int maxTicks, out MoveOutcome outcome,
        out MiniPhysics sim)
    {
        sim = new MiniPhysics(g, start) { PickPower = 35, Platforms = 150 };
        var move = new MoveToSim(g, sim, goal, RealTerrainFuzzTests.Profile(), new PathOptions(), log: output.WriteLine);
        outcome = move.Run(maxTicks);
        if (outcome != MoveOutcome.Arrived)
            foreach (var l in move.Trace) output.WriteLine(l);
        output.WriteLine($"{outcome}: {move.Reason} after {sim.Ticks} ticks, {move.Replans} replans");
        return move;
    }

    private void ArrivesFirstTry(string fixture, int x0, int y0, int x1, int y1, TilePoint start, TilePoint goal, int maxTicks = 6000)
    {
        var move = Walk(Crop(fixture, x0, y0, x1, y1), start, goal, maxTicks, out var outcome, out var sim);
        Assert.Equal(MoveOutcome.Arrived, outcome);
        Assert.Null(sim.Hurt);
        Assert.Equal(0, move.Replans);
        Assert.Equal(0, move.FailedPlans);
    }

    /// <summary>Seen in game (west caves, 3910,926): standing inside the bottom platform of a pillar, the plan walked onto
    /// the low side of a floor slope, then "walked" back onto the platform one row up. Feet on the slope's low side are
    /// below the platform's top, and vanilla never lifts the body onto a platform from inside it: "stuck at Walk"
    /// (repeatedly). Origin of the crop: world (3895,900).</summary>
    [Fact]
    public void PlatformAboveSunkFeet_IsNotAWalk()
    {
        string[] rows =
        {
            "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
            "......##==...........#############..",
            "...............=....#####...........",
            "...............=....######..........",
            "..............===...#######.........",
            "...............=....######..........",
            "...............=.../######\\.........",
            "...............=./##########........",
            "..........###################.......",
            "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
        };
        var g = ArrayTileGrid.Parse(rows).Grid;
        var r = Pathfinder.FindPath(g, new(15, 7), new(15, 6), RealTerrainFuzzTests.Profile(), new PathOptions(GoalTolerance: 0));
        Assert.True(r.Success, r.FailureReason);
        var from = new TilePoint(15, 7);
        foreach (var st in r.Steps)
        {
            output.WriteLine($"{st.Kind} {st.Pos}");
            // Feet on the slope's low side (16,6 / 17,7 region) can't walk onto the platform at (15,7).
            Assert.False(st.Kind == MoveKind.Walk && st.Pos == new TilePoint(15, 6) && from == new TilePoint(16, 6),
                "walked from the slope's low side onto the platform");
            from = st.Pos;
        }
    }

    /// <summary>Seen in game (west caves, 3952,947): a non-swimmer on the bottom at the edge of a pool, the exit a
    /// half block and a ledge up to the right: stalled 20 s. Origin of the crop: world (3935,930).</summary>
    [Fact]
    public void PoolEdgeExitUpRight()
    {
        string[] rows =
        {
            "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
            "................##._####r................",
            "................#.####r..................",
            ".............................#...........",
            ".............................#.......##..",
            ".............................#......####.",
            "_##_.........................#...../#####",
            "####.........................#..../######",
            "####################.........#....#######",
            "#####........................#._#########",
            "####.........................############",
            "#####....................../#############",
            "####..................../################",
            "####~~~~~~~~~~~~~~~~~~~~#################",
            "####~~~~~~~~~~~~~~~~~~~~#################",
            "####\\~~~~~~~~~~~~~~~~.._#################",
            "######~~~~~~~~~~~~~~~..P#################",
            "#####~~~~~~~~~~~~~~_#PP###########.......",
            "######~~~~~~~~~~~~################.......",
            "####r~~~~~~~~~~~~################........",
            "###~~/##~~~~~~~####.#############....##..",
            "##~_#############.#.#############...####\\",
            "################......############.######",
            "##############r........##########...#####",
            "##############.........###########...q###",
            "#############..........############_...##",
            "#############.............###########_..#",
            "############..............############\\.#",
            "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX"

        };
        var g = ArrayTileGrid.Parse(rows).Grid;
        var move = Walk(g, new(17, 17), new(26, 11), 3000, out var outcome, out var sim);
        Assert.Equal(MoveOutcome.Arrived, outcome);
        Assert.Null(sim.Hurt);
    }

    /// <summary>Feet on a half block, a full block one row up beside it: 1.5 tiles, more than vanilla's auto step-up.
    /// The planner used a StepUp and the player stood against the step until Stuck, five times over; now it jumps.</summary>
    [Fact]
    public void StepUpFromHalfBlock_IsAJump() =>
        ArrivesFirstTry("desert_surface", 110, 35, 160, 75, new(127, 63), new(143, 49));

    /// <summary>A 2-wide shaft whose exit is an overhang on the target side: a run-up jump drifted under the overhang,
    /// bonked and fell into the pit beside it. Now the planner measures the launch level contiguously (the overhang
    /// blocks the arc above an open bottom row) and the follower takes off standing and rises inside the shaft.</summary>
    [Fact]
    public void JumpOutOfShaftUnderOverhang() =>
        ArrivesFirstTry("base_underground", 20, 120, 75, 175, new(36, 157), new(56, 138));

    /// <summary>The same rule must let go once the feet are up: landing on a slope below the launch level kept steering
    /// back toward the shaft and slid off it ("stuck at Jump", repeatedly).</summary>
    [Fact]
    public void ShaftRuleReleasesAfterTheRise() =>
        ArrivesFirstTry("base_underground", 0, 0, 299, 260, new(149, 222), new(221, 200), 12000);

    /// <summary>Cactus between source and target below the jump's launch level: the run-up carried the hitbox into it
    /// on the takeoff tick. Now the jump takes off standing at the edge and rises before drifting across.</summary>
    [Fact]
    public void JumpOverCactus_TakesOffStanding()
    {
        var move = Walk(Crop("desert_surface", 190, 15, 240, 45), new(225, 32), new(210, 35), 4000, out var outcome, out var sim);
        Assert.Null(sim.Hurt);
        Assert.Equal(MoveOutcome.Arrived, outcome);
    }

    /// <summary>A jump out of a 1-deep puddle planned with the dry reach fell short back into it, and the landing speed
    /// slid the player on into the cactus in the puddle. Jumps taking off in liquid now get less reach.</summary>
    [Fact]
    public void JumpOutOfPuddle_PlannedWithWetReach()
    {
        Walk(Crop("west_caves", 60, 235, 130, 275), new(73, 266), new(98, 254), 5000, out var outcome, out var sim);
        Assert.Null(sim.Hurt);
        Assert.Equal(MoveOutcome.Arrived, outcome);
    }

    /// <summary>A pocket a dozen tiles of rock above a cave with a flat ceiling: no step to stair-dig from, so the search
    /// ran dry ("unreachable"). Now it can dig the head room and climb a platform pillar, and climb platforms are never
    /// anchored to rock the path itself just mined (they were unplaceable).</summary>
    [Fact]
    public void DigUpOutOfFlatCeilingCave() =>
        ArrivesFirstTry("west_caves", 255, 255, 300, 315, new(283, 306), new(272, 273));

    /// <summary>A dug staircase that turns back stepped onto tiles it had mined a few steps earlier ("path changed at
    /// Dig" again and again).</summary>
    [Fact]
    public void DugStaircase_NeverStandsOnItsOwnDigs() =>
        ArrivesFirstTry("corruption_chasm", 5, 15, 60, 80, new(28, 72), new(44, 49), 8000);

    /// <summary>Landing on a slope at the bottom of a pool never reached velocity.Y == 0 in the sim (each wet tick moved
    /// half the gap to the floor; vanilla's WaterCollision moves a collided component in full), so a jump out of it
    /// never started. A sim fix; the trip itself is the regression check.</summary>
    [Fact]
    public void JumpOutOfPoolWithSlopedBottom() =>
        ArrivesFirstTry("corruption_chasm", 0, 0, 159, 130, new(46, 124), new(25, 125), 3000);

    /// <summary>The sim half of that: sinking onto the bottom of a pool lands (velocity.Y == 0) within a few ticks of
    /// touching it, instead of covering half the remaining gap each tick.</summary>
    [Fact]
    public void Sim_SinkingOntoAPoolBottom_Lands()
    {
        var (g, m) = new GridBuilder(8, 12).Fill(0, 10, 7, 11, '#').Fill(0, 2, 7, 9, '~').Put(3, 5, 'S').Parse();
        var sim = new MiniPhysics(g, m['S']); // placed 4 rows above the bottom, in the water
        int t = 0;
        while (t < 200 && !(sim.Kinematics.OnGround && sim.StandingTile.Y == 9))
        {
            sim.Step(default);
            t++;
        }
        output.WriteLine($"{sim}");
        Assert.True(t < 45, $"landed after {t} ticks"); // 38 now; 56 with the half-step landing
    }

    /// <summary>The snapshot's outside read as unbreakable rock, i.e. as floor: a plan fell onto the window's bottom
    /// edge and the follower found it unsupported ("path changed at Jump" five times without moving). Every standing
    /// step of a PlanJob plan must have real support.</summary>
    [Fact]
    public void PlanJob_NeverStandsOnTheWindowEdge()
    {
        var g = RealTerrainFuzzTests.Grid("west_caves");
        var job = new PlanJob(new(87, 318), new(155, 359), RealTerrainFuzzTests.Profile(), new PathOptions());
        while (!job.Step(g)) Thread.Yield();
        var r = job.Result!;
        Assert.True(r.Success, r.FailureReason);
        foreach (var s in r.Steps.Where(s => s.Kind is MoveKind.Walk or MoveKind.Fall or MoveKind.Jump or MoveKind.StepUp or MoveKind.DropThrough))
        {
            bool wet = Enumerable.Range(s.Pos.X, 2).Any(x => g.Get(x, s.Pos.Y).Kind == TileKind.Water);
            bool held = g.Get(s.Pos.X, s.Pos.Y + 1).IsStandable || g.Get(s.Pos.X + 1, s.Pos.Y + 1).IsStandable
                        || r.Steps.Any(b => b.Kind is MoveKind.BuildPlatform && b.WorkTile is { } w && w.Y == s.Pos.Y + 1 && w.X - s.Pos.X is 0 or 1);
            Assert.True(wet || held, $"{s} stands on nothing");
        }
    }

    /// <summary>A pocket walled by rock the copper pickaxe can't break, whose way round leaves the bounding box +
    /// margin window: the first search fails, the job retries once in the widest window and finds it.</summary>
    [Fact]
    public void PlanJob_WidensTheWindowAfterAFailedSearch()
    {
        var g = RealTerrainFuzzTests.Grid("corruption_chasm");
        var job = new PlanJob(new(130, 30), new(115, 62), RealTerrainFuzzTests.Profile(), new PathOptions());
        int ticks = 0;
        while (!job.Step(g))
        {
            ticks++;
            Thread.Yield();
        }
        Assert.True(job.Result!.Success, job.Result.FailureReason);
        Assert.False(job.IsPartial);
        // the narrow window (47 x 97 tiles) copies in 1 tick, the widest (200 x 160) in 6 more
        Assert.True(job.CopyTicks > 2, $"copied in {job.CopyTicks} ticks: never widened?");
    }
}

public class ReplanGuardTests
{
    [Fact]
    public void GivesUpOnTheSameFailureFromTheSameSpot()
    {
        var g = new ReplanGuard(maxRepeats: 3);
        var here = new TilePoint(28, 129);
        Assert.False(g.Record(here, "path changed at Jump (28, 125)"));
        Assert.False(g.Record(new TilePoint(29, 130), "path changed at Jump (28, 125)")); // elsewhere: its own count
        Assert.False(g.Record(here, "stuck at Walk (27, 124)"));                         // another failure
        Assert.False(g.Record(here, "path changed at Jump (28, 125)"));
        Assert.True(g.Record(here, "path changed at Jump (28, 125)"));
    }
}
