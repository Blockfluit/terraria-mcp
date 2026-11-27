using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using LlmPlayer.Core.Npc;

namespace LlmPlayer.Core.Tests;

/// <summary>talk_to_npc / buy: stand anywhere within vanilla talk range (through walls), and pick NPCs by name.</summary>
public class TalkStandsTests
{
    private const int NpcW = 18, NpcH = 40;

    /// <summary>NPC hitbox standing with its feet on tile row <paramref name="feetY"/>, left edge at column x.</summary>
    private static (int X, int Y) NpcAt(int x, int feetY) => (x * 16, (feetY + 1) * 16 - NpcH);

    // An NPC shut in a house of unbreakable blocks (no door): the pathfinder can't get in. 'S' player, 'G' NPC feet.
    private static readonly string[] ClosedHouse =
    {
        "..........................",
        "..........................",
        "..............XXXXXXXX....",
        "..............X......X....",
        "..............X......X....",
        "..............X......X....",
        "S.............X.G....X....",
        "XXXXXXXXXXXXXXXXXXXXXXXXXX",
        "XXXXXXXXXXXXXXXXXXXXXXXXXX",
    };

    [Fact]
    public void InRangeFrom_HoldsForEveryPositionThatRoundsToTheTile()
    {
        var (nx, ny) = NpcAt(20, 10);
        for (int x = 8; x <= 32; x++)
            for (int y = 2; y <= 18; y++)
            {
                var t = new TilePoint(x, y);
                if (!TalkStands.InRangeFrom(t, 5, 4, nx, ny, NpcW, NpcH)) continue;
                float top = (y + 1) * 16f - 42;
                for (float left = x * 16 - 1.5f; left < x * 16 + 14; left += 0.5f) // (-2 itself is a rounding tie)
                {
                    // Every hitbox position whose standing tile is t (NavMath.StandingTile) really is in range.
                    var k = NavMath.FromPixels(left, top, 0, 0);
                    Assert.Equal(t, NavMath.StandingTile(k));
                    Assert.True(NpcMath.InTalkRange(left, top, 20, 42, 5, 4, nx, ny, NpcW, NpcH), $"{t} left={left}");
                }
            }
    }

    [Fact]
    public void Find_ThroughTheWall_IncludesStandsOutsideTheHouse_ButNotTooFar()
    {
        var (g, m) = ArrayTileGrid.Parse(ClosedHouse);
        var (nx, ny) = NpcAt(m['G'].X, m['G'].Y);
        var stands = TalkStands.Find(g, nx, ny, NpcW, NpcH, 5, 4);
        Assert.Contains(new TilePoint(12, 6), stands);     // right outside the left wall
        Assert.Contains(new TilePoint(11, 6), stands);     // 5 tiles off: still in range
        Assert.DoesNotContain(new TilePoint(10, 6), stands); // 6 tiles off: not from everywhere in that tile
        Assert.Contains(new TilePoint(16, 6), stands);     // inside, next to the NPC
        Assert.DoesNotContain(new TilePoint(8, 6), stands); // 8 tiles off: out of range
        Assert.DoesNotContain(new TilePoint(14, 6), stands); // in the wall
        Assert.All(stands, s => Assert.True(Pathfinder.CanStand(g, s)));
    }

    [Fact]
    public void Find_SkipsUnknownTiles()
    {
        var rows = (string[])ClosedHouse.Clone();
        rows[7] = "XXXXXXXXXXX???XXXXXXXXXXXX"; // ground under x 11..13 never seen
        var (g, m) = ArrayTileGrid.Parse(rows);
        var (nx, ny) = NpcAt(m['G'].X, m['G'].Y);
        var stands = TalkStands.Find(g, nx, ny, NpcW, NpcH, 5, 4);
        Assert.DoesNotContain(new TilePoint(12, 6), stands); // ground under both feet unknown
    }

    [Fact]
    public void Pathfinder_GoalSet_ReachesAStandOutsideAShutHouse()
    {
        var (g, m) = ArrayTileGrid.Parse(ClosedHouse);
        var opts = new PathOptions(AllowDig: false, AllowBuild: false, GoalTolerance: 0);
        // The NPC itself can't be reached (the live failure: "unreachable: searched N nodes").
        Assert.False(Pathfinder.FindPath(g, m['S'], m['G'], new(), opts).Success);

        var (nx, ny) = NpcAt(m['G'].X, m['G'].Y);
        var stands = TalkStands.Find(g, nx, ny, NpcW, NpcH, 5, 4);
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(), opts with { GoalSet = stands });
        Assert.True(r.Success, r.FailureReason);
        var end = r.Steps[^1].Pos;
        Assert.Contains(end, stands);
        Assert.Equal(end, r.ResolvedGoal);
        Assert.Equal(new TilePoint(11, 6), end); // the first stand in range on the way (5 tiles from the NPC), not up to the wall
    }

    [Fact]
    public void Pathfinder_GoalSet_NoneReachable_Fails()
    {
        var (g, m) = ArrayTileGrid.Parse(ClosedHouse);
        var opts = new PathOptions(AllowDig: false, AllowBuild: false, GoalTolerance: 0);
        // Only stands inside the house: unreachable.
        var inside = new HashSet<TilePoint> { new(15, 6), new(16, 6), new(17, 6) };
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(), opts with { GoalSet = inside });
        Assert.False(r.Success);
        Assert.StartsWith("unreachable", r.FailureReason);
    }

    [Fact]
    public void Pathfinder_GoalSet_GoalInsideRock_IsNoFailure()
    {
        var (g, m) = ArrayTileGrid.Parse("..........", "..........", "S.........", "##########", "#####G####");
        var set = new HashSet<TilePoint> { new(6, 2) };
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(), new PathOptions(AllowDig: false, AllowBuild: false, GoalSet: set));
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(new TilePoint(6, 2), r.Steps[^1].Pos);
    }

    private static readonly NpcCandidate[] Town =
    {
        new(3, "Demetrius", "Arms Dealer", 12),
        new(5, "Andrew", "Guide", 4),
        new(8, "Bob", "Guide", 2),
        new(9, "Skeleton Merchant", "Skeleton Merchant", 30),
    };

    [Theory]
    [InlineData("Demetrius", 3)]
    [InlineData("arms dealer", 3)]
    [InlineData("  Arms   Dealer ", 3)]
    [InlineData("guide", 8)]   // two Guides: the nearer one
    [InlineData("ANDREW", 5)]
    [InlineData("skeleton merchant", 9)]
    [InlineData("Arms", -1)]   // whole names only
    [InlineData("", -1)]
    public void NpcNames_MatchByNameOrProfession(string query, int want) => Assert.Equal(want, NpcNames.Match(Town, query));

    [Fact]
    public void NpcNames_ListForErrors()
    {
        Assert.Equal("Bob (Guide), Andrew (Guide), Demetrius (Arms Dealer), Skeleton Merchant", NpcNames.List(Town));
        Assert.Equal("none", NpcNames.List(Array.Empty<NpcCandidate>()));
    }
}
