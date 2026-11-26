namespace LlmPlayer.Core.Nav;

/// <summary>
/// The player's movement constants, in pixels and ticks (16 px per tile, 60 ticks per second).
/// Defaults are vanilla tModLoader 1.4.4 (Player fields reset each update: jumpSpeed 5.01, jumpHeight 15,
/// gravity 0.4, maxFallSpeed 10, maxRunSpeed 3, runAcceleration 0.08, runSlowdown 0.2; hitbox 20x42).
/// </summary>
public sealed record PlayerPhysics(
    float JumpSpeed = 5.01f,
    int JumpHeight = 15,
    float Gravity = 0.4f,
    float MaxFallSpeed = 10f,
    float MaxRunSpeed = 3f,
    float RunAcceleration = 0.08f,
    float RunSlowdown = 0.2f)
{
    public const int HitboxWidthPx = 20, HitboxHeightPx = 42, TilePx = 16;

    /// <summary>Hitbox width in tiles (1.25).</summary>
    public const float HitboxWidth = HitboxWidthPx / (float)TilePx;

    /// <summary>Hitbox left edge relative to the feet-left tile when centred in the 2-wide body (0.375).</summary>
    public const float CenterOffset = (2f - HitboxWidth) / 2f;

    public static readonly PlayerPhysics Vanilla = new();
}
