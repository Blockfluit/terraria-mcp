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
/// <param name="CanSwim">The player can swim up through water/honey (Flippers, Diving Gear, Flipper Potion, Neptune's
/// Shell form, a swimming mount, or wings / rocket boots, which also work under water). Without it vanilla lets the
/// player only sink and jump off the bottom (wiki "Swimming"): the pathfinder then walks along the bottom, rises only
/// with a water jump from it (<paramref name="MaxWaterJumpUp"/>) and sinks everywhere else.</param>
/// <param name="MaxWaterJumpUp">How many tiles the feet rise in one jump off the bottom of liquid
/// (<see cref="JumpModel.WaterJumpUp"/>; vanilla 8).</param>
/// <param name="WaterWalk">Water Walking (Water Walking Potion buff, Water Walking Boots and their upgrades: vanilla
/// <c>waterWalk</c> / <c>waterWalk2</c>): the top cell of water or honey holds the player like a platform (vanilla
/// Collision.WaterCollision, only while Down isn't held), so lakes can be walked over (<see cref="LiquidWalk"/>).</param>
/// <param name="LavaWalk">The lava surface holds the player too. Vanilla's <c>waterWalk</c> (the potion, Lava Waders)
/// also walks on lava, but the mod only sets this with lava immunity: a slip is deadly otherwise.</param>
/// <param name="WaterWalkTicks">Ticks of <paramref name="WaterWalk"/> left (the potion's remaining buff time;
/// int.MaxValue = from gear, unlimited). The planner never stands the player on a liquid surface later than the
/// estimated arrival time allows (<see cref="LiquidWalk.UsableTicks"/>, at <paramref name="MaxRunSpeed"/>).</param>
/// <param name="MaxRunSpeed">Running speed in px/tick (vanilla 3), for the travel-time estimate.</param>
/// <param name="BuildPlatformCount">How many of the <paramref name="BuildItemCount"/> build items are platforms; the
/// rest are blocks (dirt, stone …). Walks place platforms first (BuildReserve.Pick), so build number k along a path is
/// a platform when k ≤ this. A block is solid: it can't be dropped through, and vanilla won't place it where it would
/// overlap the player's hitbox. Default: every build item is a platform.</param>
public sealed record MovementProfile(
    int MaxJumpUp = 5,
    int MaxJumpGap = 6,
    int SafeFallTiles = 20,
    bool NoFallDamage = false,
    int PickPower = 0,
    int BuildItemCount = 0,
    IReadOnlyList<int>? JumpReach = null,
    int MaxSubmergedSteps = BreathMath.VanillaMaxSubmergedSteps,
    bool WaterBreathing = false,
    bool CanSwim = false,
    int MaxWaterJumpUp = JumpModel.VanillaWaterJumpUp,
    bool WaterWalk = false,
    bool LavaWalk = false,
    int WaterWalkTicks = int.MaxValue,
    float MaxRunSpeed = 3f,
    int BuildPlatformCount = int.MaxValue)
{
    /// <summary>Build number <paramref name="k"/> (1-based along a path) places a platform, not a block.</summary>
    public bool BuildIsPlatform(int k) => k <= Math.Min(BuildPlatformCount, BuildItemCount);

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
