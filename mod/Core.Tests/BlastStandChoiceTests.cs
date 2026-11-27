using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;

namespace LlmPlayer.Core.Tests;

/// <summary>
/// blast never walks at the same unreachable throwing spot again. Seen in game: blast {x:7523,y:677} failed eight calls
/// in a row with "can't reach the throwing spot (7533,675): unreachable: searched 450 nodes": the planner picked stands
/// by distance only and forgot every failed walk.
/// </summary>
public class BlastStandChoiceTests
{
    private static readonly ExplosiveSpec Sticky = Explosives.Spec(ExplosiveKind.StickyBomb);
    private static readonly TilePoint Target = new(30, 20);

    private static ArrayTileGrid Cavern(int width = 60) =>
        ArrayTileGrid.Parse(Enumerable.Range(0, 35).Select(y => y >= 20 ? new string('#', width) : new string('.', width)).ToArray()).Grid;

    private static int Chebyshev(TilePoint a, TilePoint b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    // ---- memory ------------------------------------------------------------------------------------------------

    [Fact]
    public void Memory_BlocksTheStandAndItsNeighbours_UntilItExpires()
    {
        var m = new BlastStandMemory(ttlTicks: 1000);
        m.UseWorld("w");
        var s = new TilePoint(7533, 675);
        m.WalkFailed(s, now: 100);
        Assert.True(m.Blocked(s, 200));
        Assert.True(m.Blocked(new TilePoint(7535, 673), 200));  // 2 tiles away: the same ledge
        Assert.False(m.Blocked(new TilePoint(7536, 675), 200)); // 3 tiles away
        Assert.False(m.Blocked(s, 1100));                       // expired
        Assert.Equal(0, m.Count);
    }

    [Fact]
    public void Memory_ForgetsOnANewWorldOrAReload()
    {
        var m = new BlastStandMemory();
        m.UseWorld("a");
        m.WalkFailed(new TilePoint(5, 5), 1000);
        m.UseWorld("a");
        Assert.True(m.Blocked(new TilePoint(5, 5), 1001));
        m.UseWorld("b");
        Assert.False(m.Blocked(new TilePoint(5, 5), 1002));
        m.WalkFailed(new TilePoint(5, 5), 5000);
        Assert.False(m.Blocked(new TilePoint(5, 5), 10)); // the tick counter went back: a reload
    }

    // ---- planner: exclusions -----------------------------------------------------------------------------------

    [Fact]
    public void Plan_SkipsExcludedStands_AndPicksTheNextBest()
    {
        var g = Cavern();
        var player = new TilePoint(45, 19);
        var first = BlastPlanner.Plan(g, player, Target, Sticky, out var why);
        Assert.True(first != null, why);

        var memory = new BlastStandMemory();
        memory.UseWorld("w");
        memory.WalkFailed(first!.Stand, 10);
        var second = BlastPlanner.Plan(g, player, Target, Sticky, out why, excluded: s => memory.Blocked(s, 20));
        Assert.True(second != null, why);
        Assert.True(Chebyshev(second!.Stand, first.Stand) > BlastStandMemory.StandRadius,
            $"{second.Stand} is next to the failed stand {first.Stand}");
        Assert.True(second.Miss <= Sticky.MaxMiss);
    }

    [Fact]
    public void Plan_EverythingExcluded_SaysSo()
    {
        var plan = BlastPlanner.Plan(Cavern(), new TilePoint(45, 19), Target, Sticky, out var why, excluded: _ => true);
        Assert.Null(plan);
        Assert.Contains("walking to them failed recently", why);
    }

    // ---- planner: reachability ---------------------------------------------------------------------------------

    [Fact]
    public void Plan_DropsUnreachableStands_AndTheirNeighbours()
    {
        var g = Cavern();
        var player = new TilePoint(45, 19);
        var asked = new List<TilePoint>();
        var dropped = new List<TilePoint>();
        // The two best stands (and anything near them) can't be reached.
        var plan = BlastPlanner.Plan(g, player, Target, Sticky, out var why, reachable: s =>
        {
            asked.Add(s);
            return asked.Count > 2;
        }, unreachable: dropped);
        Assert.True(plan != null, why);
        Assert.Equal(3, asked.Count);
        Assert.Equal(2, dropped.Count);
        Assert.Equal(asked[2], plan!.Stand);
        foreach (var d in dropped)
            Assert.True(Chebyshev(plan.Stand, d) > BlastPlanner.UnreachableRadius, $"{plan.Stand} is next to unreachable {d}");
        Assert.True(Chebyshev(asked[0], asked[1]) > BlastPlanner.UnreachableRadius);
    }

    [Fact]
    public void Plan_NoReachableStand_GivesUpAfterTheLimit()
    {
        int asked = 0;
        var plan = BlastPlanner.Plan(Cavern(), new TilePoint(45, 19), Target, Sticky, out var why, reachable: _ =>
        {
            asked++;
            return false;
        });
        Assert.Null(plan);
        Assert.Equal(BlastPlanner.MaxReachChecks, asked);
        Assert.Contains("no reachable throwing spot", why);
    }

    [Fact]
    public void Plan_WithoutCallbacks_IsUnchanged()
    {
        var g = Cavern();
        var a = BlastPlanner.Plan(g, new TilePoint(45, 19), Target, Sticky, out _);
        var b = BlastPlanner.Plan(g, new TilePoint(45, 19), Target, Sticky, out _, excluded: _ => false, reachable: _ => true);
        Assert.Equal(a, b);
    }

    // ---- bounded pathfinder check ------------------------------------------------------------------------------

    /// <summary>The cavern with a 2-wide pit (x=40..41, rows 20..20+depth-1, walls unbreakable) the player stands at the
    /// bottom of: 10 deep it can't jump out (no digging, no building), 3 deep it can.</summary>
    private static (ArrayTileGrid G, TilePoint Player) PitCavern(int depth)
    {
        var rows = Enumerable.Range(0, 35).Select(y => (y >= 20 ? new string('#', 60) : new string('.', 60)).ToCharArray()).ToArray();
        int bottom = 20 + depth; // the pit's floor row
        for (int y = 20; y <= bottom; y++)
        {
            rows[y][39] = rows[y][42] = 'X';
            rows[y][40] = rows[y][41] = y < bottom ? '.' : 'X';
        }
        return (ArrayTileGrid.Parse(rows.Select(r => new string(r)).ToArray()).Grid, new TilePoint(40, bottom - 1));
    }

    private static Func<TilePoint, bool> Reach(ArrayTileGrid g, TilePoint from, List<PathResult> log)
    {
        var profile = new MovementProfile();
        var options = new PathOptions(AllowDig: false, AllowBuild: false, GoalTolerance: 0, MaxNodes: BlastPlanner.ReachNodes);
        return s =>
        {
            var r = Pathfinder.FindPath(g, from, s, profile, options);
            log.Add(r);
            return !BlastPlanner.ProvedUnreachable(r);
        };
    }

    [Fact]
    public void BoundedSearch_StuckInAPit_EveryStandProvedUnreachable()
    {
        var (g, player) = PitCavern(10);
        var log = new List<PathResult>();
        var plan = BlastPlanner.Plan(g, player, Target, Sticky, out var why, reachable: Reach(g, player, log));
        Assert.Null(plan);
        Assert.Contains("no reachable throwing spot", why);
        Assert.Equal(BlastPlanner.MaxReachChecks, log.Count);
        Assert.All(log, r => Assert.True(r.NodesExpanded < BlastPlanner.ReachNodes, $"{r.NodesExpanded} nodes"));
    }

    [Fact]
    public void BoundedSearch_AShallowPit_IsReachable()
    {
        var (g, player) = PitCavern(3);
        var log = new List<PathResult>();
        var plan = BlastPlanner.Plan(g, player, Target, Sticky, out var why, reachable: Reach(g, player, log));
        Assert.True(plan != null, why);
        Assert.Single(log);
        Assert.True(log[0].Success, log[0].FailureReason);
    }

    [Fact]
    public void ProvedUnreachable_OnlyForAnExhaustedSearch()
    {
        var none = Array.Empty<PathStep>();
        Assert.True(BlastPlanner.ProvedUnreachable(new PathResult(false, none, "unreachable: searched 450 nodes", 450)));
        Assert.False(BlastPlanner.ProvedUnreachable(new PathResult(false, none, "unreachable: searched 4000 nodes", 4000)));
        Assert.False(BlastPlanner.ProvedUnreachable(new PathResult(false, none, "goal_not_standable: …", 0)));
        Assert.False(BlastPlanner.ProvedUnreachable(new PathResult(true, none, null, 12)));
    }
}
