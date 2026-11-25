using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class PathFollowerTests
{
    // 12 wide, floor at row 5, player stands on row 4.
    private static ArrayTileGrid Flat() => ArrayTileGrid.Parse(
        "............",
        "............",
        "............",
        "............",
        "............",
        "############").Grid;

    // Ledge 3 high starting at x=3; player stands on row 5 left of it.
    private static ArrayTileGrid Ledge() => ArrayTileGrid.Parse(
        "............",
        "............",
        "............",
        "...#########",
        "...#########",
        "...#########",
        "############").Grid;

    private static PlayerKinematics At(float x, float y, bool onGround = true, float vx = 0, float vy = 0)
        => new(x, y, vx, vy, onGround);

    private static List<PathStep> WalkTo(int fromX, int toX, int y)
    {
        var steps = new List<PathStep>();
        int dir = Math.Sign(toX - fromX);
        for (int x = fromX + dir; x != toX + dir; x += dir) steps.Add(new PathStep(new TilePoint(x, y), MoveKind.Walk));
        return steps;
    }

    [Fact]
    public void WalkRight_HoldsRight()
    {
        var f = new PathFollower(WalkTo(1, 6, 4));
        var o = f.Tick(At(1, 4), Flat());
        Assert.True(o.Intent.Right);
        Assert.False(o.Intent.Left);
        Assert.Equal(FollowerStatus.Running, o.Status);
    }

    [Fact]
    public void WalkLeft_HoldsLeft()
    {
        var f = new PathFollower(WalkTo(6, 2, 4));
        var o = f.Tick(At(6, 4), Flat());
        Assert.True(o.Intent.Left);
        Assert.False(o.Intent.Right);
    }

    [Fact]
    public void WithinDeadzone_ReleasesBothDirections()
    {
        var f = new PathFollower(WalkTo(1, 3, 4));
        // 0.2 tiles short of step 0 (x=2) horizontally but airborne -> not advanced, inside deadzone
        var o = f.Tick(At(1.8f, 3.5f, onGround: false), Flat());
        Assert.False(o.Intent.Left);
        Assert.False(o.Intent.Right);
    }

    [Fact]
    public void ReachesWaypoint_Advances()
    {
        var steps = WalkTo(1, 6, 4);
        var f = new PathFollower(steps);
        int before = f.RemainingSteps;
        f.Tick(At(steps[0].Pos.X, steps[0].Pos.Y), Flat());
        Assert.True(f.RemainingSteps < before);
    }

    [Fact]
    public void Waypoint_NotAdvancedWhileAirborne()
    {
        var steps = WalkTo(1, 6, 4);
        var f = new PathFollower(steps);
        f.Tick(At(2, 4, onGround: false), Flat());
        Assert.Equal(steps.Count, f.RemainingSteps);
    }

    [Fact]
    public void JumpStep_PressesJumpWhenOnGround()
    {
        var g = Ledge();
        var f = new PathFollower(new[] { new PathStep(new TilePoint(3, 2), MoveKind.Jump) });
        var o = f.Tick(At(1, 5), g);
        Assert.True(o.Intent.Jump);
        Assert.True(o.Intent.Right);
    }

    [Fact]
    public void JumpStep_HeldWhileRisingAndReleasedAtApex()
    {
        var g = Ledge();
        var f = new PathFollower(new[] { new PathStep(new TilePoint(3, 2), MoveKind.Jump) });
        Assert.True(f.Tick(At(1, 5), g).Intent.Jump);
        Assert.True(f.Tick(At(1.1f, 4.6f, onGround: false, vy: -0.5f), g).Intent.Jump);
        Assert.True(f.Tick(At(1.2f, 4.0f, onGround: false, vy: -0.3f), g).Intent.Jump);
        // rising stopped
        Assert.False(f.Tick(At(1.3f, 3.9f, onGround: false, vy: 0.1f), g).Intent.Jump);
    }

    [Fact]
    public void JumpStep_ReleasedWhenTargetHeightReached()
    {
        var g = Ledge();
        var f = new PathFollower(new[] { new PathStep(new TilePoint(3, 2), MoveKind.Jump) });
        f.Tick(At(1, 5), g);
        var o = f.Tick(At(1.5f, 1.9f, onGround: false, vy: -0.2f), g);
        Assert.False(o.Intent.Jump);
        Assert.True(o.Intent.Right);
    }

    [Fact]
    public void JumpStep_NotPressedWhileAirborneBeforeTakeoff()
    {
        var f = new PathFollower(new[] { new PathStep(new TilePoint(5, 4), MoveKind.Jump) });
        var o = f.Tick(At(1, 3, onGround: false, vy: 0.3f), Flat());
        Assert.False(o.Intent.Jump);
    }

    [Fact]
    public void JumpStep_ReleasesOneTickAfterLandingShortThenRejumps()
    {
        var g = Ledge();
        var f = new PathFollower(new[] { new PathStep(new TilePoint(3, 2), MoveKind.Jump) });
        Assert.True(f.Tick(At(1, 5), g).Intent.Jump);
        Assert.True(f.Tick(At(1, 4.5f, onGround: false, vy: -0.4f), g).Intent.Jump);
        Assert.False(f.Tick(At(1, 4.0f, onGround: false, vy: 0.2f), g).Intent.Jump);
        // landed short: Terraria needs jump released before it can jump again
        Assert.False(f.Tick(At(1, 5), g).Intent.Jump);
        Assert.True(f.Tick(At(1, 5), g).Intent.Jump);
    }

    [Fact]
    public void DigStep_AimsAtWorkTileAndUsesItem()
    {
        var (g, _) = ArrayTileGrid.Parse(
            "............",
            "............",
            "............",
            "............",
            "............",
            "############",
            "############");
        var work = new TilePoint(2, 5);
        var f = new PathFollower(new[] { new PathStep(new TilePoint(2, 5), MoveKind.Dig, work) });
        var o = f.Tick(At(2, 4), g);
        Assert.True(o.Intent.UseItem);
        Assert.Equal(work, o.Intent.CursorTile);
        Assert.Equal(FollowerStatus.Running, o.Status);

        g.Set(2, 5, TileInfo.Air);
        g.Set(3, 5, TileInfo.Air);
        o = f.Tick(At(2, 4.5f, onGround: false, vy: 0.3f), g);
        Assert.False(o.Intent.UseItem);
        o = f.Tick(At(2, 5), g);
        Assert.Equal(FollowerStatus.Arrived, o.Status);
    }

    [Fact]
    public void DigStep_SlowBlockNotReportedStuckAtNormalBudget()
    {
        var (g, _) = ArrayTileGrid.Parse(
            "............",
            "............",
            "............",
            "...#........",
            "...#........",
            "############");
        var f = new PathFollower(new[] { new PathStep(new TilePoint(2, 4), MoveKind.Dig, new TilePoint(3, 4)) }, stuckTicks: 10);
        FollowerOutput o = default;
        for (int i = 0; i < 15; i++) o = f.Tick(At(2, 4), g);
        Assert.Equal(FollowerStatus.Running, o.Status);
        Assert.True(o.Intent.UseItem);
    }

    [Fact]
    public void ConsecutiveDigsAtSamePos_DigsAllBeforeMoving()
    {
        var (g, _) = ArrayTileGrid.Parse(
            "............",
            "............",
            "...#........",
            "...#........",
            "...#........",
            "############");
        var pos = new TilePoint(2, 4);
        var f = new PathFollower(new[]
        {
            new PathStep(pos, MoveKind.Dig, new TilePoint(3, 4)),
            new PathStep(pos, MoveKind.Dig, new TilePoint(3, 3)),
            new PathStep(pos, MoveKind.Dig, new TilePoint(3, 2)),
        });
        Assert.Equal(new TilePoint(3, 4), f.Tick(At(1, 4), g).Intent.CursorTile);
        g.Set(3, 4, TileInfo.Air);
        var o = f.Tick(At(1, 4), g);
        Assert.Equal(new TilePoint(3, 3), o.Intent.CursorTile);
        Assert.True(o.Intent.UseItem);
        Assert.False(o.Intent.Right);
    }

    [Fact]
    public void BuildStep_AimsAtWorkTileAndUsesItem()
    {
        var (g, _) = ArrayTileGrid.Parse(
            "............",
            "............",
            "............",
            "............",
            "............",
            "##...#######");
        var work = new TilePoint(2, 5);
        var f = new PathFollower(new[] { new PathStep(new TilePoint(2, 4), MoveKind.BuildPlatform, work) });
        var o = f.Tick(At(0, 4), g);
        Assert.True(o.Intent.UseItem);
        Assert.Equal(work, o.Intent.CursorTile);
        Assert.False(o.Intent.Right);

        g.Set(2, 5, new TileInfo(TileKind.Platform));
        o = f.Tick(At(0, 4), g);
        Assert.False(o.Intent.UseItem);
        Assert.True(o.Intent.Right);
    }

    [Fact]
    public void DropThrough_PressesDown()
    {
        var (g, _) = ArrayTileGrid.Parse(
            "............",
            "............",
            "====........",
            "............",
            "............",
            "############");
        var f = new PathFollower(new[] { new PathStep(new TilePoint(1, 4), MoveKind.DropThrough) });
        var o = f.Tick(At(1, 1), g);
        Assert.True(o.Intent.Down);
        Assert.Equal(FollowerStatus.Running, o.Status);
    }

    [Fact]
    public void Swim_PressesJumpToRise()
    {
        var (g, _) = ArrayTileGrid.Parse(
            "~~~~~~~~~~~~",
            "~~~~~~~~~~~~",
            "~~~~~~~~~~~~",
            "~~~~~~~~~~~~",
            "~~~~~~~~~~~~",
            "############");
        var f = new PathFollower(new[] { new PathStep(new TilePoint(4, 2), MoveKind.Swim) });
        var o = f.Tick(At(2, 4, onGround: false), g);
        Assert.True(o.Intent.Jump);
        Assert.True(o.Intent.Right);
        // reached without ground contact
        o = f.Tick(At(4.2f, 2.2f, onGround: false), g);
        Assert.Equal(FollowerStatus.Arrived, o.Status);
    }

    [Fact]
    public void NoProgress90Ticks_ReportsStuck()
    {
        var f = new PathFollower(WalkTo(1, 6, 4));
        FollowerOutput o = default;
        for (int i = 0; i < 89; i++) o = f.Tick(At(1, 4), Flat());
        Assert.Equal(FollowerStatus.Running, o.Status);
        o = f.Tick(At(1, 4), Flat());
        Assert.Equal(FollowerStatus.Stuck, o.Status);
        Assert.Equal(default, o.Intent);
    }

    [Fact]
    public void Progress_ResetsStuckCounter()
    {
        var f = new PathFollower(new[] { new PathStep(new TilePoint(10, 4), MoveKind.Walk) }, stuckTicks: 10);
        FollowerOutput o = default;
        for (int i = 0; i < 30; i++) o = f.Tick(At(0 + i * 0.2f, 4), Flat());
        Assert.Equal(FollowerStatus.Running, o.Status);
    }

    [Fact]
    public void LastStepReached_ReportsArrived()
    {
        var steps = WalkTo(1, 3, 4);
        var f = new PathFollower(steps);
        f.Tick(At(2, 4), Flat());
        var o = f.Tick(At(3, 4), Flat());
        Assert.Equal(FollowerStatus.Arrived, o.Status);
        Assert.Equal(0, f.RemainingSteps);
        Assert.Equal(default, o.Intent);
    }

    [Fact]
    public void EmptyPath_ReportsArrived()
    {
        var f = new PathFollower(Array.Empty<PathStep>());
        Assert.Equal(FollowerStatus.Arrived, f.Tick(At(1, 4), Flat()).Status);
    }

    [Fact]
    public void FastWalk_PassingWaypointStillAdvances()
    {
        var f = new PathFollower(WalkTo(1, 6, 4));
        // moving 0.7 tiles/tick: jumped from 1.9 to 2.6, past waypoint x=2
        f.Tick(At(2.6f, 4, vx: 0.7f), Flat());
        // x=2 overshot (advanced thanks to speed), x=3 within 0.5
        Assert.Equal(3, f.RemainingSteps);
    }

    [Fact]
    public void PlannedTileChanged_ReportsNeedsReplan()
    {
        var g = Flat();
        var f = new PathFollower(WalkTo(1, 6, 4));
        Assert.Equal(FollowerStatus.Running, f.Tick(At(1, 4), g).Status);
        g.Set(4, 5, TileInfo.Air);
        g.Set(5, 5, TileInfo.Air);
        var o = f.Tick(At(1, 4), g);
        Assert.Equal(FollowerStatus.NeedsReplan, o.Status);
    }

    [Fact]
    public void BodyBlockedOnPath_ReportsNeedsReplan()
    {
        var g = Flat();
        var f = new PathFollower(WalkTo(1, 6, 4));
        g.Set(4, 3, new TileInfo(TileKind.Solid));
        Assert.Equal(FollowerStatus.NeedsReplan, f.Tick(At(1, 4), g).Status);
    }

    [Fact]
    public void UnknownSupport_DoesNotTriggerReplan()
    {
        var g = Flat();
        g.Set(4, 5, new TileInfo(TileKind.Unknown));
        g.Set(5, 5, new TileInfo(TileKind.Unknown));
        var f = new PathFollower(WalkTo(1, 6, 4));
        Assert.Equal(FollowerStatus.Running, f.Tick(At(1, 4), g).Status);
    }

    [Fact]
    public void SupportProvidedByPlannedBuild_DoesNotTriggerReplan()
    {
        var (g, _) = ArrayTileGrid.Parse(
            "............",
            "............",
            "............",
            "............",
            "............",
            "###....#####");
        var f = new PathFollower(new[]
        {
            new PathStep(new TilePoint(2, 4), MoveKind.BuildPlatform, new TilePoint(3, 5)),
            new PathStep(new TilePoint(3, 4), MoveKind.BuildPlatform, new TilePoint(4, 5)),
            new PathStep(new TilePoint(4, 4), MoveKind.Walk),
        });
        Assert.Equal(FollowerStatus.Running, f.Tick(At(1, 4), g).Status);
    }
}
