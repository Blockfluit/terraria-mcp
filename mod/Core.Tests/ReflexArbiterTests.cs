using LlmPlayer.Core.Combat;
using LlmPlayer.Core.Tasks;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class ReflexArbiterTests
{
    private static ReflexInputs Calm(TaskPhase phase = TaskPhase.Idle) =>
        new(phase, AutoDefend: true, ThreatInReach: false, Life: 100, LifeMax: 100, HealThreshold: 0.35f,
            PotionSick: false, HasHealingPotion: true);

    [Fact]
    public void Calm_DoesNothing()
    {
        var a = new ReflexArbiter();
        var d = a.Tick(Calm());
        Assert.Equal(default, d);
        Assert.False(a.Defending);
    }

    [Theory]
    [InlineData(TaskPhase.Idle)]
    [InlineData(TaskPhase.Defendable)]
    public void Threat_WhileIdleOrMoving_Defends(TaskPhase phase)
    {
        var a = new ReflexArbiter();
        var d = a.Tick(Calm(phase) with { ThreatInReach = true });
        Assert.True(d.Defend);
        Assert.True(d.DefendStarted);
        Assert.True(a.Defending);
        var d2 = a.Tick(Calm(phase) with { ThreatInReach = true });
        Assert.True(d2.Defend);
        Assert.False(d2.DefendStarted); // started fires once
    }

    [Fact]
    public void Threat_DuringBusyTask_NoDefend()
    {
        var a = new ReflexArbiter();
        Assert.False(a.Tick(Calm(TaskPhase.Busy) with { ThreatInReach = true }).Defend);
    }

    [Fact]
    public void Threat_AutoDefendOff_NoDefend()
    {
        var a = new ReflexArbiter();
        Assert.False(a.Tick(Calm() with { ThreatInReach = true, AutoDefend = false }).Defend);
    }

    [Fact]
    public void ThreatGone_KeepsDefendingForGrace_ThenReleasesOnce()
    {
        var a = new ReflexArbiter();
        a.Tick(Calm(TaskPhase.Defendable) with { ThreatInReach = true });
        for (int i = 1; i < ReflexArbiter.ReleaseGraceTicks; i++)
            Assert.True(a.Tick(Calm(TaskPhase.Defendable)).Defend);
        var end = a.Tick(Calm(TaskPhase.Defendable));
        Assert.False(end.Defend);
        Assert.True(end.DefendEnded);
        Assert.False(a.Tick(Calm(TaskPhase.Defendable)).DefendEnded);
    }

    [Fact]
    public void ThreatReturnsDuringGrace_ResetsGrace()
    {
        var a = new ReflexArbiter();
        a.Tick(Calm() with { ThreatInReach = true });
        for (int i = 0; i < ReflexArbiter.ReleaseGraceTicks - 2; i++) a.Tick(Calm());
        a.Tick(Calm() with { ThreatInReach = true });
        for (int i = 1; i < ReflexArbiter.ReleaseGraceTicks; i++) Assert.True(a.Tick(Calm()).Defend);
    }

    [Fact]
    public void BusyTaskStarts_EndsDefenseImmediately()
    {
        var a = new ReflexArbiter();
        a.Tick(Calm() with { ThreatInReach = true });
        var d = a.Tick(Calm(TaskPhase.Busy) with { ThreatInReach = true });
        Assert.False(d.Defend);
        Assert.True(d.DefendEnded);
    }

    [Fact]
    public void LowLife_QuickHeals_ThenRateLimited()
    {
        var a = new ReflexArbiter();
        var low = Calm() with { Life = 30 };
        Assert.True(a.Tick(low).QuickHeal);
        for (int i = 1; i < ReflexArbiter.HealRetryTicks; i++) Assert.False(a.Tick(low).QuickHeal);
        Assert.True(a.Tick(low).QuickHeal);
    }

    [Fact]
    public void QuickHeal_NotAtOrAboveThreshold_NotWhenSick_NotWithoutPotion()
    {
        Assert.False(new ReflexArbiter().Tick(Calm() with { Life = 35 }).QuickHeal);
        Assert.False(new ReflexArbiter().Tick(Calm() with { Life = 10, PotionSick = true }).QuickHeal);
        Assert.False(new ReflexArbiter().Tick(Calm() with { Life = 10, HasHealingPotion = false }).QuickHeal);
        Assert.False(new ReflexArbiter().Tick(Calm() with { Life = 0 }).QuickHeal);
        Assert.False(new ReflexArbiter().Tick(Calm() with { Life = 10, HealThreshold = 0f }).QuickHeal);
    }

    [Fact]
    public void QuickHeal_FiresDuringBusyTaskAndAlongsideDefense()
    {
        var a = new ReflexArbiter();
        Assert.True(a.Tick(Calm(TaskPhase.Busy) with { Life = 10 }).QuickHeal);
        var b = new ReflexArbiter();
        var d = b.Tick(Calm() with { Life = 10, ThreatInReach = true });
        Assert.True(d.QuickHeal);
        Assert.True(d.Defend);
    }

    [Fact]
    public void Reset_ClearsDefenseWithoutEndEvent()
    {
        var a = new ReflexArbiter();
        a.Tick(Calm() with { ThreatInReach = true });
        a.Reset();
        Assert.False(a.Defending);
        Assert.False(a.Tick(Calm()).DefendEnded);
    }
    // ---- release waits for the defender's swing -----------------------------------------------------------------

    [Fact]
    public void Release_WaitsForItemUseToEnd()
    {
        var a = new ReflexArbiter();
        a.Tick(Calm(TaskPhase.Defendable) with { ThreatInReach = true });
        for (int i = 1; i < ReflexArbiter.ReleaseGraceTicks; i++) a.Tick(Calm(TaskPhase.Defendable) with { UsingItem = true });
        // Grace is over but the swing isn't: keep the controls.
        for (int i = 0; i < 10; i++)
        {
            var d = a.Tick(Calm(TaskPhase.Defendable) with { UsingItem = true });
            Assert.True(d.Defend);
            Assert.False(d.DefendEnded);
        }
        var end = a.Tick(Calm(TaskPhase.Defendable));
        Assert.False(end.Defend);
        Assert.True(end.DefendEnded);
    }

    [Fact]
    public void Release_ItemUseNeverEnding_GivesUpAfterMaxWait()
    {
        var a = new ReflexArbiter();
        a.Tick(Calm(TaskPhase.Defendable) with { ThreatInReach = true });
        var busy = Calm(TaskPhase.Defendable) with { UsingItem = true };
        int ticks = 0;
        ReflexDecision d;
        do { d = a.Tick(busy); ticks++; } while (d.Defend && ticks < 1000);
        Assert.True(d.DefendEnded);
        Assert.Equal(ReflexArbiter.ReleaseGraceTicks + ReflexArbiter.MaxReleaseWaitTicks, ticks);
    }

    [Fact]
    public void Release_ThreatReturnsWhileWaiting_ResetsWait()
    {
        var a = new ReflexArbiter();
        a.Tick(Calm() with { ThreatInReach = true });
        var busy = Calm() with { UsingItem = true };
        for (int i = 0; i < ReflexArbiter.ReleaseGraceTicks + ReflexArbiter.MaxReleaseWaitTicks - 1; i++) Assert.True(a.Tick(busy).Defend);
        a.Tick(Calm() with { ThreatInReach = true });
        for (int i = 0; i < ReflexArbiter.ReleaseGraceTicks + ReflexArbiter.MaxReleaseWaitTicks - 1; i++) Assert.True(a.Tick(busy).Defend);
        Assert.True(a.Tick(busy).DefendEnded);
    }

    [Fact]
    public void UsingItem_DoesNotDelayStart()
    {
        // The task's tool is mid-swing when the threat arrives: the defence starts at once (the task stops being
        // ticked, "use" is released); the reflex's HeldItemLease switches weapons once the swing ends.
        var d = new ReflexArbiter().Tick(Calm(TaskPhase.Defendable) with { ThreatInReach = true, UsingItem = true });
        Assert.True(d.Defend);
        Assert.True(d.DefendStarted);
    }

    // ---- the chop_tree playtest bug -----------------------------------------------------------------------------

    [Fact]
    public void ChopTree_ZombieInReach_PausesTaskAndResumesAfterGrace()
    {
        var a = new ReflexArbiter();
        var chopping = Calm(TaskPhases.Of("chop_tree"));
        Assert.False(a.Tick(chopping).Defend); // task ticks
        var hit = a.Tick(chopping with { ThreatInReach = true });
        Assert.True(hit.Defend);                // task paused
        Assert.True(hit.DefendStarted);
        for (int i = 1; i < ReflexArbiter.ReleaseGraceTicks; i++) Assert.True(a.Tick(chopping).Defend);
        var resume = a.Tick(chopping);
        Assert.True(resume.DefendEnded);
        Assert.False(resume.Defend);            // task ticks again this tick
    }

    // ---- surfacing -------------------------------------------------------------------------------------------

    private static ReflexInputs Underwater(TaskPhase phase, int breath) =>
        Calm(phase) with { HeadInLiquid = true, Breath = breath, BreathMax = 200 };

    [Theory]
    [InlineData(TaskPhase.Idle)]
    [InlineData(TaskPhase.Defendable)]
    [InlineData(TaskPhase.Busy)]
    public void LowBreath_Surfaces_EvenDuringBusyTasks(TaskPhase phase)
    {
        var a = new ReflexArbiter();
        Assert.False(a.Tick(Underwater(phase, 100)).Surface); // 50 %: still fine
        var d = a.Tick(Underwater(phase, 80));
        Assert.True(d.Surface);
        Assert.True(d.SurfaceStarted);
        Assert.True(d.OwnsControls);
        var d2 = a.Tick(Underwater(phase, 79));
        Assert.True(d2.Surface);
        Assert.False(d2.SurfaceStarted);
    }

    [Fact]
    public void Surfacing_KeepsControlUntilBreathIsBack_ThenEndsOnce()
    {
        var a = new ReflexArbiter();
        a.Tick(Underwater(TaskPhase.Busy, 60));
        // head out: breath refills, still surfacing below 90 %
        Assert.True(a.Tick(Calm(TaskPhase.Busy) with { Breath = 120, BreathMax = 200 }).Surface);
        Assert.True(a.Tick(Calm(TaskPhase.Busy) with { Breath = 179, BreathMax = 200 }).Surface);
        var end = a.Tick(Calm(TaskPhase.Busy) with { Breath = 180, BreathMax = 200 });
        Assert.False(end.Surface);
        Assert.True(end.SurfaceEnded);
        Assert.False(a.Tick(Calm(TaskPhase.Busy) with { Breath = 200, BreathMax = 200 }).SurfaceEnded);
    }

    [Fact]
    public void WaterBreathing_NeverSurfaces()
    {
        var a = new ReflexArbiter();
        Assert.False(a.Tick(Underwater(TaskPhase.Idle, 10) with { WaterBreathing = true }).Surface);
        Assert.False(a.Tick(Underwater(TaskPhase.Idle, 10) with { BreathMax = 0 }).Surface);
    }

    [Fact]
    public void Surfacing_EndsADefence_AndBlocksIt()
    {
        var a = new ReflexArbiter();
        Assert.True(a.Tick(Calm(TaskPhase.Defendable) with { ThreatInReach = true }).Defend);
        var d = a.Tick(Underwater(TaskPhase.Defendable, 50) with { ThreatInReach = true });
        Assert.True(d.Surface);
        Assert.False(d.Defend);
        Assert.True(d.DefendEnded);
        Assert.False(a.Tick(Underwater(TaskPhase.Defendable, 50) with { ThreatInReach = true }).Defend);
    }

    [Fact]
    public void Surfacing_StillQuickHeals()
    {
        var a = new ReflexArbiter();
        var d = a.Tick(Underwater(TaskPhase.Busy, 50) with { Life = 20 });
        Assert.True(d.Surface);
        Assert.True(d.QuickHeal);
    }
}

