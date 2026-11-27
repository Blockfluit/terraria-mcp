using LlmPlayer.Core.Combat;
using LlmPlayer.Core.Grid;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class TargetRulesTests
{
    private static NpcTraits Zombie(int id = 1, float x = 0, float y = 0) =>
        new(id, Friendly: false, TownNpc: false, Critter: false, DontTakeDamage: false, Immortal: false, LifeMax: 45, X: x, Y: y);

    [Fact]
    public void HostileNpc_IsAttackable() => Assert.Equal(TargetVerdict.Ok, TargetRules.Check(Zombie()));

    [Fact]
    public void TownNpc_Refused() => Assert.Equal(TargetVerdict.TownNpc, TargetRules.Check(Zombie() with { TownNpc = true, Friendly = true }));

    [Fact]
    public void Critter_Refused() => Assert.Equal(TargetVerdict.Critter, TargetRules.Check(Zombie() with { Critter = true }));

    [Fact]
    public void FriendlyNonTown_Refused() => Assert.Equal(TargetVerdict.Friendly, TargetRules.Check(Zombie() with { Friendly = true }));

    [Fact]
    public void Invulnerable_Refused()
    {
        Assert.Equal(TargetVerdict.Invulnerable, TargetRules.Check(Zombie() with { DontTakeDamage = true }));
        Assert.Equal(TargetVerdict.Invulnerable, TargetRules.Check(Zombie() with { Immortal = true }));
    }

    [Fact]
    public void TinyLifeMax_IsNotATarget() => Assert.Equal(TargetVerdict.Invulnerable, TargetRules.Check(Zombie() with { LifeMax = 5 }));

    [Fact]
    public void TownCheckWinsOverCritter() =>
        Assert.Equal(TargetVerdict.TownNpc, TargetRules.Check(Zombie() with { TownNpc = true, Critter = true }));

    [Fact]
    public void PickNearest_SkipsNonAttackableAndOutOfRange()
    {
        var npcs = new[]
        {
            Zombie(1, 3, 0) with { TownNpc = true },  // nearest but town
            Zombie(2, 0, 2) with { Critter = true },  // critter
            Zombie(3, 6, 0),
            Zombie(4, -5, 0),                          // nearest hostile
            Zombie(5, 50, 0),
        };
        Assert.Equal(4, TargetRules.PickNearest(npcs, 0, 0, maxRange: 20)?.Id);
        Assert.Null(TargetRules.PickNearest(npcs, 0, 0, maxRange: 4));
        Assert.Null(TargetRules.PickNearest(Array.Empty<NpcTraits>(), 0, 0, 20));
    }
}

public class WeaponScoringTests
{
    private static WeaponCandidate Sword(int slot, int damage, int anim) =>
        new(slot, damage, UseTime: anim, UseAnimation: anim, ReuseDelay: 0, CritChance: 4, AutoReuse: true, Channel: false,
            WeaponStyle.Melee, ShootSpeed: 0, ReachTiles: 3, Usable: true);

    private static WeaponCandidate Bow(int slot, int damage, int useTime, bool usable = true) =>
        new(slot, damage, useTime, useTime, 0, 4, AutoReuse: false, Channel: false, WeaponStyle.Ranged, 7f, 15f, usable);

    [Fact]
    public void Dps_UsesDamageCritAndRate()
    {
        // 10 dmg, 4% crit, swing every 20 ticks → 10 * 1.04 * 3 = 31.2
        Assert.Equal(31.2, WeaponScoring.Dps(Sword(0, 10, 20)), 3);
    }

    [Fact]
    public void Dps_NonAutoReuseNeedsOneReleaseTick()
    {
        // 25 ticks + 1 release tick → 10*1.04*60/26
        Assert.Equal(10 * 1.04 * 60 / 26, WeaponScoring.Dps(Bow(0, 10, 25)), 3);
    }

    [Fact]
    public void Dps_BurstWeaponsCountShotsPerAnimation()
    {
        // useAnimation 12, useTime 4 → 3 shots per 12-tick animation (+ reuseDelay 10)
        var burst = Bow(0, 10, 4) with { UseAnimation = 12, ReuseDelay = 10, AutoReuse = true, CritChance = 0 };
        Assert.Equal(10 * 3 * 60 / 22.0, WeaponScoring.Dps(burst), 3);
    }

