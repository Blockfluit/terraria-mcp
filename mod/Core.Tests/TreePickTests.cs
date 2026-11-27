using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Inventory;

namespace LlmPlayer.Core.Tests;

public class TreePickTests
{
    private static TilePoint? Self(TilePoint t) => t;

    [Fact]
    public void Rank_SkipsProtectedTrees_AndCountsThem()
    {
        // Seen in game: the nearest tree (near spawn) was protected six calls in a row.
        var trees = Enumerable.Range(1, 12).Select(i => new TilePoint(100 + 3 * i, 50)).ToList();
        var choice = TreePick.Rank(trees, new TilePoint(100, 49), Self, t => t.X < 130, _ => true, max: 3);
        Assert.Equal(new[] { new TilePoint(130, 50), new TilePoint(133, 50), new TilePoint(136, 50) }, choice.Bases);
        Assert.Equal(9, choice.SkippedProtected);
        Assert.Contains("9 protected", choice.SkipText());
    }

    [Fact]
    public void Rank_DedupsRootsToOneTrunk_SkipsHiddenBases_NearestFirst()
    {
        var trunk = new TilePoint(10, 20);
        var found = new[] { new TilePoint(9, 20), trunk, new TilePoint(11, 20), new TilePoint(30, 20), new TilePoint(5, 20) };
        TilePoint? baseOf(TilePoint t) => Math.Abs(t.X - 10) <= 1 ? trunk : t;
        var choice = TreePick.Rank(found, new TilePoint(0, 19), baseOf, _ => false, t => t.X != 5);
        Assert.Equal(new[] { trunk, new TilePoint(30, 20) }, choice.Bases);
        Assert.Equal(1, choice.SkippedHidden);
        Assert.Equal(0, choice.SkippedProtected);
    }

    [Fact]
    public void Rank_AllProtected_ReturnsNoBases()
    {
        var choice = TreePick.Rank(new[] { new TilePoint(1, 1), new TilePoint(2, 1) }, new TilePoint(0, 0), Self, _ => true, _ => true);
        Assert.Empty(choice.Bases);
        Assert.Equal(2, choice.Skipped);
    }

    [Theory]
    [InlineData("can't reach (5,5): goal_not_standable", true)]
    [InlineData("couldn't get within reach of (5,5)", true)]
    [InlineData("(5,5) didn't break after 15 s of swinging", false)]
    [InlineData(null, false)]
    public void IsReachFailure(string? reason, bool expected) => Assert.Equal(expected, TreePick.IsReachFailure(reason));
}

public class SlotTrackingTests
{
    private sealed class It(int type, int stack = 1)
    {
        public int Type = type;
        public int Stack = stack;
    }

    private static int Resolve(It?[] inv, int slot, It item) =>
        SlotTracking.Resolve(inv, slot, item, i => i.Stack <= 0, (a, b) => a.Type == b.Type);

    [Fact]
    public void SameSlot_StaysPut()
    {
        var torch = new It(8, 50);
        Assert.Equal(3, Resolve(new It?[] { null, null, null, torch }, 3, torch));
    }

    [Fact]
    public void SwappedBack_ByAnotherLease_FollowsTheObject()
    {
        // The command picked slot 0 while a running task had swapped the torches there from slot 20; cancelling that
        // task swapped them back and left slot 0 empty.
        var torch = new It(8, 50);
        var inv = new It?[21];
        inv[20] = torch;
        Assert.Equal(20, Resolve(inv, 0, torch));
    }

    [Fact]
    public void ReplacedStack_FallsBackToSameType_ElseGone()
    {
        var old = new It(8, 0);
        var inv = new It?[] { new It(1), new It(8, 12) };
        Assert.Equal(1, Resolve(inv, 0, old));
        Assert.Equal(-1, Resolve(new It?[] { new It(1), null }, 0, old));
    }
}
