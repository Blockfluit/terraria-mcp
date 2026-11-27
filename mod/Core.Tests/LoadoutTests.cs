using LlmPlayer.Core.Combat;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class AmmoOrderTests
{
    private const int Arrow = 40, Bullet = 97;
    private const int WoodenArrow = 40, FlamingArrow = 41, FrostburnArrow = 988, MusketBall = 97;

    private static AmmoSlot[] Inv(params (int Slot, int Ammo, int Item, int Stack)[] items)
    {
        var a = new AmmoSlot[AmmoOrder.SearchedSlots];
        foreach (var (slot, ammo, item, stack) in items) a[slot] = new AmmoSlot(ammo, item, stack);
        return a;
    }

    [Fact]
    public void VanillaOrder_AmmoSlotsFirst_ThenInventory()
    {
        var order = AmmoOrder.VanillaOrder().ToList();
        Assert.Equal(new[] { 54, 55, 56, 57, 0, 1 }, order.Take(6));
        Assert.Equal(58, order.Count);
        Assert.Equal(53, order[^1]);
    }

    [Fact]
    public void FirstUsed_PrefersAmmoSlotOverHotbar()
    {
        var inv = Inv((2, Arrow, FlamingArrow, 10), (56, Arrow, WoodenArrow, 99), (55, Bullet, MusketBall, 50));
        Assert.Equal(56, AmmoOrder.FirstUsed(inv, Arrow));
        Assert.Equal(55, AmmoOrder.FirstUsed(inv, Bullet));
        Assert.Equal(-1, AmmoOrder.FirstUsed(inv, 999));
    }

    [Fact]
    public void Plan_SwapsWantedStackToTheFront()
    {
        var inv = Inv((54, Arrow, WoodenArrow, 99), (20, Arrow, FlamingArrow, 30));
        var plan = AmmoOrder.Plan(inv, Arrow, FlamingArrow);
        Assert.Equal(new AmmoPlan(AmmoPlanKind.Swap, 54, 20), plan);
    }

    [Fact]
    public void Plan_ReadyWhenAlreadyFirst_IgnoresOtherClasses()
    {
        var inv = Inv((54, Bullet, MusketBall, 10), (55, Arrow, FlamingArrow, 30), (56, Arrow, WoodenArrow, 99));
        Assert.Equal(AmmoPlanKind.Ready, AmmoOrder.Plan(inv, Arrow, FlamingArrow).Kind);
    }

    [Fact]
    public void Plan_MissingAndWrongAmmo()
    {
        var inv = Inv((54, Arrow, WoodenArrow, 99), (55, Bullet, MusketBall, 10), (56, Arrow, FlamingArrow, 0));
        Assert.Equal(AmmoPlanKind.Missing, AmmoOrder.Plan(inv, Arrow, FlamingArrow).Kind); // empty stack doesn't count
        Assert.Equal(AmmoPlanKind.WrongAmmo, AmmoOrder.Plan(inv, Arrow, MusketBall).Kind);
    }

    [Fact]
    public void Alternative_IsTheNextOtherMatchingStack()
    {
        var inv = Inv((54, Arrow, FlamingArrow, 0), (55, Bullet, MusketBall, 10), (30, Arrow, FrostburnArrow, 5), (3, Arrow, WoodenArrow, 50));
        Assert.Equal(3, AmmoOrder.Alternative(inv, Arrow, FlamingArrow)); // inventory 0-53 in order: slot 3 before 30
        Assert.Equal(-1, AmmoOrder.Alternative(inv, Bullet, MusketBall));
    }

    [Fact]
    public void PlanThenSwap_MakesWantedFirst_AndJournalRestoresOrder()
    {
        var items = new object?[58];
        var wooden = new object(); var flaming = new object(); var frost = new object();
        items[54] = wooden; items[55] = frost; items[12] = flaming;
        var inv = Inv((54, Arrow, WoodenArrow, 99), (55, Arrow, FrostburnArrow, 20), (12, Arrow, FlamingArrow, 30));

        var plan = AmmoOrder.Plan(inv, Arrow, FlamingArrow);
        Assert.Equal(AmmoPlanKind.Swap, plan.Kind);
        var journal = new SwapJournal<object>();
        journal.Swap(items!, plan.A, plan.B);
        (inv[plan.A], inv[plan.B]) = (inv[plan.B], inv[plan.A]);
        Assert.Same(flaming, items[54]);
        Assert.Equal(54, AmmoOrder.FirstUsed(inv, Arrow));
        Assert.Equal(FlamingArrow, inv[AmmoOrder.FirstUsed(inv, Arrow)].ItemType);

        Assert.Equal(1, journal.Restore(items!));
        Assert.Same(wooden, items[54]);
        Assert.Same(flaming, items[12]);
        Assert.Same(frost, items[55]);
        Assert.Equal(0, journal.Count);
    }

    [Fact]
    public void Journal_SkipsSwapsWhoseItemsMoved_AndUndoesNewestFirst()
    {
        var a = new object(); var b = new object(); var c = new object(); var d = new object();
        var slots = new object?[] { a, b, c };
        var j = new SwapJournal<object>();
        j.Swap(slots!, 0, 1); // b a c
        j.Swap(slots!, 0, 2); // c a b
        Assert.Equal(2, j.Restore(slots!));
        Assert.Equal(new[] { a, b, c }, slots);

        j.Swap(slots!, 0, 1); // b a c
        slots[1] = d;         // the player moved something into slot 1
        Assert.Equal(0, j.Restore(slots!));
        Assert.Same(b, slots[0]);
        Assert.Same(d, slots[1]);
    }
}

