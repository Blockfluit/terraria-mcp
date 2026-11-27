namespace LlmPlayer.Core.Nav;

/// <summary>Player movement capabilities. Defaults are conservative vanilla values;
/// the mod builds the real profile from player stats (ProfileBuilder → <see cref="JumpModel.Apply"/>).</summary>
/// <param name="MaxJumpGap">Largest horizontal jump distance (feet-left tiles) the pathfinder considers at all.</param>
/// <param name="JumpReach">Optional physical jump envelope from <see cref="JumpModel"/>: the largest |dx| for a jump
/// landing dy rows lower (negative = higher), given the ceiling (how many tiles the body can rise, 1..MaxJumpUp+1,
/// the last also covering anything higher) and the launch level (how high the feet must get before nothing blocks
/// sideways motion, 0..MaxJumpUp+1). Indexed by <see cref="ReachIndex"/>. Null → MaxJumpGap everywhere (box model).</param>
/// <param name="MaxSubmergedSteps">Most consecutive path steps with the head in water/honey (<see cref="BreathMath"/>;
/// default: vanilla's 200 breath). Ignored with <paramref name="WaterBreathing"/>.</param>
/// <param name="WaterBreathing">The player can't drown (gills, merfolk): submerged steps are neither capped nor
/// penalised.</param>
public sealed record MovementProfile(
    int MaxJumpUp = 5,
    int MaxJumpGap = 6,
    int SafeFallTiles = 20,
    bool NoFallDamage = false,
    int PickPower = 0,
    int BuildItemCount = 0,
    IReadOnlyList<int>? JumpReach = null,
    int MaxSubmergedSteps = BreathMath.VanillaMaxSubmergedSteps,
    bool WaterBreathing = false)
{
    /// <summary>Largest |dx| for a jump landing dy rows lower (negative dy = higher), with the body able to rise at
    /// most <paramref name="ceiling"/> tiles and sideways motion blocked until the feet are <paramref name="launch"/>
    /// tiles up.</summary>
    public int JumpReachAt(int dy, int ceiling = int.MaxValue, int launch = 0)
    {
        if (JumpReach == null) return MaxJumpGap;
        int levels = MaxJumpUp + 1;
        if (dy < -MaxJumpUp || dy > MaxJumpUp || launch > levels || JumpReach.Count < levels * (levels + 1) * (2 * MaxJumpUp + 1))
            return 0;
        return JumpReach[ReachIndex(MaxJumpUp, Math.Clamp(ceiling, 1, levels), Math.Max(0, launch), dy)];
    }

    public static int ReachIndex(int maxJumpUp, int ceiling, int launch, int dy) =>
        ((ceiling - 1) * (maxJumpUp + 2) + launch) * (2 * maxJumpUp + 1) + dy + maxJumpUp;
}
