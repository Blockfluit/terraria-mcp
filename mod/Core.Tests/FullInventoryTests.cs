using LlmPlayer.Core.Inventory;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class FullInventoryTests
{
    private static readonly string[] Junk = { "Ebonstone Block", "Dirt Block" };

    [Theory]
    [InlineData("Life Crystal", false, false, 2, true)]
    [InlineData("Gold Ore", false, false, 0, true)]
    [InlineData("Iron Bar", false, false, 0, true)]
    [InlineData("Lesser Healing Potion", false, true, 1, true)]
    [InlineData("Gold Coin", true, false, 0, true)]
    [InlineData("Amethyst", false, false, 0, true)]
    [InlineData("Ebonstone Block", false, false, 0, false)]
    [InlineData("Dirt Block", false, false, 0, false)]
    [InlineData("Gel", false, false, 0, false)]
    public void Valuable(string name, bool coin, bool potion, int rare, bool expected) =>
        Assert.Equal(expected, FullInventory.IsValuable(new ItemWorth(name, coin, potion, rare)));

    private static List<InvSlot> Inv() => new()
    {
        new(0, "Copper Pickaxe", 1),
        new(12, "Dirt Block", 999, Placeable: true),
        new(13, "Ebonstone Block", 250, Placeable: true),
        new(14, "Ebonstone Block", 40, Placeable: true),
        new(15, "Ebonstone Block", 10, Favorited: true, Placeable: true),
        new(16, "Gold Ore", 30, Valuable: true, Placeable: true),
    };

    [Fact]
    public void ChooseTrash_ListOrderThenSmallestStack_NeverFavorited()
    {
        Assert.Equal(14, FullInventory.ChooseTrash(Inv(), Junk, new ItemWorth("Life Crystal", Rarity: 2)));
        Assert.Equal(12, FullInventory.ChooseTrash(Inv(), new[] { "Dirt Block", "Ebonstone Block" }, new ItemWorth("Life Crystal", Rarity: 2)));
    }

    [Fact]
    public void ChooseTrash_OnlyForValuables_AndOnlyWithAList()
    {
        Assert.Equal(-1, FullInventory.ChooseTrash(Inv(), Junk, new ItemWorth("Gel")));
        Assert.Equal(-1, FullInventory.ChooseTrash(Inv(), Array.Empty<string>(), new ItemWorth("Life Crystal", Rarity: 2)));
        Assert.Equal(-1, FullInventory.ChooseTrash(Inv(), new[] { "Gold Ore" }, new ItemWorth("Gold Ore")));
        Assert.Equal(-1, FullInventory.ChooseTrash(Inv(), new[] { "Stone Block" }, new ItemWorth("Life Crystal", Rarity: 2)));
        var inUse = new List<InvSlot> { new(13, "Ebonstone Block", 250, InUse: true) };
        Assert.Equal(-1, FullInventory.ChooseTrash(inUse, Junk, new ItemWorth("Life Crystal", Rarity: 2)));
    }

    [Fact]
    public void Suggestion_PrefersListedThenBiggestJunkBlock()
    {
        Assert.Equal("Ebonstone Block 250", FullInventory.Suggestion(Inv(), new[] { "Ebonstone Block" }));
        Assert.Equal("Dirt Block 999", FullInventory.Suggestion(Inv(), Array.Empty<string>()));
        Assert.Null(FullInventory.Suggestion(new List<InvSlot> { new(16, "Gold Ore", 30, Valuable: true) }, Array.Empty<string>()));
    }

    [Fact]
    public void Note_ListsItemsAndTheSuggestion()
    {
        Assert.Null(FullInventory.Note(new List<LeftItem>(), "x"));
        Assert.Equal("inventory full: Life Crystal at (4201,560) not picked up — drop/trash something (e.g. Ebonstone Block 250) with drop_item and pick_up_items again",
            FullInventory.Note(new List<LeftItem> { new("Life Crystal", 1, 4201, 560) }, "Ebonstone Block 250"));
        var many = Enumerable.Range(0, 6).Select(i => new LeftItem("Gold Ore", 3, i, 0)).ToList();
        var note = FullInventory.Note(many, null)!;
        Assert.Contains("Gold Ore x3 at (0,0)", note);
        Assert.Contains("(+2 more)", note);
        Assert.DoesNotContain("e.g.", note);
    }
}
