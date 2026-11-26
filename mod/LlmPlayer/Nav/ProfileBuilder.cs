using LlmPlayer.Core.Nav;
using LlmPlayer.Tasks;
using Terraria;

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

    public static MovementProfile From(Player p, bool allowDig = true, bool allowBuild = true)
    {
        var phys = Physics(p);
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
            BuildItemCount = allowBuild ? HeldItemLease.BuildItemCount(p) : 0,
        };
    }
}
