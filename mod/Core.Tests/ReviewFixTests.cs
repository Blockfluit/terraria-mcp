using LlmPlayer.Core.Combat;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class ItemRulesTests
{
    private static ItemTraits Sword => new(Damage: 10);

    [Fact] public void Sword_IsWeapon() => Assert.True(ItemRules.IsWeapon(Sword));

    [Theory]
    [InlineData(35, 0, 0)]
    [InlineData(0, 10, 0)]
    [InlineData(0, 0, 25)]
    public void DamagingTools_AreNotWeapons(int pick, int axe, int hammer) =>
        Assert.False(ItemRules.IsWeapon(Sword with { Pick = pick, Axe = axe, Hammer = hammer }));

    [Fact] public void Explosive_IsNotWeapon() => Assert.False(ItemRules.IsWeapon(Sword with { ShootsExplosive = true }));

    [Fact] public void Placeable_Ammo_Potion_NotWeapons()
    {
        Assert.False(ItemRules.IsWeapon(Sword with { Places = true }));
        Assert.False(ItemRules.IsWeapon(Sword with { IsAmmo = true }));
        Assert.False(ItemRules.IsWeapon(Sword with { Heals = true }));
        Assert.False(ItemRules.IsWeapon(new ItemTraits(Damage: 0)));
    }

    [Fact] public void Explosives_DestroyTiles() =>
        Assert.True(ItemRules.DestroysTiles(new ItemTraits(Damage: 0, ShootsExplosive: true)));

    [Fact] public void Potion_DoesNotDestroyTiles() =>
        Assert.False(ItemRules.DestroysTiles(new ItemTraits(Damage: 0, Heals: true)));
}

public class SupportRulesTests
{
    [Fact]
    public void BlockUnderProtectedObject_IsSupport()
    {
        bool Obj(int x, int y) => x == 5 && y == 4; // e.g. a work bench at (5,4)
        Assert.True(SupportRules.SupportsProtectedObject(Obj, 5, 5));  // below
        Assert.True(SupportRules.SupportsProtectedObject(Obj, 4, 4));  // beside
        Assert.False(SupportRules.SupportsProtectedObject(Obj, 7, 5)); // unrelated
    }

    [Fact]
    public void ObjectTileItself_IsNotCountedAsSupport()
    {
        bool Obj(int x, int y) => x == 5 && y == 4;
        Assert.False(SupportRules.SupportsProtectedObject(Obj, 5, 4));
    }
}

public class ProgressGuardTests
{
    [Fact]
    public void ArrivalsThatGetCloser_NeverFail()
    {
        var g = new ProgressGuard(maxStalls: 3);
        foreach (var d in new[] { 100, 80, 60, 40, 20 }) Assert.False(g.RecordArrival(d));
    }

    [Fact]
    public void RepeatedArrivalsWithoutProgress_FailAfterMaxStalls()
    {
        var g = new ProgressGuard(maxStalls: 3);
        Assert.False(g.RecordArrival(50));
        Assert.False(g.RecordArrival(50));
        Assert.False(g.RecordArrival(51));
        Assert.True(g.RecordArrival(50));
    }

    [Fact]
    public void ProgressResetsStallCount()
    {
        var g = new ProgressGuard(maxStalls: 2);
        g.RecordArrival(50); g.RecordArrival(50);
        Assert.False(g.RecordArrival(40));
        Assert.False(g.RecordArrival(40));
        Assert.True(g.RecordArrival(40));
    }
}
