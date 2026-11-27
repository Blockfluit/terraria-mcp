using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;
using Xunit.Abstractions;

namespace LlmPlayer.Core.Tests.Sim;

/// <summary>
/// The pool where the character probably drowned (get_map radius 12 around (4185,651), origin (4173,639); '@' = the
/// player standing in the water). In game, plans from here jumped onto the top of the wall left of the pool
/// (x=4184, visible '#'), dug down through that wall and fell into the unknown on its far side, then failed with
/// "path changed" once the unknown cells came into view as rock. Unknown counts as passable (at a cost), so the
/// plan is consistent with what the player knows: these tests pin that (dug tiles are known solid, never water or
/// air; the jump out of the water is physically doable) so a change in liquid handling can't break it.
/// </summary>
public class InGamePoolTests(ITestOutputHelper output)
{
    private const int OriginX = 4173, OriginY = 639, Pad = 10;

    private static readonly string[] Map =
    {
        "????????????????????u####",
        "?????????????????????####",
        "?????????????????????????",
        "?????????????????????????",
        "?????????????????..??????",
        "????????????????...#????#",
        "???????????????....####?#",
        "??????????????........##?",
        "?????????????.........###",
        "???????????#............#",
        "???????????#.............",
        "???????????#~~~~~~~~~~~~~",
        "???????????#@~~~~~~~~~~~~",
        "???????????##~~~~~~~~~~~~",
        "?????????????#~~~~#~~~~~~",
        "??????????????##~###~~~~~",
        "???????????????##??##~~~~",
        "????????????????????###~~",
        "??????????????????????###",
        "????????????????????????#",
        "?????????????????????????",
        "?????????????????????????",
        "?????????????????????????",
        "?????????????????????????",
        "?????????????????????????",
    };

    /// <summary>The map padded with unknown on both sides, the player tile as water, a known floor 30 rows down.</summary>
    private static ArrayTileGrid Grid()
    {
        var rows = Map.Select(r => new string('?', Pad) + r.Replace('@', '~').Replace('u', '#') + new string('?', Pad)).ToList();
        while (rows.Count < 31) rows.Add(new string('?', rows[0].Length));
        rows[30] = new string('#', rows[0].Length);
        return ArrayTileGrid.Parse(rows.ToArray()).Grid;
    }

    private static TilePoint Local(int x, int y) => new(x - OriginX + Pad, y - OriginY);

    private static MovementProfile Profile(bool waterBreathing = false) =>
        JumpModel.Apply(new MovementProfile(PickPower: 35, BuildItemCount: 30, WaterBreathing: waterBreathing), PlayerPhysics.Vanilla);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlanFromThePool_DigsOnlyKnownRock_AndNeverStandsInRock(bool waterBreathing)
    {
        var g = Grid();
        var start = Local(4185, 651);
        Assert.Equal(TileKind.Water, g.Get(start).Kind);
        var r = Pathfinder.FindPath(g, start, Local(4175, 668), Profile(waterBreathing), new(GoalTolerance: 0));
        output.WriteLine(string.Join(" ", r.Steps.Select(s => $"{s.Kind}{s.Pos}{s.WorkTile}")));
        Assert.True(r.Success, r.FailureReason);

        var dug = new HashSet<TilePoint>();
        foreach (var s in r.Steps)
        {
            if (s.Kind == MoveKind.Dig)
            {
                var w = s.WorkTile!.Value;
                Assert.Equal(TileKind.Solid, g.Get(w).Kind); // never "digs" water, air or the unknown
                dug.Add(w);
                continue;
            }
            for (int x = s.Pos.X; x <= s.Pos.X + 1; x++)
                for (int y = s.Pos.Y - 2; y <= s.Pos.Y; y++)
                    Assert.True(!g.Get(x, y).BlocksBody || dug.Contains(new TilePoint(x, y)), $"{s.Kind}{s.Pos} overlaps rock ({x},{y})");
        }
    }

    [Fact]
    public void JumpOutOfThePool_IsFollowable()
    {
        // With the unknown being air (what the plan assumed), the follower executes the plan in the sim.
        var g = Grid();
        var start = Local(4185, 651);
        var plan = Pathfinder.FindPath(g, start, Local(4175, 668), Profile(), new(GoalTolerance: 0));
        Assert.True(plan.Success, plan.FailureReason);
        var sim = new MiniPhysics(g, start);
        var f = new PathFollower(plan.Steps, start: start);
        FollowerOutput o = default;
        for (int t = 0; t < 2000; t++)
        {
            o = f.Tick(sim.Kinematics, g);
            if (o.Status != FollowerStatus.Running) break;
            sim.Step(o.Intent, o.Current?.Kind switch
            {
                MoveKind.Dig => SimTool.Pickaxe,
                MoveKind.BuildPlatform or MoveKind.BuildDown => SimTool.Platform,
                _ => SimTool.None,
            });
        }
        Assert.Equal(FollowerStatus.Arrived, o.Status);
        Assert.Null(sim.Hurt);
        Assert.Equal(plan.Steps[^1].Pos, sim.StandingTile);
    }
}
