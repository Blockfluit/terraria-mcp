using LlmPlayer.Core.Build;
using LlmPlayer.Core.Npc;

namespace LlmPlayer.Core.Tests;

public class HouseMaterialsTests
{
    private const int Frame = 15, Walls = 32; // default 10x6 on flat ground

    private static BlockStock Wood(int n) => new(9, "Wood", n, WallSource.Wood);
    private static BlockStock Dirt(int n) => new(2, "Dirt Block", n, WallSource.Dirt);
    private static BlockStock Stone(int n) => new(3, "Stone Block", n, WallSource.Stone);

    private static HouseStock Stock(BlockStock[] blocks, bool bench = true, int doors = 0, int tables = 0, int chairs = 0,
        int torches = 0, int walls = 0, int gel = 0) => new(doors, tables, chairs, torches, walls, blocks, gel, bench);

    [Fact]
    public void EverythingFromWood_AtAWorkBench()
    {
        // Door 6 + chair 4 + table 8 + walls 8 (32/4) + frame 15 = 41 Wood; the torch is in the inventory.
        var bill = HouseMaterials.Plan(Frame, Walls, Stock(new[] { Wood(41) }, torches: 1));
        Assert.True(bill.Ok, bill.MissingText());
        Assert.Equal(new[] { new CraftOrder("Wood Wall", 8), new("Wooden Door", 1), new("Wooden Chair", 1), new("Wooden Table", 1) }, bill.Crafts);
        Assert.Equal(new[] { ("Wood", 15) }, bill.FrameUse);
    }

    [Fact]
    public void OneWoodShort_ReportsExactlyOne()
    {
        var bill = HouseMaterials.Plan(Frame, Walls, Stock(new[] { Wood(40) }, torches: 1));
        Assert.False(bill.Ok);
        var m = Assert.Single(bill.Missing);
        Assert.Equal("Wood", m.Item);
        Assert.Equal(1, m.Count);
    }

    [Fact]
    public void OwnedItemsAreUsedFirst()
    {
        var bill = HouseMaterials.Plan(Frame, Walls, Stock(new[] { Wood(15) }, doors: 1, tables: 1, chairs: 1, torches: 3, walls: 40));
        Assert.True(bill.Ok, bill.MissingText());
        Assert.Empty(bill.Crafts);
    }

    [Fact]
    public void WallsFromDirt_WhenWoodOnlyCoversFurnitureAndFrame()
    {
        var bill = HouseMaterials.Plan(Frame, Walls, Stock(new[] { Wood(33), Dirt(8) }, torches: 1));
        Assert.True(bill.Ok, bill.MissingText());
        Assert.Equal(new CraftOrder("Dirt Wall", 8), bill.Crafts[0]);
        Assert.Equal(new[] { ("Wood", 15) }, bill.FrameUse);
    }

    [Fact]
    public void FrameMixesBlocksInPreferenceOrder()
    {
        var bill = HouseMaterials.Plan(Frame, 0, Stock(new[] { Wood(5), Stone(20) }, doors: 1, tables: 1, chairs: 1, torches: 1));
        Assert.True(bill.Ok, bill.MissingText());
        Assert.Equal(new[] { ("Wood", 5), ("Stone Block", 10) }, bill.FrameUse);
    }

    [Fact]
    public void NoWorkBench_TableBecomesAWorkBench_AndBenchCraftsAreReported()
    {
        var bill = HouseMaterials.Plan(Frame, 0, Stock(new[] { Wood(100) }, bench: false, doors: 1, chairs: 1, torches: 1));
        Assert.True(bill.Ok, bill.MissingText());
        Assert.Equal(new[] { new CraftOrder("Work Bench", 1) }, bill.Crafts);

        var noBench = HouseMaterials.Plan(Frame, Walls, Stock(new[] { Wood(100) }, bench: false, torches: 1));
        var m = Assert.Single(noBench.Missing);
        Assert.Equal("Work Bench nearby", m.Item);
        Assert.Contains("Wooden Door", m.Hint);
        Assert.Contains("background walls", m.Hint);
    }

