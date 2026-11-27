using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;

namespace LlmPlayer.Core.Tests.Sim;

/// <summary>Sanity checks that the sim behaves like the game before trusting it to judge the follower.</summary>
public class MiniPhysicsTests
{
    private static (ArrayTileGrid, Dictionary<char, TilePoint>) Flat() =>
        new GridBuilder(40, 20).Fill(0, 15, 39, 19, '#').Put(5, 14, 'S').Parse();

    [Fact]
    public void StandingStill_StaysOnGround()
    {
        var (g, m) = Flat();
        var sim = new MiniPhysics(g, m['S']);
        for (int i = 0; i < 30; i++) sim.Step(default);
        Assert.Equal(m['S'], sim.StandingTile);
        Assert.True(sim.Kinematics.OnGround);
    }

    [Fact]
    public void Running_ReachesAboutThreePxPerTick()
    {
        var (g, m) = Flat();
        var sim = new MiniPhysics(g, m['S']);
        for (int i = 0; i < 60; i++) sim.Step(new ControlIntent { Right = true });
        Assert.InRange(sim.VX, 2.7f, 3.05f);
        Assert.True(sim.Kinematics.OnGround);
    }

    [Fact]
    public void Jump_PeaksAboutSixTiles()
    {
        var (g, m) = Flat();
        var sim = new MiniPhysics(g, m['S']);
        float startTop = sim.Y, minTop = sim.Y;
        for (int i = 0; i < 80; i++)
        {
            sim.Step(new ControlIntent { Jump = true });
            minTop = Math.Min(minTop, sim.Y);
        }
        Assert.InRange((startTop - minTop) / 16f, 5.8f, 6.4f);
        Assert.Equal(m['S'], sim.StandingTile);
    }

    [Fact]
    public void HeldJump_DoesNotRejumpOnLanding()
    {
        var (g, m) = Flat();
        var sim = new MiniPhysics(g, m['S']);
        for (int i = 0; i < 80; i++) sim.Step(new ControlIntent { Jump = true });
        for (int i = 0; i < 20; i++) sim.Step(new ControlIntent { Jump = true });
        Assert.True(sim.Kinematics.OnGround);
    }

    [Fact]
    public void WalkingIntoOneTileStep_StepsUp()
    {
        var (g, m) = new GridBuilder(40, 20).Fill(0, 15, 39, 19, '#').Fill(10, 14, 39, 14, '#')
            .Put(5, 14, 'S').Parse();
        var sim = new MiniPhysics(g, m['S']);
        for (int i = 0; i < 90; i++) sim.Step(new ControlIntent { Right = true });
        Assert.Equal(13, sim.StandingTile.Y);
        Assert.True(sim.StandingTile.X > 10);
    }

    [Fact]
    public void Platform_HoldsUnlessDown()
    {
        var (g, m) = new GridBuilder(20, 20).Fill(0, 19, 19, 19, '#').Fill(0, 10, 19, 10, '=')
            .Put(5, 9, 'S').Parse();
        var sim = new MiniPhysics(g, m['S']);
        for (int i = 0; i < 20; i++) sim.Step(default);
        Assert.Equal(9, sim.StandingTile.Y);
        sim.Step(new ControlIntent { Down = true });
        for (int i = 0; i < 60; i++) sim.Step(default);
        Assert.Equal(18, sim.StandingTile.Y);
    }

    [Fact]
    public void WalkingOffLedge_Falls()
    {
        var (g, m) = new GridBuilder(30, 30).Fill(0, 10, 9, 10, '#').Fill(0, 25, 29, 25, '#')
            .Put(5, 9, 'S').Parse();
        var sim = new MiniPhysics(g, m['S']);
        for (int i = 0; i < 120; i++) sim.Step(new ControlIntent { Right = i < 40 });
        Assert.Equal(24, sim.StandingTile.Y);
        Assert.Null(sim.Hurt);
    }

    [Fact]
    public void LongFall_Hurts()
    {
        var (g, m) = new GridBuilder(30, 60).Fill(0, 10, 9, 10, '#').Fill(0, 55, 29, 55, '#')
            .Put(5, 9, 'S').Parse();
        var sim = new MiniPhysics(g, m['S']);
        for (int i = 0; i < 200; i++) sim.Step(new ControlIntent { Right = i < 40 });
        Assert.NotNull(sim.Hurt);
    }

    [Fact]
    public void Pickaxe_MinesInReach_NotOutOfReach()
    {
        var (g, m) = Flat();
        var sim = new MiniPhysics(g, m['S']);
        var near = new TilePoint(5, 15);
        for (int i = 0; i < sim.DigTicks; i++) sim.Step(new ControlIntent { UseItem = true, CursorTile = near }, SimTool.Pickaxe);
        Assert.Contains(near, sim.Mined);
        var far = new TilePoint(20, 15);
        for (int i = 0; i < 100; i++) sim.Step(new ControlIntent { UseItem = true, CursorTile = far }, SimTool.Pickaxe);
        Assert.DoesNotContain(far, sim.Mined);
    }

