using LlmPlayer.Core.Nav;
using LlmPlayer.Tasks;
using Terraria;
using Terraria.ID;

namespace LlmPlayer.Nav;

/// <summary>
/// The player's real movement abilities for the pathfinder. Jump limits come from <see cref="JumpModel"/>, which
/// simulates vanilla's jump with the player's current <c>Player.jumpSpeed</c> / <c>Player.jumpHeight</c> (statics
/// the local player's UpdateJumpHeight sets each update: balloons, frog legs, wereform, sticky, dazed…),
/// <c>gravity</c> and run constants, assuming a standing start and worst-case obstacles. Never more optimistic
/// than reality:
/// <list type="bullet">
/// <item>Wet, mounted or with reversed gravity, those statics describe a different movement mode (water: 6.01 x30),
/// so the vanilla dry values are used, and never anything better than measured.</item>
/// <item>Extra jumps (bottles), wings and rocket boots are ignored; run boots too (maxRunSpeed only).</item>
/// <item>SafeFallTiles = 25 + extraFall (vanilla's damage threshold) - 5 margin; NoFallDamage = noFallDmg or wings
/// equipped (vanilla skips fall damage for both).</item>
/// <item>Breath: MaxSubmergedSteps from breathMax × breathCDMax (<see cref="BreathMath"/>; Breathing Reed / Diving
/// Gear raise breathCDMax); WaterBreathing = gills or merfolk (no breath loss at all).</item>
/// <item>CanSwim (<see cref="CanSwim"/>): without it the pathfinder treats water like vanilla does for a player
/// without Flippers (sink, walk along the bottom, jump off it).</item>
/// <item>Water walking (<see cref="WaterWalkState"/>): WaterWalk from waterWalk / waterWalk2, LavaWalk only with
/// waterWalk and lava immunity, WaterWalkTicks = the potion buff's time left (unlimited from gear).</item>
/// </list>
/// </summary>
public static class ProfileBuilder
{
    private static PlayerPhysics? _cachedPhysics;
    private static MovementProfile? _cachedJumps;

    public static PlayerPhysics Physics(Player p)
    {
        var vanilla = PlayerPhysics.Vanilla;
        bool special = p.wet || p.mount.Active || p.gravDir != 1f;
        float jumpSpeed = Player.jumpSpeed, gravity = p.gravity;
        int jumpHeight = Player.jumpHeight;
        if (special || gravity <= 0f)
        {
            jumpSpeed = Math.Min(jumpSpeed, vanilla.JumpSpeed);
            jumpHeight = Math.Min(jumpHeight, vanilla.JumpHeight);
            gravity = Math.Max(gravity, vanilla.Gravity);
        }
        return new PlayerPhysics(
            JumpSpeed: jumpSpeed,
            JumpHeight: Math.Max(0, jumpHeight),
            Gravity: gravity,
            MaxFallSpeed: special ? Math.Max(p.maxFallSpeed, vanilla.MaxFallSpeed) : Math.Max(p.maxFallSpeed, 1f),
            MaxRunSpeed: Math.Max(p.maxRunSpeed, 0.5f),
            RunAcceleration: Math.Max(p.runAcceleration, 0.01f),
            RunSlowdown: Math.Max(p.runSlowdown, 0.01f));
    }

    /// <param name="platformReserve">Platforms the plan may not use (<see cref="Core.Build.BuildReserve.Budget"/>);
    /// 0 = everything.</param>
    public static MovementProfile From(Player p, bool allowDig = true, bool allowBuild = true, int platformReserve = 0)
    {
        var phys = Physics(p);
        var ww = WaterWalkState(p);
        if (_cachedJumps == null || _cachedPhysics != phys)
        {
            _cachedJumps = JumpModel.Apply(new MovementProfile(), phys);
            _cachedPhysics = phys;
        }
        return _cachedJumps with
        {
            SafeFallTiles = Math.Max(0, 25 + p.extraFall - 5),
            NoFallDamage = p.noFallDmg || p.equippedWings != null,
            PickPower = allowDig ? HeldItemLease.PickPower(p) : 0,
            BuildItemCount = allowBuild
                ? Core.Build.BuildReserve.Budget(HeldItemLease.PlatformCount(p), HeldItemLease.BlockCount(p), platformReserve, released: platformReserve <= 0)
                : 0,
            BuildPlatformCount = allowBuild
                ? Core.Build.BuildReserve.PlatformBudget(HeldItemLease.PlatformCount(p), platformReserve, released: platformReserve <= 0)
                : 0,
            MaxSubmergedSteps = BreathMath.MaxSubmergedSteps(p.breathMax, p.breathCDMax),
            WaterBreathing = Tasks.Reflexes.CanBreatheInLiquid(p),
            CanSwim = CanSwim(p),
            WaterWalk = ww.Water,
            LavaWalk = ww.Lava,
            WaterWalkTicks = ww.Ticks,
            MaxRunSpeed = phys.MaxRunSpeed,
        };
    }

    /// <summary>
    /// Water walking (wiki "Water Walking Potion", "Water Walking Boots"): vanilla's <c>waterWalk</c> (the potion's
    /// buff, Lava Waders and their upgrades) walks on water, honey and lava, <c>waterWalk2</c> (Water Walking Boots,
    /// Obsidian Water Walking Boots) on water and honey. Lava only counts with <c>lavaImmune</c> (a slip or the buff
    /// running out would be deadly). Ticks: the Water Walking buff's time left when it is the only possible source
    /// (no waterWalk2 from boots); with waterWalk but the buff gone, it comes from gear: unlimited. With both the buff
    /// and waterWalk gear the buff time is used (conservative: the buff can't be told apart from the gear).
    /// </summary>
    public static (bool Water, bool Lava, int Ticks) WaterWalkState(Player p)
    {
        bool water = p.waterWalk || p.waterWalk2;
        if (!water) return (false, false, int.MaxValue);
        bool lava = p.waterWalk && p.lavaImmune;
        int ticks = int.MaxValue;
        if (!p.waterWalk2)
        {
            int idx = p.FindBuffIndex(BuffID.WaterWalking);
            if (idx >= 0) ticks = Math.Max(0, p.buffTime[idx]);
        }
        return (true, lava, ticks);
    }

    /// <summary>
    /// The player can swim up through liquid (wiki "Swimming"): Flippers and their upgrades, Diving Gear / Arctic
    /// Diving Gear and the Flipper Potion all set <c>accFlipper</c>; Neptune's Shell's merfolk form; the Inner Tube;
    /// the Hardy Saddle's turtle mount. Wings and rocket boots work under water too. Everything else only sinks
    /// and jumps off the bottom.
    /// </summary>
    public static bool CanSwim(Player p) =>
        p.accFlipper || p.HasBuff(BuffID.Flipper) || p.merman || p.hasFloatingTube
        || (p.mount.Active && p.mount.Type == MountID.Turtle)
        || p.equippedWings != null || p.wingTimeMax > 0 || p.rocketBoots > 0;
}