    [Fact]
    public void Torch_CraftedFromGel_OrReportedMissing()
    {
        var withGel = HouseMaterials.Plan(Frame, 0, Stock(new[] { Wood(16) }, doors: 1, tables: 1, chairs: 1, gel: 2));
        Assert.True(withGel.Ok, withGel.MissingText());
        Assert.Contains(new CraftOrder("Torch", 1), withGel.Crafts);

        var none = HouseMaterials.Plan(Frame, 0, Stock(new[] { Wood(16) }, doors: 1, tables: 1, chairs: 1));
        Assert.Equal("Torch", Assert.Single(none.Missing).Item);
    }

    [Fact]
    public void NothingAtAll_ListsWoodAndTorch()
    {
        var bill = HouseMaterials.Plan(Frame, Walls, Stock(Array.Empty<BlockStock>()));
        Assert.False(bill.Ok);
        Assert.Contains(bill.Missing, m => m.Item == "Torch");
        Assert.Equal(6 + 4 + 8 + 8 + 15, bill.Missing.Where(m => m.Item == "Wood").Sum(m => m.Count));
    }
}

public class TownNpcFactsTests
{
    private static readonly IReadOnlySet<int> None = new HashSet<int>();

    [Fact]
    public void Table_HasTheEarlyNpcsOnce()
    {
        foreach (var name in new[] { "Guide", "Merchant", "Nurse", "Demolitionist", "Arms Dealer", "Dryad" })
            Assert.Single(TownNpcFacts.All, f => f.Name == name);
        Assert.Equal(TownNpcFacts.All.Count, TownNpcFacts.All.Select(f => f.NpcType).Distinct().Count());
    }

    [Fact]
    public void Guide_AlwaysCanMoveIn_PresentNpcsAreLeftOut()
    {
        var p = TownNpcFacts.Prospects(None, 0, _ => false);
        Assert.True(p.Single(x => x.Name == "Guide").CanMoveIn);
        var withGuide = TownNpcFacts.Prospects(new HashSet<int> { TownNpcFacts.Guide }, 1, _ => false);
        Assert.DoesNotContain(withGuide, x => x.Name == "Guide");
    }

    [Fact]
    public void Nurse_NeedsLifeCrystalAndMerchant()
    {
        bool Crystal(SpawnCondition c) => c == SpawnCondition.LifeCrystalUsed;
        var noMerchant = TownNpcFacts.Prospects(None, 1, Crystal).Single(x => x.Name == "Nurse");
        Assert.False(noMerchant.CanMoveIn);
        Assert.Contains("Merchant must live here first", noMerchant.Missing);

        var ok = TownNpcFacts.Prospects(new HashSet<int> { TownNpcFacts.Merchant }, 2, Crystal).Single(x => x.Name == "Nurse");
        Assert.True(ok.CanMoveIn);
        Assert.Null(ok.Missing);

        var noCrystal = TownNpcFacts.Prospects(new HashSet<int> { TownNpcFacts.Merchant }, 2, _ => false).Single(x => x.Name == "Nurse");
        Assert.Contains("Life Crystal", noCrystal.Missing);
    }

    [Fact]
    public void Merchant_ArmsDealer_Demolitionist_FollowTheirItemConditions()
    {
        var met = new HashSet<SpawnCondition> { SpawnCondition.Coins50Silver, SpawnCondition.Explosive };
        var p = TownNpcFacts.Prospects(None, 1, met.Contains);
        Assert.True(p.Single(x => x.Name == "Merchant").CanMoveIn);
        Assert.False(p.Single(x => x.Name == "Arms Dealer").CanMoveIn);
        Assert.Contains("bullets", p.Single(x => x.Name == "Arms Dealer").Missing);
        // Demolitionist has the explosive but no Merchant yet.
        Assert.False(p.Single(x => x.Name == "Demolitionist").CanMoveIn);
    }

    [Fact]
    public void Painter_And_DyeTrader_CountTownNpcs()
    {
        var p = TownNpcFacts.Prospects(None, 7, _ => true);
        Assert.Contains("8 town NPCs (have 7)", p.Single(x => x.Name == "Painter").Missing);
        Assert.True(p.Single(x => x.Name == "Dye Trader").CanMoveIn);
        Assert.Contains("4 town NPCs (have 3)", TownNpcFacts.Prospects(None, 3, _ => true).Single(x => x.Name == "Dye Trader").Missing);
    }
}
