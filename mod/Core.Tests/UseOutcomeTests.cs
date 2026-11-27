using LlmPlayer.Core.Inventory;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class UseOutcomeTests
{
    private static UseObservation Potion(int before, int after, bool buff, bool started = true) =>
        new("Ironskin Potion", Consumable: true, before, after, BuffType: 5, BuffAfter: buff, BuffSecondsLeft: buff ? 480 : 0,
            BuffName: "Ironskin", LifeBefore: 100, LifeAfter: 100, Started: started);

    [Fact]
    public void ConsumedBuffPotion_IsSuccessWithBuffTime()
    {
        var r = UseOutcome.Judge(Potion(3, 2, buff: true));
        Assert.True(r.Ok);
        Assert.Equal("used Ironskin Potion (buff Ironskin 8:00, 2 left)", r.Detail);
    }

    [Fact]
    public void AnimationWithoutConsumption_IsAFailure()
    {
        // An earlier swing still running looked like "the use began": not proof for a consumable.
        var r = UseOutcome.Judge(Potion(3, 3, buff: false, started: true));
        Assert.False(r.Ok);
        Assert.Contains("didn't use Ironskin Potion", r.Detail);
    }

    [Fact]
    public void NonConsumableBuffItem_NeedsItsBuff()
    {
        var o = new UseObservation("Pet Item", false, 1, 1, 40, BuffAfter: false, 0, "Pet", 100, 100, Started: true);
        Assert.False(UseOutcome.Happened(o));
        Assert.True(UseOutcome.Happened(o with { BuffAfter = true }));
    }

    [Fact]
    public void PlainItem_NeedsTheAnimation()
    {
        var o = new UseObservation("Magic Mirror", false, 1, 1, 0, false, 0, null, 100, 100, Started: false);
        Assert.False(UseOutcome.Happened(o));
        Assert.True(UseOutcome.Judge(o with { Started = true }).Ok);
    }

    [Fact]
    public void HealingShowsLife_AndSummaryMarksFailures()
    {
        var heal = new UseObservation("Lesser Healing Potion", true, 5, 4, 0, false, 0, null, 40, 90, true);
        var ok = UseOutcome.Judge(heal);
        Assert.Equal("used Lesser Healing Potion (life 40 -> 90, 4 left)", ok.Detail);
        var bad = UseOutcome.Judge(Potion(2, 2, false));
        Assert.Equal($"{ok.Detail}; FAILED {bad.Detail}", UseOutcome.Summary(new[] { ok, bad }));
    }
}