public class TaskPhasesTests
{
    [Theory]
    [InlineData(null, TaskPhase.Idle)]
    [InlineData("wait", TaskPhase.Idle)]
    [InlineData("move_to", TaskPhase.Defendable)]
    [InlineData("explore", TaskPhase.Defendable)]
    [InlineData("talk_to_npc", TaskPhase.Defendable)]
    [InlineData("open_chest", TaskPhase.Defendable)]
    [InlineData("use_bed", TaskPhase.Defendable)]
    [InlineData("mine_vein", TaskPhase.Defendable)]
    [InlineData("build_platforms", TaskPhase.Defendable)]
    [InlineData("build_house", TaskPhase.Defendable)]
    [InlineData("buy", TaskPhase.Defendable)]
    [InlineData("chop_tree", TaskPhase.Defendable)]
    [InlineData("mine", TaskPhase.Defendable)]
    [InlineData("mine_area", TaskPhase.Defendable)]
    [InlineData("pick_up_items", TaskPhase.Defendable)]
    [InlineData("place", TaskPhase.Defendable)]
    [InlineData("use_item", TaskPhase.Defendable)]
    [InlineData("attack", TaskPhase.Busy)]
    [InlineData("some_future_task", TaskPhase.Busy)]
    [InlineData("Chop_Tree", TaskPhase.Busy)]
    public void ByName(string? name, TaskPhase expected) => Assert.Equal(expected, TaskPhases.Of(name));

