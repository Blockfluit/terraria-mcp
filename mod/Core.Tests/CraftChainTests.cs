using LlmPlayer.Core.Inventory;

namespace LlmPlayer.Core.Tests;

public class CraftChainTests
{
    private const int Sand = 1, Glass = 2, Bottle = 3, Mushroom = 4, PinkGel = 5, Potion = 6, Wood = 7, Platform = 8, BorealWood = 9, Chair = 10;

    private static ChainIngredient Ing(string name, int type, int count, params int[] alsoAccepted) =>
        new(name, type, new HashSet<int>(alsoAccepted.Append(type)), count);

    private static ChainRecipe R(int id, string name, int result, int stack, bool stations, params ChainIngredient[] ings) =>
        new(id, name, result, stack, ings, stations, stations ? Array.Empty<string>() : new[] { "Furnace" });

    private static List<ChainRecipe> Book(bool furnace = true) => new()
    {
        R(1, "Glass", Glass, 1, furnace, Ing("Sand Block", Sand, 1)),
        R(2, "Bottle", Bottle, 2, furnace, Ing("Glass", Glass, 1)),
        R(3, "Lesser Healing Potion", Potion, 2, true, Ing("Mushroom", Mushroom, 1), Ing("Pink Gel", PinkGel, 1), Ing("Bottle", Bottle, 2)),
        R(4, "Wood Platform", Platform, 2, true, Ing("Wood", Wood, 1, BorealWood)),
        R(5, "Wood", Wood, 1, true, Ing("Wood Platform", Platform, 2)),
        R(6, "Wooden Chair", Chair, 1, true, Ing("Wood", Wood, 4, BorealWood), Ing("Wood Platform", Platform, 4)),
    };

    private static Func<int, IReadOnlyList<ChainRecipe>> For(List<ChainRecipe> book) =>
        type => book.Where(r => r.Result == type).ToList();

    private static ChainPlan Plan(int recipeId, int times, Dictionary<int, int> owned, bool furnace = true, int maxDepth = CraftChain.MaxDepth)
    {
        var book = Book(furnace);
        return CraftChain.Plan(book.Single(r => r.Id == recipeId), times, owned, For(book), maxDepth);
    }

    [Fact]
    public void EverythingOwned_OnlyTheRequestedCraft()
    {
        var plan = Plan(3, 1, new() { [Mushroom] = 1, [PinkGel] = 1, [Bottle] = 2 });
        Assert.True(plan.Ok);
        Assert.Equal(new[] { new CraftStep(3, "Lesser Healing Potion", 1) }, plan.Steps);
    }

    [Fact]
    public void ChainsThroughBottleGlassSand()
    {
        var plan = Plan(3, 1, new() { [Mushroom] = 1, [PinkGel] = 1, [Sand] = 5 });
        Assert.True(plan.Ok, plan.Problem);
        Assert.Equal(new[] { new CraftStep(1, "Glass", 1), new CraftStep(2, "Bottle", 1), new CraftStep(3, "Lesser Healing Potion", 1) }, plan.Steps);
    }

    [Fact]
    public void ScalesIntermediatesWithTimesAndResultStacks()
    {
        // 3 potion crafts need 6 bottles = 3 bottle crafts = 3 glass.
        var plan = Plan(3, 3, new() { [Mushroom] = 3, [PinkGel] = 3, [Sand] = 3 });
        Assert.True(plan.Ok, plan.Problem);
        Assert.Equal(new[] { new CraftStep(1, "Glass", 3), new CraftStep(2, "Bottle", 3), new CraftStep(3, "Lesser Healing Potion", 3) }, plan.Steps);
    }

    [Fact]
    public void UsesPartlyOwnedIntermediates()
    {
        var plan = Plan(3, 2, new() { [Mushroom] = 2, [PinkGel] = 2, [Bottle] = 3, [Glass] = 1 });
        Assert.True(plan.Ok, plan.Problem);
        Assert.Equal(new[] { new CraftStep(2, "Bottle", 1), new CraftStep(3, "Lesser Healing Potion", 2) }, plan.Steps);
    }

    [Fact]
    public void MissingStation_ExplainsTheChain()
    {
        var plan = Plan(3, 1, new() { [Mushroom] = 1, [PinkGel] = 1, [Sand] = 5 }, furnace: false);
        Assert.False(plan.Ok);
        Assert.Empty(plan.Steps);
        Assert.Contains("Bottle x2 (have 0)", plan.Problem);
        Assert.Contains("Furnace", plan.Problem);
    }

    [Fact]
    public void RawMaterialMissing_SaysGatherIt()
    {
        var plan = Plan(3, 1, new() { [Mushroom] = 1, [Sand] = 5 });
        Assert.False(plan.Ok);
        Assert.Contains("Pink Gel x1 (have 0): not craftable", plan.Problem);
    }

    [Fact]
    public void NeverLoopsWoodAndPlatforms()
    {
        var plan = Plan(4, 1, new());
        Assert.False(plan.Ok);
        Assert.Contains("loop", plan.Problem);
    }

    [Fact]
    public void RecipeGroupMemberCounts()
    {
        var plan = Plan(4, 2, new() { [BorealWood] = 2 });
        Assert.True(plan.Ok, plan.Problem);
        Assert.Equal(new[] { new CraftStep(4, "Wood Platform", 2) }, plan.Steps);
    }

    [Fact]
    public void SharedIngredient_ReservesEnoughForBoth()
    {
        // Chair: Wood x4 + Platform x4; platforms come from Wood (1 → 2). 6 wood: 2 for platforms, 4 left. 
        var plan = Plan(6, 1, new() { [Wood] = 6 });
        Assert.True(plan.Ok, plan.Problem);
        Assert.Equal(new[] { new CraftStep(4, "Wood Platform", 2), new CraftStep(6, "Wooden Chair", 1) }, plan.Steps);

        var tooLittle = Plan(6, 1, new() { [Wood] = 5 });
        Assert.False(tooLittle.Ok);
    }

    [Fact]
    public void DepthLimit()
    {
        var plan = Plan(3, 1, new() { [Mushroom] = 1, [PinkGel] = 1, [Sand] = 5 }, maxDepth: 1);
        Assert.False(plan.Ok);
        Assert.Contains("too many crafting steps", plan.Problem);
    }

    [Fact]
    public void TopRecipeStationMissing()
    {
        var plan = Plan(1, 1, new() { [Sand] = 1 }, furnace: false);
        Assert.False(plan.Ok);
        Assert.Equal("Glass needs Furnace", plan.Problem);
    }
}
