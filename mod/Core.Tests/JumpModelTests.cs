using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class JumpModelTests
{
    private static MovementProfile Vanilla() => JumpModel.Apply(new MovementProfile(), PlayerPhysics.Vanilla);

    [Fact]
    public void Vanilla_MatchesKnownSafeValues()
    {
        var p = Vanilla();
        // A vanilla jump peaks ~6.1 tiles: 5 is the highest ledge you can still get onto.
        Assert.Equal(5, p.MaxJumpUp);
        // Standing-start gap at the same level: the conservative vanilla default.
        Assert.Equal(6, p.JumpReachAt(0));
    }

    [Fact]
    public void Reach_ShrinksWithHeight_AndGrowsWhenLanding_Lower()
    {
        var p = Vanilla();
        for (int dy = -p.MaxJumpUp; dy < 0; dy++)
            Assert.True(p.JumpReachAt(dy) <= p.JumpReachAt(dy + 1), $"dy={dy}");
        Assert.True(p.JumpReachAt(3) >= p.JumpReachAt(0));
        Assert.True(p.JumpReachAt(-5) >= 1);
        Assert.True(p.JumpReachAt(-5) < p.JumpReachAt(0));
    }

    [Fact]
    public void MaxJumpGap_IsTheLargestReach()
    {
        var p = Vanilla();
        int max = 0;
        for (int dy = -p.MaxJumpUp; dy <= p.MaxJumpUp; dy++) max = Math.Max(max, p.JumpReachAt(dy));
        Assert.Equal(max, p.MaxJumpGap);
    }

    [Fact]
    public void BetterJump_ReachesHigher()
    {
        // Shiny Red Balloon: jumpHeight 20, jumpSpeed 6.51
        var p = JumpModel.Apply(new MovementProfile(), PlayerPhysics.Vanilla with { JumpHeight = 20, JumpSpeed = 6.51f });
        Assert.True(p.MaxJumpUp > 5);
    }

    [Fact]
    public void Dazed_ReachesLower()
    {
        // dazed: jumpHeight /= 5, jumpSpeed /= 2
        var p = JumpModel.Apply(new MovementProfile(), PlayerPhysics.Vanilla with { JumpHeight = 3, JumpSpeed = 2.5f });
        Assert.True(p.MaxJumpUp <= 1);
        Assert.True(p.JumpReachAt(0) < 6);
    }

    [Fact]
    public void KeepsOtherFields()
    {
        var p = JumpModel.Apply(new MovementProfile(SafeFallTiles: 33, PickPower: 55, BuildItemCount: 9), PlayerPhysics.Vanilla);
        Assert.Equal(33, p.SafeFallTiles);
        Assert.Equal(55, p.PickPower);
        Assert.Equal(9, p.BuildItemCount);
    }

    [Fact]
    public void ProfileWithoutReach_FallsBackToMaxJumpGap()
    {
        var p = new MovementProfile(MaxJumpGap: 4);
        Assert.Equal(4, p.JumpReachAt(-3));
        Assert.Equal(4, p.JumpReachAt(2));
    }

    [Fact]
    public void Pathfinder_RespectsReach_HighFarLedge()
    {
        // A ledge 5 up and 5 across (feet-left 5 → 10) over a fatal pit: inside the old box model (5 up, 6 across) but a
        // standing-start vanilla jump can't get there.
        var (g, m) = new GridBuilder(24, 40)
            .Fill(0, 12, 5, 12, '#')     // takeoff ground, feet row 11
            .Fill(11, 7, 23, 7, '#')     // ledge, feet row 6
            .Put(4, 11, 'S').Put(14, 6, 'G').Parse();
        var box = Pathfinder.FindPath(g, m['S'], m['G'], new MovementProfile(), new(AllowDig: false, AllowBuild: false));
        Assert.True(box.Success);
        var real = Pathfinder.FindPath(g, m['S'], m['G'], Vanilla(), new(AllowDig: false, AllowBuild: false));
        Assert.False(real.Success);
    }

    [Fact]
    public void LowCeiling_ShortensReach()
    {
        var p = Vanilla();
        Assert.True(p.JumpReachAt(0, ceiling: 1) < p.JumpReachAt(0));
        Assert.Equal(0, p.JumpReachAt(-3, ceiling: 3)); // must rise one above the target row
        Assert.True(p.JumpReachAt(-3, ceiling: 4) >= 1);
    }

    [Fact]
    public void Pathfinder_LowCeiling_NoLongGapJump()
    {
        // 6-wide fatal pit with only one tile of head room (the world top): the arc would bonk and fall short.
        var (low, m) = new GridBuilder(24, 30).Fill(0, 4, 5, 4, '#').Fill(12, 4, 23, 4, '#')
            .Put(1, 3, 'S').Put(15, 3, 'G').Parse();
        Assert.True(Pathfinder.FindPath(low, m['S'], m['G'], new MovementProfile(), new()).Success); // box model
        Assert.False(Pathfinder.FindPath(low, m['S'], m['G'], Vanilla(), new()).Success);

        var (open, m2) = new GridBuilder(24, 40).Fill(0, 10, 5, 10, '#').Fill(12, 10, 23, 10, '#')
            .Put(1, 9, 'S').Put(15, 9, 'G').Parse();
        Assert.True(Pathfinder.FindPath(open, m2['S'], m2['G'], Vanilla(), new()).Success);
    }
}