public class WeaponFallbackTests
{
    private static WeaponCandidate Bow(int slot, int damage = 10, bool usable = true) =>
        new(slot, damage, 20, 20, 0, 4, true, false, WeaponStyle.Ranged, 7f, 15f, usable);

    private static WeaponCandidate Sword(int slot, int damage = 12) =>
        new(slot, damage, 20, 20, 0, 4, true, false, WeaponStyle.Melee, 0f, 4f, true);

    [Fact]
    public void Diagnose_OrderOfReasons()
    {
        Assert.Equal(WeaponIssue.Gone, WeaponFallback.Diagnose(false, true, false, true, false, false));
        Assert.Equal(WeaponIssue.NoAmmo, WeaponFallback.Diagnose(true, true, false, true, false, false));
        Assert.Equal(WeaponIssue.NoMana, WeaponFallback.Diagnose(true, false, true, true, false, false));
        Assert.Equal(WeaponIssue.Unusable, WeaponFallback.Diagnose(true, false, true, false, true, false));
        Assert.Equal(WeaponIssue.None, WeaponFallback.Diagnose(true, true, true, false, true, true));
    }

    [Fact]
    public void UsableChosenWeapon_IsUsedEvenIfAnotherScoresHigher()
    {
        var d = WeaponFallback.Decide("Wooden Bow", 3, WeaponIssue.None, true, new[] { Bow(3, 5), Sword(0, 50) });
        Assert.Equal(new WeaponDecision(WeaponDecisionKind.UseChosen, 3), d);
    }

    [Fact]
    public void Unusable_FallsBackToBestOther_DefenseAware()
    {
        // Against defense 12 the fast low-damage wand (8) does ~2 per hit; the 14-damage bow does 8.
        var wand = new WeaponCandidate(5, 8, 10, 10, 0, 4, true, false, WeaponStyle.Ranged, 8f, 15f, true);
        var d = WeaponFallback.Decide("Copper Bow", 3, WeaponIssue.NoAmmo, true,
            new[] { Bow(3, 20, usable: false), wand, Bow(7, 14) }, targetDefense: 12);
        Assert.Equal(WeaponDecisionKind.Fallback, d.Kind);
        Assert.Equal(7, d.Slot);
        Assert.Contains("out of ammo", d.Reason);
    }

    [Fact]
    public void Fallback_PrefersTheStrategysStyle()
    {
        var others = new[] { Sword(0, 40), Bow(4, 10) };
        Assert.Equal(4, WeaponFallback.Decide("Bow", 2, WeaponIssue.NoAmmo, true, others, prefer: WeaponStyle.Ranged).Slot);
        Assert.Equal(0, WeaponFallback.Decide("Bow", 2, WeaponIssue.NoAmmo, true, others).Slot);
        // No weapon of the preferred style: any style.
        Assert.Equal(0, WeaponFallback.Decide("Bow", 2, WeaponIssue.NoAmmo, true, new[] { Sword(0) }, prefer: WeaponStyle.Ranged).Slot);
    }

