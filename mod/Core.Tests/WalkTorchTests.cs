using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class WalkTorchTests
{
    private static readonly ReachSpec Vanilla = new(5, 4, 0);

    private static WalkTorchSense Dark(MoveKind? step = MoveKind.Walk) =>
        new(HasTorch: true, OnGround: true, InLiquid: false, Step: step, Underground: true, Digging: false,
            BodyBrightness: 0.02f, NearestTorch: null, ProtectedArea: false);

    [Fact]
    public void DarkUnderground_NoTorchNearby_Places() => Assert.Null(WalkTorch.Blocker(Dark()));

    [Theory]
    [InlineData(MoveKind.Walk)]
    [InlineData(MoveKind.StepUp)]
    [InlineData(MoveKind.Dig)]
    public void SteadySteps_Allow(MoveKind k) => Assert.Null(WalkTorch.Blocker(Dark(k)));

    [Fact]
    public void BetweenSteps_Allows() => Assert.Null(WalkTorch.Blocker(Dark(step: null)));

    [Theory]
    [InlineData(MoveKind.Jump)]
    [InlineData(MoveKind.Fall)]
    [InlineData(MoveKind.DropThrough)]
    [InlineData(MoveKind.Swim)]
    [InlineData(MoveKind.BuildPlatform)]
    [InlineData(MoveKind.BuildDown)]
    [InlineData(MoveKind.OpenDoor)]
    [InlineData(MoveKind.CloseDoor)]
    public void BusySteps_Wait(MoveKind k) => Assert.Equal("busy with a step", WalkTorch.Blocker(Dark(k)));

    [Fact]
    public void Refusals()
    {
        Assert.Equal("no torches", WalkTorch.Blocker(Dark() with { HasTorch = false }));
        Assert.Equal("protected area", WalkTorch.Blocker(Dark() with { ProtectedArea = true }));
        Assert.Equal("not standing", WalkTorch.Blocker(Dark() with { OnGround = false }));
        Assert.Equal("not standing", WalkTorch.Blocker(Dark() with { InLiquid = true }));
        Assert.Equal("surface", WalkTorch.Blocker(Dark() with { Underground = false }));
        Assert.Equal("torch nearby", WalkTorch.Blocker(Dark() with { NearestTorch = 7 }));
        Assert.Equal("light enough", WalkTorch.Blocker(Dark() with { BodyBrightness = 0.5f }));
    }

    [Fact]
    public void DugTunnel_NearTheSurface_Places() =>
        Assert.Null(WalkTorch.Blocker(Dark() with { Underground = false, Digging = true }));

    [Fact]
    public void TorchBeyondTheSpacing_DoesNotCount() =>
        Assert.Null(WalkTorch.Blocker(Dark() with { NearestTorch = TorchPlanner.SpacingTiles + 1 }));

    [Fact]
    public void Pacing_ChecksEveryFewTicks_AndSpacesTorches()
    {
        var w = new WalkTorch();
        Assert.True(w.Due());
        for (int i = 0; i < WalkTorch.CheckTicks; i++) Assert.False(w.Due());
        Assert.True(w.Due());
        w.OnPlaced();
        Assert.Equal(1, w.Placed);
        int waited = 0;
        while (!w.Due()) waited++;
        Assert.Equal(WalkTorch.MinGapTicks, waited);
    }

    [Fact]
    public void RepeatedFailures_BackOff()
    {
        var w = new WalkTorch();
        w.Due();
        for (int i = 0; i < WalkTorch.MaxFailuresInARow; i++) w.OnFailed();
        int waited = 0;
        while (!w.Due()) waited++;
        Assert.Equal(WalkTorch.BackoffTicks, waited);
        Assert.Equal(0, w.Placed);
    }

    private static (ArrayTileGrid G, Func<TilePoint, TorchCell> Cell) Tunnel()
    {
        var g = ArrayTileGrid.Parse(
            "################",
            "#..............#",
            "#..............#",
            "#..............#",
            "################").Grid;
        return (g, t => new TorchCell(g.Get(t).Kind == TileKind.Air, false));
    }

    private static List<PathStep> WalkRight(int fromX, int toX, int y = 3)
    {
        var steps = new List<PathStep>();
        for (int x = fromX + 1; x <= toX; x++) steps.Add(new PathStep(new TilePoint(x, y), MoveKind.Walk));
        return steps;
    }

    [Fact]
    public void Spot_AimsAheadAlongThePath()
    {
        var (g, cell) = Tunnel();
        var spot = WalkTorch.Spot(g, cell, new TilePoint(4, 3), Vanilla, WalkRight(4, 12));
        Assert.NotNull(spot);
        Assert.True(spot!.Value.X > 5, $"expected a torch ahead (right), got {spot}");

        var left = WalkTorch.Spot(g, cell, new TilePoint(10, 3), Vanilla, new[]
        {
            new PathStep(new TilePoint(9, 3), MoveKind.Walk), new PathStep(new TilePoint(8, 3), MoveKind.Walk),
            new PathStep(new TilePoint(7, 3), MoveKind.Walk), new PathStep(new TilePoint(6, 3), MoveKind.Walk),
        });
        Assert.True(left!.Value.X < 10, $"expected a torch ahead (left), got {left}");
    }

    [Fact]
    public void Spot_NoPath_FallsBackToTheHead()
    {
        var (g, cell) = Tunnel();
        Assert.Equal(TorchPlanner.BestSpot(g, cell, new TilePoint(5, 3), Vanilla), WalkTorch.Spot(g, cell, new TilePoint(5, 3), Vanilla, Array.Empty<PathStep>()));
    }

    [Fact]
    public void Spot_NeverWhereThePathBuildsAPlatform()
    {
        var (g, cell) = Tunnel();
        var want = WalkTorch.Spot(g, cell, new TilePoint(4, 3), Vanilla, WalkRight(4, 12))!.Value;
        var steps = WalkRight(4, 12);
        steps.Insert(0, new PathStep(new TilePoint(4, 3), MoveKind.BuildPlatform, want));
        var spot = WalkTorch.Spot(g, cell, new TilePoint(4, 3), Vanilla, steps);
        Assert.NotNull(spot);
        Assert.NotEqual(want, spot);
    }

    [Fact]
    public void Spot_NotAnchoredToABlockAboutToBeDug()
    {
        // A lone block the path digs next: the cells around it are anchored only by it (the torch would drop).
        var g = ArrayTileGrid.Parse(
            "..........",
            "..........",
            "......#...",
            "..........",
            "..........",
            "##########").Grid;
        Func<TilePoint, TorchCell> cell = t => new TorchCell(g.Get(t).Kind == TileKind.Air, false);
        var stand = new TilePoint(4, 4);
        var dig = new TilePoint(6, 2);
        Assert.Equal(new TilePoint(6, 1), TorchPlanner.BestSpot(g, cell, stand, Vanilla, focus: new TilePoint(6, 1)));
        var steps = new List<PathStep> { new(stand, MoveKind.Dig, dig), new(new TilePoint(6, 2), MoveKind.Walk) };
        var spot = WalkTorch.Spot(g, cell, stand, Vanilla, steps);
        Assert.NotNull(spot);
        Assert.DoesNotContain(spot!.Value, new[] { new TilePoint(6, 1), new TilePoint(5, 2), new TilePoint(7, 2), dig });
        Assert.Equal(4, spot.Value.Y); // anchored by the floor instead
    }

    [Fact]
    public void Spot_SkipsCellsTheGameRefuses()
    {
        var (g, _) = Tunnel();
        var bad = new HashSet<TilePoint>();
        TorchCell Cell(TilePoint t) => new(g.Get(t).Kind == TileKind.Air && !bad.Contains(t), false);
        var first = WalkTorch.Spot(g, Cell, new TilePoint(4, 3), Vanilla, WalkRight(4, 12))!.Value;
        bad.Add(first);
        var second = WalkTorch.Spot(g, Cell, new TilePoint(4, 3), Vanilla, WalkRight(4, 12));
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }
}
