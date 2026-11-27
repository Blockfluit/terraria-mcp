namespace LlmPlayer.Core.Nav;

/// <summary>
/// How far the player may go with its head under water/honey. Vanilla: breath drops by 1 every
/// <c>breathCDMax</c> ticks (7 without gear; Breathing Reed / Diving Helmet raise it) while the head is in liquid,
/// so the player has <c>breathMax × breathCDMax</c> ticks (200 × 7 = 1400, ~23 s) before drowning; out of liquid
/// breath refills by 3 per tick.
/// </summary>
public static class BreathMath
{
    public const int VanillaBreathMax = 200, VanillaTicksPerBreath = 7;

    /// <summary>Planning estimate for one submerged path step: moving in liquid runs at half speed (~11 ticks per
    /// tile) and without Flippers rising means jumping off the bottom, so a step is often slower.</summary>
    public const int TicksPerSubmergedStep = 12;

    /// <summary>Only plan on a tenth of the air: the player may dive with less than full breath, get knocked back,
    /// retry jumps, and the surfacing reflex needs air left to get out.</summary>
    public const int SafetyFactor = 10;

    public const int MinSteps = 4, MaxSteps = 60;

    /// <summary>11 for vanilla's 200 breath.</summary>
    public const int VanillaMaxSubmergedSteps = VanillaBreathMax * VanillaTicksPerBreath / (TicksPerSubmergedStep * SafetyFactor);

    /// <summary>Most consecutive submerged path steps for this much breath.</summary>
    public static int MaxSubmergedSteps(int breathMax, int ticksPerBreath)
    {
        long air = (long)Math.Max(0, breathMax) * Math.Max(1, ticksPerBreath);
        return (int)Math.Clamp(air / (TicksPerSubmergedStep * SafetyFactor), MinSteps, MaxSteps);
    }
}