    [Fact]
    public void Best_PicksHighestDpsAmongUsable()
    {
        var weapons = new[] { Sword(0, 8, 25), Bow(1, 30, 20, usable: false), Bow(2, 12, 15), Sword(3, 9, 25) };
        Assert.Equal(2, WeaponScoring.Best(weapons)?.Slot);
    }

    [Fact]
    public void Best_IgnoresZeroDamage_AndReturnsNullWhenNothingUsable()
    {
        Assert.Null(WeaponScoring.Best(new[] { Sword(0, 0, 20), Bow(1, 20, 20, usable: false) }));
    }

    [Fact]
    public void Best_PrefersABowOverAShortswordOfSimilarDps()
    {
        // Copper Shortsword: 6 dmg, 12-tick animation, 2.5-tile reach; Copper Bow + Wooden Arrow: 11 dmg, 27 ticks.
        var shortsword = new WeaponCandidate(0, 6, 12, 12, 0, 4, AutoReuse: false, Channel: false, WeaponStyle.Melee, 0f, 2.5f, true);
        var bow = new WeaponCandidate(1, 11, 27, 27, 0, 4, AutoReuse: false, Channel: false, WeaponStyle.Ranged, 7f, 15f, true);
        Assert.True(WeaponScoring.Dps(shortsword) > WeaponScoring.Dps(bow) * 0.9);
        Assert.Equal(1, WeaponScoring.Best(new[] { shortsword, bow })?.Slot);
        // A much stronger sword still wins.
        var broadsword = shortsword with { Slot = 2, Damage = 20, ReachTiles = 3.5f };
        Assert.Equal(2, WeaponScoring.Best(new[] { broadsword, bow })?.Slot);
    }

    [Fact]
    public void Best_AccountsForTargetDefense()
    {
        // Iron Bow + Wooden Arrow (13 per shot, 27 ticks) vs Wand of Sparking (8, autoReuse ~ fast): against the Eye of
        // Cthulhu's 12 defense the bow must win.
        var bow = new WeaponCandidate(0, 13, 27, 27, 0, 4, AutoReuse: true, Channel: false, WeaponStyle.Ranged, 7f, 15f, true);
        var wand = new WeaponCandidate(1, 8, 12, 12, 0, 4, AutoReuse: true, Channel: false, WeaponStyle.Ranged, 8f, 15f, true);
        Assert.Equal(1, WeaponScoring.Best(new[] { bow, wand })?.Slot);          // no defense: the fast wand
        Assert.Equal(0, WeaponScoring.Best(new[] { bow, wand }, 12)?.Slot);      // 12 defense: the bow
    }

    [Fact]
    public void Best_TieGoesToLowerSlot()
    {
        Assert.Equal(1, WeaponScoring.Best(new[] { Sword(4, 10, 20), Sword(1, 10, 20) })?.Slot);
    }

    [Fact]
    public void MeleeReach_FromItemSizeAndScale()
    {
        Assert.Equal(3f, WeaponScoring.MeleeReachTiles(sizePx: 32, scale: 1f, projectileMelee: false), 3);
        Assert.Equal(4f, WeaponScoring.MeleeReachTiles(48, 1f, false), 3);
        Assert.Equal(2f, WeaponScoring.MeleeReachTiles(8, 1f, false), 3);     // never below 2
        Assert.Equal(4f, WeaponScoring.MeleeReachTiles(16, 1f, true), 3);     // spears/yoyos/flails reach further
    }
}

public class CombatStanceTests
{
    [Theory]
    [InlineData(2f, 0f, StanceMove.Hold)]        // in reach
    [InlineData(5f, 1f, StanceMove.StepToward)]  // close, same level: walk straight at it
    [InlineData(5f, -4f, StanceMove.PathToward)] // close but well above: pathfind
    [InlineData(20f, 0f, StanceMove.PathToward)] // far
    public void Melee(float dx, float dy, StanceMove expected) =>
        Assert.Equal(expected, CombatStance.Decide(WeaponStyle.Melee, dx, dy, reachTiles: 3, lineOfSight: true));