    [Fact]
    public void Platform_PlacedOnlyWithAnchor()
    {
        var (g, m) = new GridBuilder(20, 20).Fill(0, 15, 19, 19, '#').Put(5, 14, 'S').Parse();
        var sim = new MiniPhysics(g, m['S']);
        var floating = new TilePoint(8, 10);
        sim.Step(new ControlIntent { UseItem = true, CursorTile = floating }, SimTool.Platform);
        Assert.Empty(sim.Placed);
        var anchored = new TilePoint(8, 14);
        sim.Step(new ControlIntent { UseItem = true, CursorTile = anchored }, SimTool.Platform);
        Assert.Contains(anchored, sim.Placed);
    }

    [Fact]
    public void Water_NoSwimmingUpWithoutGround()
    {
        // deep water: a jump only starts from the bottom, so the player can't climb mid-water
        var (g, m) = new GridBuilder(20, 40).Fill(0, 39, 19, 39, '#').Fill(0, 5, 19, 38, '~')
            .Put(5, 38, 'S').Parse();
        var sim = new MiniPhysics(g, m['S']);
        float best = sim.Y;
        for (int i = 0; i < 400; i++)
        {
            sim.Step(new ControlIntent { Jump = i % 2 == 0 });
            best = Math.Min(best, sim.Y);
        }
        Assert.True(best > 5 * 16 + 20 * 16, $"rose to y={best / 16f}"); // never got near the surface
    }

    [Fact]
    public void SlopeHill_WalkedUpAndDownOnTheGround()
    {
        var (g, m) = FollowerIntegrationTests.SlopeHill().Parse();
        var sim = new MiniPhysics(g, m['S']);
        int airborne = 0;
        float minBottom = float.MaxValue;
        for (int i = 0; i < 400 && sim.X < 32 * 16; i++)
        {
            sim.Step(new ControlIntent { Right = true });
            if (!sim.Kinematics.OnGround) airborne++;
            minBottom = Math.Min(minBottom, sim.Y + MiniPhysics.H);
        }
        Assert.Equal(10 * 16, minBottom);           // up on the 4-wide top (row 10)
        Assert.Equal(0, airborne);                  // the slopes carry the hitbox up and down: never in the air
        Assert.True(sim.VX > 2.5f, $"slowed down: {sim}");
        for (int i = 0; i < 40; i++) sim.Step(default);
        Assert.Equal(15, sim.StandingTile.Y);       // back on the flat ground
    }

    [Fact]
    public void SlopeHill_StandingStillOnSlope_DoesNotSlide()
    {
        var (g, m) = FollowerIntegrationTests.SlopeHill().Parse();
        var sim = new MiniPhysics(g, m['S']);
        while (sim.X < 12.3f * 16) sim.Step(new ControlIntent { Right = true });
        for (int i = 0; i < 60; i++) sim.Step(default);
        float x = sim.X, y = sim.Y;
        for (int i = 0; i < 60; i++) sim.Step(default);
        Assert.Equal(x, sim.X);
        Assert.Equal(y, sim.Y);
        Assert.True(sim.Kinematics.OnGround);
        // The feet are inside a slope's row; the standing tile is the body on top of it.
        Assert.True(Pathfinder.CanStand(g, sim.StandingTile), $"{sim}");
    }

    [Fact]
    public void HalfBlock_IsAHalfTileStep()
    {
        var (g, m) = new GridBuilder(40, 10).Fill(0, 8, 39, 9, '#').Fill(10, 7, 20, 7, '_').Put(2, 7, 'S').Parse();
        var sim = new MiniPhysics(g, m['S']);
        int airborne = 0;
        for (int i = 0; i < 90; i++)
        {
            sim.Step(new ControlIntent { Right = true });
            if (!sim.Kinematics.OnGround) airborne++;
        }
        Assert.True(sim.X > 12 * 16 && sim.X < 18 * 16, $"{sim}");
        Assert.Equal(7 * 16 + 8, sim.Y + MiniPhysics.H); // on the half block's top
        Assert.Equal(0, airborne);
        Assert.Equal(new TilePoint(sim.StandingTile.X, 6), sim.StandingTile);
    }

    [Fact]
    public void SlopeWallSide_StepsUpLikeABlock()
    {
        // Walking right into a slope high on the left: its full-height left side is a 1-tile step.
        var (g, m) = new GridBuilder(30, 10).Fill(0, 8, 29, 9, '#').Put(10, 7, '\\').Fill(11, 7, 29, 7, '#')
            .Put(2, 7, 'S').Parse();
        var sim = new MiniPhysics(g, m['S']);
        for (int i = 0; i < 90; i++) sim.Step(new ControlIntent { Right = true });
        Assert.Equal(6, sim.StandingTile.Y);
        Assert.True(sim.X > 12 * 16);
    }
}
