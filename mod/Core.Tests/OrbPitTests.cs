using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using LlmPlayer.Core.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace LlmPlayer.Core.Tests;

/// <summary>
/// Seen in game (corruption, beside a Shadow Orb): move_to / dev_plan from (5603,733) to open spots around the orb all
/// failed "unreachable: searched 20000 nodes". The orb hangs in a shaft of unbreakable rock over a deep pool; every
/// spot near it is mid-air (reached on platforms: Ebonstone walls everywhere anchor them). BuildDown was only offered
/// where the plain fall isn't safe, and a fall into water counted as safe, so the planner could only drop into the
/// pool (a non-swimmer can't climb back out: no building while wet) and searched the whole window for nothing.
/// Fixture orb2_fair: the fair-vision map, origin (5573,703); tests use world coordinates.
/// </summary>
[Collection(RealTerrainCollection.Name)]
public class OrbPitTests(ITestOutputHelper output)
{
    private const int Ox = 5573, Oy = 703;
    private static readonly TilePoint Start = W(5603, 733);

    private static TilePoint W(int x, int y) => new(x - Ox, y - Oy);

    internal static ArrayTileGrid Grid()
    {
        var g = RealTerrainFuzzTests.Grid("orb2_fair");
        for (int y = 0; y < g.Height; y++)
            for (int x = 0; x < g.Width; x++)
            {
                var t = g.Get(x, y);
                // Sloped / half blocks here are Ebonstone (pick power 65): the copper / platinum pickaxe can't dig them.
                if (t.Kind == TileKind.Solid && (t.Slope != TileSlope.None || t.HalfBlock)) g.Set(x, y, t with { PickPowerRequired = 65 });
                // Ebonstone background walls behind every air tile: any of them anchors a platform.
                else if (t.Kind == TileKind.Air) g.Set(x, y, t with { Wall = true });
            }
        // The 2x2 Shadow Orb at (5608,744): non-solid, protected, nothing can be placed into it.
        for (int dy = 0; dy < 2; dy++)
            for (int dx = 0; dx < 2; dx++)
            {
                var o = W(5608 + dx, 744 + dy);
                g.Set(o.X, o.Y, new TileInfo(TileKind.Air, int.MaxValue, Protected: true, Name: "Shadow Orb", Wall: true, Occupied: true));
            }
        return g;
    }

    /// <summary>The crop inside a PlanJob-sized window whose rest is unrevealed (as in game: the search there had the
    /// whole window to wander through).</summary>
    private static ArrayTileGrid Padded()
    {
        var g = Grid();
        var tiles = new TileInfo[PlanJob.MaxWidth, PlanJob.MaxHeight];
        int px = (PlanJob.MaxWidth - g.Width) / 2, py = (PlanJob.MaxHeight - g.Height) / 2;
        for (int y = 0; y < PlanJob.MaxHeight; y++)
            for (int x = 0; x < PlanJob.MaxWidth; x++)
            {
                int lx = x - px, ly = y - py;
                tiles[x, y] = lx >= 0 && ly >= 0 && lx < g.Width && ly < g.Height ? g.Get(lx, ly) : new TileInfo(TileKind.Unknown);
            }
        return new ArrayTileGrid(-px, -py, tiles);
    }

    private static MovementProfile Profile() =>
        JumpModel.Apply(new MovementProfile(PickPower: 59, BuildItemCount: 150), PlayerPhysics.Vanilla);

    public static IEnumerable<object[]> Goals() => new[]
    {
        // The spots tried in game, then stands on a platform at row 746 right beside the orb (hammer reach).
        new object[] { 5606, 740 }, new object[] { 5607, 741 }, new object[] { 5610, 742 }, new object[] { 5606, 743 },
        new object[] { 5606, 745 }, new object[] { 5610, 745 },
    };

    [Theory]
    [MemberData(nameof(Goals))]
    public void SpotsAroundTheOrb_AreFoundFast(int gx, int gy)
    {
        foreach (var g in new[] { Grid(), Padded() })
            Plan(g, gx, gy);
    }

    private void Plan(ArrayTileGrid g, int gx, int gy)
    {
        var r = Pathfinder.FindPath(g, Start, W(gx, gy), Profile(), new PathOptions());
        output.WriteLine($"{r.FailureReason} {r.NodesExpanded} nodes -> {r.ResolvedGoal}: " +
                         string.Join(" ", r.Steps.Select(st => $"{st.Kind}{st.Pos}w{st.WorkTile}")));
        Assert.True(r.Success, r.FailureReason);
        Assert.True(r.NodesExpanded < 2000, $"{r.NodesExpanded} nodes");
        // Never through the pool: a non-swimmer couldn't get back up.
        Assert.DoesNotContain(r.Steps, st => st.Kind == MoveKind.Swim);
    }

    /// <summary>The whole move_to loop (PlanJob, follower, replans) in MiniPhysics: down the shaft on platforms to a
    /// stand beside the orb over the water, unhurt and dry.</summary>
    [Theory]
    [InlineData(5606, 745)]
    [InlineData(5610, 745)]
    public void WalksDownToAStandBesideTheOrb(int gx, int gy)
    {
        var g = Grid();
        var sim = new MiniPhysics(g, Start) { PickPower = 59, Platforms = 150 };
        var move = new MoveToSim(g, sim, W(gx, gy), Profile(), new PathOptions(), log: output.WriteLine);
        var outcome = move.Run(6000);
        if (outcome != MoveOutcome.Arrived)
            foreach (var l in move.Trace) output.WriteLine(l);
        output.WriteLine($"{outcome}: {move.Reason} after {sim.Ticks} ticks, {move.Replans} replans");
        Assert.Equal(MoveOutcome.Arrived, outcome);
        Assert.Null(sim.Hurt);
    }

    /// <summary>The pool beyond reach of anything dry (no platforms): fails, and fast — the only way down is the
    /// water, which leaves no way back up.</summary>
    [Fact]
    public void WithoutPlatforms_FailsWithoutBurningTheBudget()
    {
        var p = JumpModel.Apply(new MovementProfile(PickPower: 59, BuildItemCount: 0), PlayerPhysics.Vanilla);
        var r = Pathfinder.FindPath(Grid(), Start, W(5606, 745), p, new PathOptions());
        output.WriteLine($"{r.FailureReason} {r.NodesExpanded}");
        Assert.False(r.Success);
    }
}