    private sealed class FakeTask(string name) : IGameTask<object>
    {
        public string Name => name;
        public GameTaskStatus Tick(object ctx) => GameTaskStatus.Running;
        public string? Reason => null;
        public string? Progress => null;
        public void OnCancel(object ctx) { }
    }

    private sealed class FakeUseTask : IGameTask<object>, IUninterruptibleTask
    {
        public bool Pressed;
        public string Name => "use_item";
        public bool Uninterruptible => Pressed;
        public GameTaskStatus Tick(object ctx) => GameTaskStatus.Running;
        public string? Reason => null;
        public string? Progress => null;
        public void OnCancel(object ctx) { }
    }

    [Fact]
    public void ByTask_UsesName_NullIsIdle()
    {
        Assert.Equal(TaskPhase.Idle, TaskPhases.Of<object>(null));
        Assert.Equal(TaskPhase.Defendable, TaskPhases.Of(new FakeTask("mine")));
        Assert.Equal(TaskPhase.Busy, TaskPhases.Of(new FakeTask("attack")));
    }

    [Fact]
    public void ByTask_UninterruptibleIsBusy()
    {
        var t = new FakeUseTask();
        Assert.Equal(TaskPhase.Defendable, TaskPhases.Of(t));
        t.Pressed = true;
        Assert.Equal(TaskPhase.Busy, TaskPhases.Of(t));
    }

