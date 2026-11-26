using LlmPlayer.Core.Combat;
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
}
