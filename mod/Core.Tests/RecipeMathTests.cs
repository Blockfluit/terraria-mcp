using LlmPlayer.Core.Inventory;
using LlmPlayer.Core.Observe;

namespace LlmPlayer.Core.Tests;

public class RecipeMathTests
{
    private static readonly ItemName[] Results =
    {
        new("Wooden Sword", "WoodenSword"),
        new("Work Bench", "WorkBench"),
        new("Torch", "Torch"),
        new("Work Bench", "WorkBench"),
        new("Wooden Table", "WoodenTable"),
    };

    [Fact]
    public void MatchResults_DisplayNameCaseInsensitive_ReturnsAllMatches() =>
        Assert.Equal(new[] { 1, 3 }, RecipeMath.MatchResults(Results, "  work bench "));

    [Fact]
    public void MatchResults_FallsBackToInternalName() =>
        Assert.Equal(new[] { 0 }, RecipeMath.MatchResults(Results, "woodensword"));

    [Fact]
    public void MatchResults_NoMatch_Empty()
    {
        Assert.Empty(RecipeMath.MatchResults(Results, "Wood"));
        Assert.Empty(RecipeMath.MatchResults(Results, "   "));
    }

    [Theory]
    [InlineData("bench", true)]
    [InlineData("Work Bench", true)]
    [InlineData("WorkBench", true)]
    [InlineData("sword", false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    public void NameFilter_SubstringOfDisplayOrExactInternal(string? filter, bool expected) =>
        Assert.Equal(expected, RecipeMath.NameFilter(new ItemName("Work Bench", "WorkBench"), filter));

    [Fact]
    public void Missing_ListsOnlyShortIngredientsWithCounts()
    {
        var missing = RecipeMath.Missing(new[]
        {
            new Ingredient("Wood", Need: 10, Have: 3),
            new Ingredient("Gel", Need: 1, Have: 5),
            new Ingredient("Iron Bar", Need: 2, Have: 0),
        });
        Assert.Equal(new[] { new MissingIngredient("Wood", 10, 3), new MissingIngredient("Iron Bar", 2, 0) }, missing);
    }

    [Fact]
    public void Missing_ScalesByTimes()
    {
        var missing = RecipeMath.Missing(new[] { new Ingredient("Wood", 10, 25) }, times: 3);
        Assert.Equal(new[] { new MissingIngredient("Wood", 30, 25) }, missing);
    }

    [Fact]
    public void Describe_IsReadable() =>
        Assert.Equal("Wood x10 (have 3), Iron Bar x2 (have 0)",
            RecipeMath.Describe(new[] { new MissingIngredient("Wood", 10, 3), new MissingIngredient("Iron Bar", 2, 0) }));

    [Fact]
    public void MaxCrafts_IsLimitedByScarcestIngredient()
    {
        Assert.Equal(2, RecipeMath.MaxCrafts(new[] { new Ingredient("Wood", 10, 25), new Ingredient("Gel", 1, 9) }));
        Assert.Equal(0, RecipeMath.MaxCrafts(new[] { new Ingredient("Wood", 10, 9) }));
    }

    [Fact]
    public void MaxCrafts_NoIngredients_IsUnbounded() =>
        Assert.Equal(int.MaxValue, RecipeMath.MaxCrafts(Array.Empty<Ingredient>()));

    [Fact]
    public void PickRecipe_PrefersAvailable_ThenIngredientsAtHand_ThenStationsMet_ThenFewestMissing()
    {
        // Iron Bar: from ore (have it, Furnace missing) vs from Iron Fence (no station needed, have none).
        Assert.Equal(0, RecipeMath.PickRecipe(new[]
        {
            new RecipeCandidate(Available: false, StationsMet: false, MissingKinds: 0),
            new RecipeCandidate(false, true, 1),
        }));
        Assert.Equal(2, RecipeMath.PickRecipe(new[]
        {
            new RecipeCandidate(Available: false, StationsMet: true, MissingKinds: 0),
            new RecipeCandidate(false, false, 0),
            new RecipeCandidate(true, true, 0),
        }));
        Assert.Equal(1, RecipeMath.PickRecipe(new[]
        {
            new RecipeCandidate(false, false, 1),
            new RecipeCandidate(false, true, 2),
            new RecipeCandidate(false, true, 3),
        }));
        Assert.Equal(1, RecipeMath.PickRecipe(new[]
        {
            new RecipeCandidate(false, false, 3),
            new RecipeCandidate(false, false, 1),
        }));
        Assert.Equal(-1, RecipeMath.PickRecipe(Array.Empty<RecipeCandidate>()));
    }

    [Theory]
    [InlineData(1, 5, 1, null)]
    [InlineData(5, 5, 5, null)]
    [InlineData(7, 3, 3, "only enough ingredients for 3")]
    [InlineData(4, 0, 0, "only enough ingredients for 0")]
    public void PlanCount_ClampsToCraftable(int requested, int max, int expected, string? reason)
    {
        var (n, why) = RecipeMath.PlanCount(requested, max);
        Assert.Equal(expected, n);
        Assert.Equal(reason, why);
    }
}

public class ReachMathTests
{
    // Player standing at tile (100,50): hitbox 20x42 px, position = (100*16 + 6, 51*16 - 42) = (1606, 774).
    private const float Px = 1606, Py = 774;
    private const int W = 20, H = 42;

    [Theory]
    [InlineData(96, 45, 0, true)]
    [InlineData(95, 50, 0, false)]
    [InlineData(105, 53, 0, true)]
    [InlineData(106, 50, 0, false)]
    [InlineData(100, 44, 0, false)]
    [InlineData(100, 54, 0, false)]
    [InlineData(95, 50, 1, true)]
    [InlineData(100, 54, 1, true)]
    public void InItemRange_MatchesVanillaFormula(int tx, int ty, int tileBoost, bool expected) =>
        Assert.Equal(expected, ReachMath.InItemRange(Px, Py, W, H, rangeX: 5, rangeY: 4, tileBoost, tx, ty));

    [Theory]
    [InlineData(100, 49, true)]
    [InlineData(101, 50, true)]
    [InlineData(100, 48, true)]
    [InlineData(102, 50, false)]
    [InlineData(99, 50, false)]
    [InlineData(100, 51, false)]
    [InlineData(100, 47, false)]
    public void OverlapsBody(int tx, int ty, bool expected) =>
        Assert.Equal(expected, ReachMath.OverlapsBody(Px, Py, W, H, tx, ty));

    [Fact]
    public void StepAsideGoal_GoesToTheSideThePlayerIsOn()
    {
        Assert.Equal(new Grid.TilePoint(102, 50), ReachMath.StepAsideGoal(new(100, 50), new(100, 49)));
        Assert.Equal(new Grid.TilePoint(97, 50), ReachMath.StepAsideGoal(new(99, 50), new(100, 50)));
    }
}
