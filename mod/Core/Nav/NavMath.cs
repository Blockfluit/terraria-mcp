using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Nav;

public static class NavMath
{
    /// <summary>
    /// The 2x3 body position (feet-left tile) that best contains the real 20 px hitbox: the hitbox is centred in the
    /// body when <c>X = x + 0.375</c>. The row is the feet row (exact when standing). Plan from this, not from
    /// floor(X): a hitbox at x+0.9 mostly covers columns x+1..x+2.
    /// </summary>
    public static TilePoint StandingTile(PlayerKinematics k) =>
        new((int)MathF.Round(k.X - PlayerPhysics.CenterOffset), (int)MathF.Floor(k.Y + 1f - 1f / 16f));

    /// <summary>Kinematics from the game's hitbox: top-left position and velocity in pixels.</summary>
    public static PlayerKinematics FromPixels(float left, float top, float velX, float velY, int heightPx = PlayerPhysics.HitboxHeightPx) =>
        new(left / 16f, (top + heightPx) / 16f - 1f, velX / 16f, velY / 16f, velY == 0f);
}