    [Theory]
    [InlineData(10f, 0f, true, StanceMove.Hold)]
    [InlineData(8f, 0f, true, StanceMove.Hold)]        // band is inclusive
    [InlineData(15f, 0f, true, StanceMove.Hold)]
    [InlineData(-4f, 0f, true, StanceMove.StepAway)]   // too close
    [InlineData(20f, 0f, true, StanceMove.PathToward)] // too far
    [InlineData(10f, 0f, false, StanceMove.PathToward)] // in band but no line of sight
    public void Ranged(float dx, float dy, bool los, StanceMove expected) =>
        Assert.Equal(expected, CombatStance.Decide(WeaponStyle.Ranged, dx, dy, reachTiles: 15, lineOfSight: los));

    [Fact]
    public void ShouldFire_MeleeOnlyNearReach_RangedNeedsLineOfSight()
    {
        Assert.True(CombatStance.ShouldFire(WeaponStyle.Melee, 3.5f, 3, lineOfSight: false));
        Assert.False(CombatStance.ShouldFire(WeaponStyle.Melee, 6f, 3, true));
        Assert.True(CombatStance.ShouldFire(WeaponStyle.Ranged, 18f, 15, true));
        Assert.False(CombatStance.ShouldFire(WeaponStyle.Ranged, 10f, 15, false));
        Assert.False(CombatStance.ShouldFire(WeaponStyle.Ranged, 40f, 15, true));
    }
}

public class AimLeadTests
{
    [Fact]
    public void StationaryTarget_AimsAtCenter()
    {
        var (x, y) = AimLead.AimPoint(0, 0, 160, 0, 0, 0, projectileSpeed: 8);
        Assert.Equal((160f, 0f), (x, y));
    }

    [Fact]
    public void MovingTarget_LeadsByTravelTime()
    {
        // 160 px at 8 px/tick → 20 ticks; target moves 2 px/tick right → aim 40 px ahead
        var (x, y) = AimLead.AimPoint(0, 0, 160, 0, 2, -1, 8);
        Assert.Equal(200f, x, 3);
        Assert.Equal(-20f, y, 3);
    }

    [Fact]
    public void Lead_IsCapped()
    {
        // 1600 px at 8 px/tick = 200 ticks, capped to MaxLeadTicks
        var (x, _) = AimLead.AimPoint(0, 0, 1600, 0, 1, 0, 8);
        Assert.Equal(1600f + AimLead.MaxLeadTicks, x, 3);
    }

    [Fact]
    public void NoProjectile_NoLead()
    {
        var (x, y) = AimLead.AimPoint(0, 0, 100, 50, 5, 5, projectileSpeed: 0);
        Assert.Equal((100f, 50f), (x, y));
    }
}

public class StepSafetyTests
{
    // Player stands at S (feet tile); ground row below.
    [Fact]
    public void FlatGround_IsSafeBothWays()
    {
        var (g, m) = ArrayTileGrid.Parse(
            "........",
            "........",
            "...S....",
            "########");
        Assert.True(StepSafety.IsSafe(g, m['S'], +1));
        Assert.True(StepSafety.IsSafe(g, m['S'], -1));
    }

    [Fact]
    public void Cliff_IsUnsafe()
    {
        var (g, m) = ArrayTileGrid.Parse(
            "........",
            "........",
            "...S....",
            "####....",
            "........",
            "........",
            "........",
            "........",
            "........");
        Assert.False(StepSafety.IsSafe(g, m['S'], +1));
        Assert.True(StepSafety.IsSafe(g, m['S'], -1));
    }

    [Fact]
    public void ShortDrop_IsSafe()
    {
        var (g, m) = ArrayTileGrid.Parse(
            "........",
            "........",
            "...S....",
            "####....",
            "........",
            "########");
        Assert.True(StepSafety.IsSafe(g, m['S'], +1));
    }

    [Fact]
    public void LavaOrSpikesAhead_IsUnsafe()
    {
        var (g, m) = ArrayTileGrid.Parse(
            "........",
            "........",
            "...S.L..",
            "########");
        Assert.False(StepSafety.IsSafe(g, m['S'], +1));
        var (g2, m2) = ArrayTileGrid.Parse(
            "........",
            "........",
            "...S....",
            "#####^##");
        Assert.False(StepSafety.IsSafe(g2, m2['S'], +1));
    }
}
