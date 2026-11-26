using System.Text.Json;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Observe;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class MotionTests
{
    [Fact]
    public void ConvertsPixelsPerTickToTilesPerSecond()
    {
        // 16 px/tick = 1 tile/tick = 60 tiles/s.
        Assert.Equal(60.0, Motion.TilesPerSecond(16f));
        Assert.Equal(-7.5, Motion.TilesPerSecond(-2f));
        Assert.Equal(0.4, Motion.TilesPerSecond(0.1f)); // 0.375 rounds to 0.4
    }

    [Fact]
    public void StationaryIsOmitted()
    {
        Assert.Equal((null, null), Motion.Velocity(0f, 0f));
        Assert.Equal((null, null), Motion.Velocity(0.01f, -0.01f)); // rounds to 0
        Assert.Equal((3.8, 0.0), Motion.Velocity(1f, 0f));
    }
}

public class EntityRankingTests
{
    private sealed record E(string Name, EntityTier Tier, double D, bool Proj = false);

    private static List<string> Pick(IEnumerable<E> items, int max = 30) =>
        EntityRanking.Select(items, e => e.Tier, e => e.D, e => e.Proj, max).Select(e => e.Name).ToList();

    [Fact]
    public void BossesFirstThenThreatsThenOthers_EachByDistance()
    {
        var items = new[]
        {
            new E("item", EntityTier.Other, 1),
            new E("zombie", EntityTier.Threat, 20),
            new E("boss", EntityTier.Boss, 60),
            new E("spit", EntityTier.Threat, 5, Proj: true),
            new E("guide", EntityTier.Other, 3),
        };
        Assert.Equal(["boss", "spit", "zombie", "item", "guide"], Pick(items));
    }

    [Fact]
    public void FarBossSurvivesFullBudget()
    {
        var items = Enumerable.Range(0, 50).Select(i => new E($"z{i}", EntityTier.Threat, i))
            .Append(new E("boss", EntityTier.Boss, 150)).ToList();
        var picked = Pick(items);
        Assert.Equal(30, picked.Count);
        Assert.Equal("boss", picked[0]);
        Assert.Equal("z28", picked[29]);
    }

    [Fact]
    public void ProjectilesCappedSoEnemiesStillShow()
    {
        var items = Enumerable.Range(0, 40).Select(i => new E($"p{i}", EntityTier.Threat, i * 0.1, Proj: true))
            .Append(new E("eye", EntityTier.Threat, 30)).ToList();
        var picked = Pick(items);
        Assert.Equal(EntityRanking.MaxProjectiles + 1, picked.Count);
        Assert.Contains("eye", picked);
        Assert.Equal("p0", picked[0]);
    }

    [Fact]
    public void BossSegmentsCappedToNearest()
    {
        var items = Enumerable.Range(0, 50).Select(i => new E($"seg{i}", EntityTier.Boss, 50 - i))
            .Append(new E("guide", EntityTier.Other, 2)).ToList();
        var picked = Pick(items);
        Assert.Equal(EntityRanking.MaxBosses + 1, picked.Count);
        Assert.Equal("seg49", picked[0]);
        Assert.Equal("guide", picked[^1]);
    }

    [Fact]
    public void RespectsMax()
    {
        var items = Enumerable.Range(0, 5).Select(i => new E($"b{i}", EntityTier.Boss, i));
        Assert.Equal(["b0", "b1"], Pick(items, max: 2));
    }
}

public class BossSummaryTests
{
    [Fact]
    public void RealBossesListedIndividually_SegmentsMergedByName()
    {
        var s = new[]
        {
            new BossSighting(1, "Eater of Worlds", 65, 65, 10, 10, 30, Segment: true),
            new BossSighting(2, "Eater of Worlds", 150, 150, 12, 10, 20, Segment: true),
            new BossSighting(3, "Eater of Worlds", 220, 220, 14, 10, 25, Segment: true),
            new BossSighting(7, "Eye of Cthulhu", 2000, 2800, 50, 5, 40, Segment: false),
        };
        var r = BossSummary.Collapse(s);
        Assert.Equal(2, r.Count);
        Assert.Equal(new BossStatus(2, "Eater of Worlds", 435, 435, 12, 10, 20, 3), r[0]);
        Assert.Equal(new BossStatus(7, "Eye of Cthulhu", 2000, 2800, 50, 5, 40), r[1]);
    }

    [Fact]
    public void CapsAndSortsByDistance()
    {
        var s = Enumerable.Range(0, 8).Select(i => new BossSighting(i, $"B{i}", 1, 1, 0, 0, 100 - i, false));
        var r = BossSummary.Collapse(s);
        Assert.Equal(BossSummary.MaxBosses, r.Count);
        Assert.Equal(7, r[0].Id);
    }

    [Fact]
    public void SegmentsOmittedFromJsonForRealBosses()
    {
        var json = JsonSerializer.Serialize(new BossStatus(7, "King Slime", 1, 2, 3, 4, 5.5), WireProtocol.Json);
        Assert.DoesNotContain("segments", json);
        Assert.Contains("\"lifeMax\":2", json);
    }
}

public class ItemTagsTests
{
    [Fact]
    public void WeaponGetsDamage()
    {
        var t = ItemTags.Compute(new ItemTagInput(IsWeapon: true, WeaponDamage: 13));
        Assert.Equal(new ItemTagSet(true, 13, null, null, null, null, null, null, null), t);
    }

    [Fact]
    public void PotionHealsAndIsConsumable()
    {
        var t = ItemTags.Compute(new ItemTagInput(HealLife: 50, Consumable: true));
        Assert.Equal(50, t.Heal);
        Assert.True(t.Consumable);
        Assert.Null(t.Weapon);
    }

    [Fact]
    public void BlocksArePlaceableNotConsumable()
    {
        var t = ItemTags.Compute(new ItemTagInput(Consumable: true, Places: true));
        Assert.True(t.Placeable);
        Assert.Null(t.Consumable);
    }

    [Fact]
    public void AmmoIsNotConsumableTag()
    {
        var t = ItemTags.Compute(new ItemTagInput(IsAmmo: true, Consumable: true));
        Assert.True(t.Ammo);
        Assert.Null(t.Consumable);
    }

    [Fact]
    public void ToolPowers_AxeScaledLikeTooltip()
    {
        var t = ItemTags.Compute(new ItemTagInput(Pick: 35, Axe: 7, Hammer: 0));
        Assert.Equal(35, t.Pick);
        Assert.Equal(35, t.Axe);
        Assert.Null(t.Hammer);
    }

    [Fact]
    public void PlainMaterialHasNoTags() =>
        Assert.Equal(new ItemTagSet(null, null, null, null, null, null, null, null, null), ItemTags.Compute(new ItemTagInput()));
}

public class ThreatsTests
{
    [Fact]
    public void NoneNearIsNull()
    {
        Assert.Null(Threats.Summarize([]));
        Assert.Null(Threats.Summarize([41, 100]));
    }

    [Fact]
    public void CountsWithinRadiusAndNearest()
    {
        var t = Threats.Summarize([12.34, 40, 5.06, 80]);
        Assert.Equal(new ThreatSummary(3, 5.1), t);
    }
}