    [Fact]
    public void FallbackOff_Stops_WithTheReason()
    {
        var d = WeaponFallback.Decide("Wand of Sparking", 1, WeaponIssue.NoMana, false, new[] { Sword(0) });
        Assert.Equal(WeaponDecisionKind.Stop, d.Kind);
        Assert.Equal("Wand of Sparking unusable (out of mana), fallback is off", d.Reason);
    }

    [Fact]
    public void NothingElse_Stops()
    {
        var d = WeaponFallback.Decide("Bow", 1, WeaponIssue.NoAmmo, true, new[] { Bow(1, usable: false) });
        Assert.Equal(WeaponDecisionKind.Stop, d.Kind);
        Assert.Contains("no other usable weapon", d.Reason);
    }

    [Fact]
    public void ChosenWeapon_NeverPickedAsItsOwnFallback()
    {
        // Gone: the slot may now hold something else, which is a fair candidate.
        Assert.Equal(1, WeaponFallback.Decide("Bow", 1, WeaponIssue.Gone, true, new[] { Sword(1) }).Slot);
        // Out of mana but still "usable" by the candidate's view: skipped by slot.
        Assert.Equal(WeaponDecisionKind.Stop, WeaponFallback.Decide("Wand", 1, WeaponIssue.NoMana, true, new[] { Bow(1) }).Kind);
    }

    [Fact]
    public void Messages()
    {
        Assert.Equal("attack: Wooden Bow unusable (out of ammo), switched to Copper Shortsword",
            WeaponFallback.SwitchMessage("Wooden Bow", WeaponIssue.NoAmmo, "Copper Shortsword"));
        Assert.Equal("auto-defend: Wooden Bow unusable (no longer in the inventory), switched to Copper Shortsword",
            WeaponFallback.SwitchMessage("Wooden Bow", WeaponIssue.Gone, "Copper Shortsword", "auto-defend"));
        Assert.Equal("attack: Flaming Arrow ran out, firing Wooden Arrow", WeaponFallback.AmmoSwitchMessage("Flaming Arrow", "Wooden Arrow"));
    }

    [Fact]
    public void MayReturn_ManaNeedsSeveralCasts()
    {
        Assert.True(WeaponFallback.MayReturn(WeaponIssue.NoAmmo, 0, 20, 5));
        Assert.False(WeaponFallback.MayReturn(WeaponIssue.NoMana, 6, 100, 5));
        Assert.True(WeaponFallback.MayReturn(WeaponIssue.NoMana, 20, 100, 5));
        Assert.True(WeaponFallback.MayReturn(WeaponIssue.NoMana, 20, 20, 8)); // a full small bar is enough
    }
}

public class StrategyTests
{
    [Theory]
    [InlineData("auto", AttackStrategy.Auto)]
    [InlineData("KITE", AttackStrategy.Kite)]
    [InlineData(" hit_and_run ", AttackStrategy.HitAndRun)]
    [InlineData("hold", AttackStrategy.Hold)]
    [InlineData("flee_and_shoot", AttackStrategy.FleeAndShoot)]
    public void Parse(string s, AttackStrategy want)
    {
        Assert.True(Strategies.TryParse(s, out var got));
        Assert.Equal(want, got);
        Assert.Equal(s.Trim().ToLowerInvariant(), Strategies.Name(got));
    }

    [Fact]
    public void Parse_RejectsUnknown() => Assert.False(Strategies.TryParse("charge", out _));

    [Fact]
    public void WeaponStyleChecks()
    {
        Assert.Null(Strategies.CheckWeapon(AttackStrategy.Kite, WeaponStyle.Ranged, "Bow"));
        Assert.Contains("ranged", Strategies.CheckWeapon(AttackStrategy.Kite, WeaponStyle.Melee, "Sword"));
        Assert.Contains("melee", Strategies.CheckWeapon(AttackStrategy.HitAndRun, WeaponStyle.Ranged, "Bow"));
        Assert.Null(Strategies.CheckWeapon(AttackStrategy.Hold, WeaponStyle.Melee, "Sword"));
        Assert.Null(Strategies.CheckWeapon(AttackStrategy.Auto, WeaponStyle.Ranged, "Bow"));
        // After a fallback to the other style the strategy runs as auto.
        Assert.Equal(AttackStrategy.Auto, Strategies.Effective(AttackStrategy.Kite, WeaponStyle.Melee));
        Assert.Equal(AttackStrategy.Hold, Strategies.Effective(AttackStrategy.Hold, WeaponStyle.Melee));
    }

