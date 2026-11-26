using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Observe;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class CaveFrontierTests
{
    // A tunnel running right into the unknown. Player 'S' feet-left at (1,3).
    private static readonly string[] Tunnel =
    {
        "##############",
        "#...........??",
        "#...........??",
        "#S..........??",
        "##############",
    };

    private static (ArrayTileGrid G, TilePoint S) Parse(params string[] rows)
    {
        var (g, m) = ArrayTileGrid.Parse(rows);
        return (g, m['S']);
    }

    [Fact]
    public void WalksToTheEndOfATunnelThatRunsIntoTheUnknown()
    {
        var (g, s) = Parse(Tunnel);
        var pick = CaveFrontier.Pick(g, s, s, 60);
        Assert.NotNull(pick);
        // The body can't stand half in the unknown column, so one left of the last open column, on the floor.
        Assert.Equal(new TilePoint(10, 3), pick!.Stand);
        Assert.Equal(11, pick.Frontier.X);
        Assert.True(pick.Unknown >= CaveFrontier.MinUnknown);
    }

    [Fact]
    public void NothingWhenTheCaveIsFullyKnown()
    {
        var (g, s) = Parse(
            "##########",
            "#........#",
            "#........#",
            "#S.......#",
            "##########");
        Assert.Null(CaveFrontier.Pick(g, s, s, 60));
    }

    [Fact]
    public void RadiusLimitsHowFarItLooks()
    {
        var (g, s) = Parse(Tunnel);
        Assert.Null(CaveFrontier.Pick(g, s, s, 5));
    }

    private static readonly string[] TwoWays =
    {
        "######################",
        "?....................?",
        "?....................?",
        "?.......S............?",
        "######################",
    };

    [Fact]
    public void NearestFrontierWins()
    {
        var (g, s) = Parse(TwoWays);
        var pick = CaveFrontier.Pick(g, s, s, 60);
        Assert.Equal(new TilePoint(1, 3), pick!.Stand);
    }

    [Fact]
    public void FrontiersNearLavaAreSkipped()
    {
        var rows = (string[])TwoWays.Clone();
        rows[4] = "##L###################";
        var (g, s) = Parse(rows);
        var pick = CaveFrontier.Pick(g, s, s, 60);
        Assert.Equal(new TilePoint(19, 3), pick!.Stand);
    }

    [Fact]
    public void RejectedStandsAreSkipped()
    {
        var (g, s) = Parse(TwoWays);
        var pick = CaveFrontier.Pick(g, s, s, 60, t => t == new TilePoint(1, 3));
        Assert.Equal(new TilePoint(19, 3), pick!.Stand);
        Assert.Null(CaveFrontier.Pick(g, s, s, 60, t => t.X is 1 or 19));
    }

    [Fact]
    public void UnknownBehindAWallIsNotAFrontier()
    {
        // Unknown just above a 1-thick ceiling, right over the player: not reachable through open space.
        var (g, s) = Parse(
            "??????????????",
            "##############",
            "#...........??",
            "#...........??",
            "#S..........??",
            "##############");
        var pick = CaveFrontier.Pick(g, s, s, 60);
        Assert.Equal(new TilePoint(10, 4), pick!.Stand);
    }

    [Fact]
    public void FrontierRightNextToThePlayerIsSkipped()
    {
        // The dark corner beside the player: a torch reveals it, walking doesn't.
        var (g, s) = Parse(
            "#######",
            "#....??",
            "#....??",
            "#S...??",
            "#######");
        Assert.Null(CaveFrontier.Pick(g, s, s, 60));
    }

    [Fact]
    public void ASingleDarkTileIsNotWorthAWalk()
    {
        var (g, s) = Parse(
            "##############",
            "#............#",
            "#............?",
            "#S...........#",
            "##############");
        Assert.Null(CaveFrontier.Pick(g, s, s, 60));
    }

    [Fact]
    public void StandsOnTheFloorBelowAnOpeningInTheCeiling()
    {
        // A chimney going up into the unknown at columns 9..10: the stand is on the floor under it.
        var (g, s) = Parse(
            "########????##",
            "#########..###",
            "#########..###",
            "#............#",
            "#............#",
            "#S...........#",
            "##############");
        var pick = CaveFrontier.Pick(g, s, s, 60);
        Assert.NotNull(pick);
        Assert.Equal(5, pick!.Stand.Y);
        Assert.InRange(pick.Stand.X, 8, 10);
    }

    [Fact]
    public void DoesNotWalkThroughLava()
    {
        // The only way to the unknown crosses a lava pool on the floor: the flood doesn't enter lava cells, and the
        // frontier beyond is out of reach.
        var (g, s) = Parse(
            "################",
            "#######.......??",
            "#######.......??",
            "#S....LLL.....??",
            "################");
        // Lava blocks rows 3 only; rows 1-2 at columns 1..6 are solid, so the only connection is through lava.
        Assert.Null(CaveFrontier.Pick(g, s, s, 60));
    }
}

