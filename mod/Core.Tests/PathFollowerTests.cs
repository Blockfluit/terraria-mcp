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

    /// <summary>Player with the hitbox centred over body column x (hitbox left = x + 0.375).</summary>
    private static PlayerKinematics At(float x, float y, bool onGround = true, float vx = 0, float vy = 0)
        => new(x + PlayerPhysics.CenterOffset, y, vx, vy, onGround);

    /// <summary>Hitbox pressed against the ledge wall at x=3 (its right edge touching column 3).</summary>
    private static PlayerKinematics AgainstLedge(float y = 5, bool onGround = true, float vy = 0)
        => new(3 - PlayerPhysics.HitboxWidth, y, 0, vy, onGround);

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
    public void Settled_ReleasesBothDirections()
    {
        var f = new PathFollower(WalkTo(1, 2, 4), start: new TilePoint(1, 4));
        // centred over the only step (x=2) and still, but airborne -> not advanced, nothing to steer
        var o = f.Tick(At(2.03f, 3.5f, onGround: false), Flat());
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
        var f = new PathFollower(new[] { new PathStep(new TilePoint(3, 2), MoveKind.Jump) }, start: new TilePoint(1, 5));
        var o = f.Tick(AgainstLedge(), g);
        Assert.True(o.Intent.Jump);
        Assert.True(o.Intent.Right);
    }

    [Fact]
    public void LongJump_RunsToTheEdgeBeforeTakingOff()
    {
        // ground ends at x=3; a 6-wide gap; far side from x=10
        var (g, _) = ArrayTileGrid.Parse(
            "................",
            "................",
            "................",
            "................",
            "####......######");
        var f = new PathFollower(new[] { new PathStep(new TilePoint(9, 3), MoveKind.Jump) }, start: new TilePoint(2, 3));
        var o = f.Tick(At(2, 3), g);
        Assert.False(o.Intent.Jump);
        Assert.True(o.Intent.Right);
        // at the edge, moving: next tick the hitbox would leave the support -> jump now
        o = f.Tick(new PlayerKinematics(3.7f, 3, 0.18f, 0, true), g);
        Assert.True(o.Intent.Jump);
    }

    [Fact]
    public void JumpStep_HeldWhileRisingAndReleasedAtApex()
    {
        var g = Ledge();
        var f = new PathFollower(new[] { new PathStep(new TilePoint(3, 2), MoveKind.Jump) }, start: new TilePoint(1, 5));
        Assert.True(f.Tick(AgainstLedge(), g).Intent.Jump);
        Assert.True(f.Tick(AgainstLedge(4.6f, onGround: false, vy: -0.3f), g).Intent.Jump);
        // held past the target height: full height buys air time to get over the edge
        var o = f.Tick(AgainstLedge(1.9f, onGround: false, vy: -0.2f), g);
        Assert.True(o.Intent.Jump);
        Assert.True(o.Intent.Right);
        // rising stopped
        Assert.False(f.Tick(AgainstLedge(1.5f, onGround: false, vy: 0.1f), g).Intent.Jump);
    }

    [Fact]
    public void JumpStep_NotPressedWhileAirborneBeforeTakeoff()
    {
        var f = new PathFollower(new[] { new PathStep(new TilePoint(5, 4), MoveKind.Jump) }, start: new TilePoint(1, 4));
        var o = f.Tick(At(1, 3, onGround: false, vy: 0.3f), Flat());
        Assert.False(o.Intent.Jump);
    }

    [Fact]
    public void JumpStep_LandingBackOnSource_RetriesThenStuck()
    {
        var g = Ledge();
        var f = new PathFollower(new[] { new PathStep(new TilePoint(3, 2), MoveKind.Jump) }, start: new TilePoint(1, 5));
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            Assert.True(f.Tick(AgainstLedge(), g).Intent.Jump);
            Assert.True(f.Tick(AgainstLedge(4.5f, onGround: false, vy: -0.4f), g).Intent.Jump);
            // falling: released, so vanilla lets us jump again right on landing
            Assert.False(f.Tick(AgainstLedge(4.0f, onGround: false, vy: 0.2f), g).Intent.Jump);
        }
        var o = f.Tick(AgainstLedge(), g);
        Assert.Equal(FollowerStatus.Stuck, o.Status);
    }

    [Fact]
    public void JumpStep_LandingOffPath_NeedsReplan()
    {
        var g = Ledge();
        var f = new PathFollower(new[] { new PathStep(new TilePoint(3, 2), MoveKind.Jump) }, start: new TilePoint(1, 5));
        f.Tick(AgainstLedge(), g);
        f.Tick(AgainstLedge(4.5f, onGround: false, vy: -0.4f), g);
        var o = f.Tick(At(8, 2), g); // somewhere unexpected on top of the ledge
        Assert.Equal(FollowerStatus.NeedsReplan, o.Status);
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
        Assert.Equal(new TilePoint(3, 4), f.Tick(At(2, 4), g).Intent.CursorTile);
        g.Set(3, 4, TileInfo.Air);
        var o = f.Tick(At(2, 4), g);
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
        var f = new PathFollower(new[] { new PathStep(new TilePoint(2, 4), MoveKind.BuildPlatform, work) }, start: new TilePoint(1, 4));
        var o = f.Tick(At(1, 4), g);
        Assert.True(o.Intent.UseItem);
        Assert.Equal(work, o.Intent.CursorTile);
        Assert.False(o.Intent.Right);

        g.Set(2, 5, new TileInfo(TileKind.Platform));
        o = f.Tick(At(1, 4), g);
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
        var f = new PathFollower(new[] { new PathStep(new TilePoint(4, 2), MoveKind.Swim) }, start: new TilePoint(2, 4));
        // vanilla has no swimming without Flippers: mid-water, sinking, a jump press does nothing
        var o = f.Tick(At(2, 3, onGround: false, vy: 0.1f), g);
        Assert.False(o.Intent.Jump);
        Assert.True(o.Intent.Right);
        // on the bottom: jump
        o = f.Tick(At(2, 4), g);
        Assert.True(o.Intent.Jump);
        // reached without ground contact
        o = f.Tick(At(4.2f, 2.2f, onGround: false), g);
        Assert.Equal(FollowerStatus.Arrived, o.Status);
    }

    [Fact]
    public void Swim_WithFlippers_StrokesAgainInMidWater()
    {
        var (g, _) = ArrayTileGrid.Parse(
            "~~~~~~~~~~~~",
            "~~~~~~~~~~~~",
            "~~~~~~~~~~~~",
            "~~~~~~~~~~~~",
            "~~~~~~~~~~~~",
            "############");
        var f = new PathFollower(new[] { new PathStep(new TilePoint(2, 1), MoveKind.Swim) }, start: new TilePoint(2, 4),
            canSwim: true);
        var o = f.Tick(At(2.375f, 3, onGround: false, vy: 0.1f), g);
        Assert.True(o.Intent.Jump); // sinking mid-water: press
        o = f.Tick(At(2.375f, 3, onGround: false, vy: 0.1f), g);
        Assert.False(o.Intent.Jump); // release so the next press counts
        o = f.Tick(At(2.375f, 3, onGround: false, vy: 0.1f), g);
        Assert.True(o.Intent.Jump);
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

    // Flat floor with a closed door at column 6 (rows 2..4).
    private static ArrayTileGrid DoorGrid() => ArrayTileGrid.Parse(
        "............",
        "............",
        "......|.....",
        "......|.....",
        "......|.....",
        "############").Grid;

    [Fact]
    public void OpenDoor_InteractsWhileWalkingUp_AndStopsIfStillClosed()
    {
        var steps = new List<PathStep>
        {
            new(new TilePoint(2, 4), MoveKind.Walk), new(new TilePoint(3, 4), MoveKind.Walk),
            new(new TilePoint(4, 4), MoveKind.Walk), new(new TilePoint(4, 4), MoveKind.OpenDoor, new TilePoint(6, 4)),
            new(new TilePoint(5, 4), MoveKind.Walk),
        };
        var g = DoorGrid();
        var f = new PathFollower(steps, start: new TilePoint(1, 4));
        var o = f.Tick(At(1, 4), g);
        Assert.Equal(new TilePoint(6, 4), o.Intent.Interact); // asked for early
        // Standing in front of the still-closed door: the OpenDoor step holds still and keeps interacting.
        o = f.Tick(At(4, 4), g);
        Assert.Equal(MoveKind.OpenDoor, o.Current!.Value.Kind);
        Assert.Equal(new TilePoint(6, 4), o.Intent.Interact);
        Assert.False(o.Intent.Right);
        // The door opens: the step is done and the walk goes on.
        for (int y = 2; y <= 4; y++) g.Set(6, y, TileInfo.Air);
        o = f.Tick(At(4, 4), g);
        Assert.Equal(MoveKind.Walk, o.Current!.Value.Kind);
        Assert.Null(o.Intent.Interact);
        Assert.True(o.Intent.Right);
    }

    [Fact]
    public void CloseDoor_GivesUpAfterPatience()
    {
        // The door at column 6 is open (air) and never closes (someone in the doorway): the step is dropped.
        var g = Flat();
        var steps = new List<PathStep>
        {
            new(new TilePoint(8, 4), MoveKind.CloseDoor, new TilePoint(6, 4)),
            new(new TilePoint(9, 4), MoveKind.Walk),
        };
        var f = new PathFollower(steps, start: new TilePoint(8, 4));
        FollowerOutput o = default;
        for (int i = 0; i < PathFollower.CloseDoorPatience; i++)
        {
            o = f.Tick(At(8, 4), g);
            Assert.Equal(MoveKind.CloseDoor, o.Current!.Value.Kind);
            Assert.Equal(new TilePoint(6, 4), o.Intent.Interact);
        }
        o = f.Tick(At(8, 4), g);
        Assert.Equal(FollowerStatus.Running, o.Status);
        Assert.Equal(MoveKind.Walk, o.Current!.Value.Kind);
    }

    [Fact]
    public void ClosedDoorThePathOpens_DoesNotInvalidateThePath()
    {
        var steps = new List<PathStep>
        {
            new(new TilePoint(4, 4), MoveKind.OpenDoor, new TilePoint(6, 3)),
            new(new TilePoint(5, 4), MoveKind.Walk), new(new TilePoint(6, 4), MoveKind.Walk),
        };
        var f = new PathFollower(steps, start: new TilePoint(4, 4));
        var o = f.Tick(At(4, 4), DoorGrid());
        Assert.Equal(FollowerStatus.Running, o.Status);
        Assert.Equal(MoveKind.OpenDoor, o.Current!.Value.Kind);
    }
}