    [Fact]
    public void Auto_MatchesCombatStance()
    {
        foreach (var (dx, dy, los) in new[] { (3f, 0f, true), (10f, 0f, true), (20f, 0f, true), (10f, 0f, false), (5f, 1f, true), (30f, 5f, true) })
        {
            Assert.Equal(CombatStance.Decide(WeaponStyle.Ranged, dx, dy, 15f, los),
                Strategies.Decide(AttackStrategy.Auto, WeaponStyle.Ranged, dx, dy, 15f, los, 12f));
            Assert.Equal(CombatStance.Decide(WeaponStyle.Melee, dx, dy, 3f, los),
                Strategies.Decide(AttackStrategy.Auto, WeaponStyle.Melee, dx, dy, 3f, los, 12f));
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            Assert.Equal(CombatStance.ShouldFire(WeaponStyle.Ranged, dist, 15f, los),
                Strategies.ShouldFire(AttackStrategy.Auto, WeaponStyle.Ranged, dist, 15f, los, 12f));
        }
    }

    [Fact]
    public void Kite_KeepsItsDistanceBand()
    {
        const float keep = 12f; // band 10..15
        Assert.Equal(StanceMove.StepAway, Strategies.Decide(AttackStrategy.Kite, WeaponStyle.Ranged, 9f, 0f, 15f, true, keep));
        Assert.Equal(StanceMove.Hold, Strategies.Decide(AttackStrategy.Kite, WeaponStyle.Ranged, 12f, 0f, 15f, true, keep));
        Assert.Equal(StanceMove.PathToward, Strategies.Decide(AttackStrategy.Kite, WeaponStyle.Ranged, 16f, 0f, 15f, true, keep));
        Assert.True(Strategies.KeepsMoving(AttackStrategy.Kite));
        Assert.False(Strategies.KeepsMoving(AttackStrategy.Auto));
        // A wider keep_distance widens the band and the fire range.
        Assert.Equal(StanceMove.StepAway, Strategies.Decide(AttackStrategy.Kite, WeaponStyle.Ranged, 15f, 0f, 15f, true, 20f));
        Assert.True(Strategies.ShouldFire(AttackStrategy.Kite, WeaponStyle.Ranged, 27f, 15f, true, 20f));
    }

    [Fact]
    public void Hold_NeverMoves()
    {
        foreach (var dx in new[] { 1f, 10f, 40f })
            Assert.Equal(StanceMove.Hold, Strategies.Decide(AttackStrategy.Hold, WeaponStyle.Melee, dx, 0f, 3f, false, 12f));
    }

    [Fact]
    public void FleeAndShoot_RunsAwayUntilFar_ThenShoots()
    {
        Assert.Equal(StanceMove.StepAway, Strategies.Decide(AttackStrategy.FleeAndShoot, WeaponStyle.Ranged, 15f, 0f, 15f, true, 12f));
        Assert.Equal(StanceMove.Hold, Strategies.Decide(AttackStrategy.FleeAndShoot, WeaponStyle.Ranged, 19f, 0f, 15f, true, 12f));
        Assert.Equal(StanceMove.PathToward, Strategies.Decide(AttackStrategy.FleeAndShoot, WeaponStyle.Ranged, 30f, 0f, 15f, true, 12f));
        Assert.True(Strategies.ShouldFire(AttackStrategy.FleeAndShoot, WeaponStyle.Ranged, 15f, 15f, true, 12f));
    }

    [Fact]
    public void HitAndRun_BacksOffWhileTheRhythmSaysSo()
    {
        Assert.Equal(StanceMove.StepAway, Strategies.Decide(AttackStrategy.HitAndRun, WeaponStyle.Melee, 2f, 0f, 3f, true, 12f, backOff: true));
        Assert.Equal(StanceMove.Hold, Strategies.Decide(AttackStrategy.HitAndRun, WeaponStyle.Melee, 2f, 0f, 3f, true, 12f));
        Assert.Equal(StanceMove.StepToward, Strategies.Decide(AttackStrategy.HitAndRun, WeaponStyle.Melee, 5f, 0f, 3f, true, 12f));
    }