public class ValuablesTests
{
    [Theory]
    [InlineData(Valuables.Pots, 0, true)]
    [InlineData(Valuables.Heart, 18, true)]
    [InlineData(Valuables.LifeFruit, 0, true)]
    [InlineData(Valuables.LargePiles2, 17 * 54, true)]
    [InlineData(Valuables.LargePiles2, 18 * 54 + 36, true)]
    [InlineData(Valuables.LargePiles2, 0, false)]
    [InlineData(Valuables.ShadowOrbs, 0, false)]
    [InlineData(Valuables.ShadowOrbs, 36, false)]
    [InlineData(Valuables.DemonAltar, 0, false)]
    [InlineData(21, 0, false)] // chest
    [InlineData(10, 0, false)] // door
    public void OnlyNaturalLootIsBreakable(int type, int frameX, bool loot)
    {
        Assert.Equal(loot, Valuables.IsBreakableLoot(type, frameX));
    }

    [Fact]
    public void ClassifiesCaveValuables()
    {
        Assert.Equal(ValuableKind.LifeCrystal, Valuables.Classify(Valuables.Heart, 0, false));
        Assert.Equal(ValuableKind.Pot, Valuables.Classify(Valuables.Pots, 36, false));
        Assert.Equal(ValuableKind.Orb, Valuables.Classify(Valuables.ShadowOrbs, 36, false));
        Assert.Equal(ValuableKind.Altar, Valuables.Classify(Valuables.DemonAltar, 54, false));
        Assert.Equal(ValuableKind.EnchantedSword, Valuables.Classify(Valuables.LargePiles2, 17 * 54, false));
        Assert.Equal(ValuableKind.Chest, Valuables.Classify(21, 0, true));
        Assert.Null(Valuables.Classify(Valuables.LargePiles2, 0, false));
        Assert.Null(Valuables.Classify(1, 0, false));
    }

    [Fact]
    public void NamesTellCorruptionFromCrimsonAndWarnAboutOrbs()
    {
        Assert.Equal("Shadow Orb (don't break)", Valuables.Name(ValuableKind.Orb, 0));
        Assert.Equal("Crimson Heart (don't break)", Valuables.Name(ValuableKind.Orb, 36));
        Assert.Equal("Demon Altar", Valuables.Name(ValuableKind.Altar, 18));
        Assert.Equal("Crimson Altar", Valuables.Name(ValuableKind.Altar, 54));
        Assert.Equal("Enchanted Sword (in stone)", Valuables.Name(ValuableKind.EnchantedSword, 17 * 54 + 18));
        Assert.StartsWith("Sword in Stone", Valuables.Name(ValuableKind.EnchantedSword, 18 * 54));
    }

    [Fact]
    public void ParsesStopOnKeys()
    {
        Assert.True(Valuables.TryParse("life_crystal", out var k) && k == ValuableKind.LifeCrystal);
        Assert.True(Valuables.TryParse("Life Crystal", out k) && k == ValuableKind.LifeCrystal);
        Assert.True(Valuables.TryParse("ore", out k) && k == ValuableKind.Ore);
        Assert.False(Valuables.TryParse("diamonds", out _));
        Assert.Equal("enchanted_sword", Valuables.Key(ValuableKind.EnchantedSword));
        Assert.Equal(Enum.GetValues<ValuableKind>().Length, Valuables.AllKeys.Count);
    }

    [Fact]
    public void OnlyLootKindsAreCollectible()
    {
        Assert.True(Valuables.IsCollectible(ValuableKind.Pot));
        Assert.True(Valuables.IsCollectible(ValuableKind.LifeCrystal));
        Assert.False(Valuables.IsCollectible(ValuableKind.Orb));
        Assert.False(Valuables.IsCollectible(ValuableKind.Altar));
        Assert.False(Valuables.IsCollectible(ValuableKind.Chest));
    }
}