    [Fact]
    public void UseItemPressedMidDefence_EndsDefenceImmediately()
    {
        var a = new ReflexArbiter();
        var t = new FakeUseTask();
        var inputs = new ReflexInputs(TaskPhases.Of(t), true, true, 100, 100, 0.35f, false, true);
        Assert.True(a.Tick(inputs).Defend);
        t.Pressed = true;
        var d = a.Tick(inputs with { Task = TaskPhases.Of(t) });
        Assert.False(d.Defend);
        Assert.True(d.DefendEnded);
    }
}

public class ThreatRulesTests
{
    private const int Never = int.MaxValue;

    [Fact]
    public void Melee_WithinReach_IsThreat()
    {
        Assert.True(ThreatRules.IsThreat(WeaponStyle.Melee, 2.5f, 2.5f, Never, lineOfSight: false));
        Assert.False(ThreatRules.IsThreat(WeaponStyle.Melee, 2.5f, 2.6f, Never, lineOfSight: true));
    }

    [Fact]
    public void Melee_RecentContact_ExtendsReachByOneTile()
    {
        Assert.True(ThreatRules.IsThreat(WeaponStyle.Melee, 2.5f, 3.4f, 10, lineOfSight: true));
        Assert.True(ThreatRules.IsThreat(WeaponStyle.Melee, 2.5f, 3.5f, ThreatRules.ContactMemoryTicks, lineOfSight: true));
        Assert.False(ThreatRules.IsThreat(WeaponStyle.Melee, 2.5f, 3.6f, 10, lineOfSight: true));
    }

    [Fact]
    public void Melee_OldContact_NoExtension()
    {
        Assert.False(ThreatRules.IsThreat(WeaponStyle.Melee, 2.5f, 3f, ThreatRules.ContactMemoryTicks + 1, lineOfSight: true));
        Assert.False(ThreatRules.IsThreat(WeaponStyle.Melee, 2.5f, 3f, -1, lineOfSight: true));
    }

    [Fact]
    public void Ranged_NeedsLineOfSight_UnlessItJustTouchedUs()
    {
        Assert.False(ThreatRules.IsThreat(WeaponStyle.Ranged, 15f, 5f, Never, lineOfSight: false));
        Assert.True(ThreatRules.IsThreat(WeaponStyle.Ranged, 15f, 5f, Never, lineOfSight: true));
        Assert.True(ThreatRules.IsThreat(WeaponStyle.Ranged, 15f, 1f, 5, lineOfSight: false));
        Assert.False(ThreatRules.IsThreat(WeaponStyle.Ranged, 15f, CombatStance.RangedMaxTiles + 0.1f, 5, lineOfSight: true));
    }
}