    [Fact]
    public void Rhythm_SwingInReach_BacksOffUntilClearOrTimeUp()
    {
        var r = new HitAndRunRhythm();
        Assert.False(r.Tick(false, 2f, 3f));
        Assert.False(r.Tick(true, 8f, 3f));  // swing out of reach: no back-off
        Assert.True(r.Tick(true, 2f, 3f));   // swing in reach
        Assert.True(r.Tick(false, 4f, 3f));
        Assert.False(r.Tick(false, 6f, 3f)); // 3 + 2.5 clear
        Assert.True(r.Tick(true, 2f, 3f));
        int ticks = 1;
        while (r.Tick(false, 2f, 3f)) ticks++;
        Assert.Equal(HitAndRunRhythm.BackoffTicks, ticks);
    }

    [Fact]
    public void BossPlan_PerStrategy()
    {
        // Boss to the right (dx +10), kiter running left (-1).
        Assert.Equal(-1, Strategies.BossPlan(AttackStrategy.Auto, WeaponStyle.Ranged, 10f, 10f, 15f, 12f, false, -1, true, true));
        Assert.Equal(1, Strategies.BossPlan(AttackStrategy.Auto, WeaponStyle.Melee, 10f, 10f, 3f, 12f, false, 0, true, true));
        Assert.Equal(0, Strategies.BossPlan(AttackStrategy.Hold, WeaponStyle.Ranged, 10f, 10f, 15f, 12f, false, -1, true, true));
        Assert.Equal(-1, Strategies.BossPlan(AttackStrategy.FleeAndShoot, WeaponStyle.Ranged, 10f, 10f, 15f, 12f, false, 1, true, true));
        // Flee blocked on the left: follows the kiter instead.
        Assert.Equal(1, Strategies.BossPlan(AttackStrategy.FleeAndShoot, WeaponStyle.Ranged, 10f, 10f, 15f, 12f, false, 1, false, true));
        // Kite: inside the band runs away; outside follows the kiter (which may run toward).
        Assert.Equal(-1, Strategies.BossPlan(AttackStrategy.Kite, WeaponStyle.Ranged, 5f, 5f, 15f, 12f, false, 1, true, true));
        Assert.Equal(1, Strategies.BossPlan(AttackStrategy.Kite, WeaponStyle.Ranged, 12f, 12f, 15f, 12f, false, 1, true, true));
        // Hit and run: toward, away while backing off, standing when away is blocked.
        Assert.Equal(1, Strategies.BossPlan(AttackStrategy.HitAndRun, WeaponStyle.Melee, 5f, 5f, 3f, 12f, false, 0, true, true));
        Assert.Equal(-1, Strategies.BossPlan(AttackStrategy.HitAndRun, WeaponStyle.Melee, 1f, 1f, 3f, 12f, true, 0, true, true));
        Assert.Equal(0, Strategies.BossPlan(AttackStrategy.HitAndRun, WeaponStyle.Melee, 1f, 1f, 3f, 12f, true, 0, false, true));
    }

    [Fact]
    public void KeepDistance_ClampedWithDefault()
    {
        Assert.Equal(12f, Strategies.Keep(null));
        Assert.Equal(3f, Strategies.Keep(1f));
        Assert.Equal(30f, Strategies.Keep(99f));
    }
}

public class AttackResultLoadoutTests
{
    [Fact]
    public void Result_ReportsWeaponAmmoStrategyAndSwitches()
    {
        var s = new AttackStats(0) { Weapon = "Wooden Bow", Ammo = "Flaming Arrow", Strategy = "kite" };
        Assert.Null(s.Result(60).Switches);
        s.AddSwitch("attack: Flaming Arrow ran out, firing Wooden Arrow");
        var r = s.Result(60);
        Assert.Equal("Wooden Bow", r.Weapon);
        Assert.Equal("Flaming Arrow", r.Ammo);
        Assert.Equal("kite", r.Strategy);
        Assert.Equal(new[] { "attack: Flaming Arrow ran out, firing Wooden Arrow" }, r.Switches);
    }
}
