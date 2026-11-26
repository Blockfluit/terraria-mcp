using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Combat;

/// <summary>Hold = stand and fight; StepToward/StepAway = walk straight at / away from the target (no pathfinding);
/// PathToward = pathfind toward it (MoveToTask).</summary>
public enum StanceMove { Hold, StepToward, StepAway, PathToward }

/// <summary>Where to stand relative to the target. dx/dy: target minus player, in tiles (y down).</summary>
public static class CombatStance
{
    public const float RangedMinTiles = 8f, RangedMaxTiles = 15f;

    /// <summary>Within this distance (and roughly level) melee walks straight at the target instead of pathfinding.</summary>
    public const float DirectChaseTiles = 6f, DirectChaseMaxDy = 2f;

    /// <summary>Ranged weapons fire while approaching up to this far beyond the band.</summary>
    public const float RangedFireSlackTiles = 5f;

    public static StanceMove Decide(WeaponStyle style, float dx, float dy, float reachTiles, bool lineOfSight)
    {
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        if (style == WeaponStyle.Melee)
        {
            if (dist <= reachTiles) return StanceMove.Hold;
            if (dist <= DirectChaseTiles && MathF.Abs(dy) <= DirectChaseMaxDy) return StanceMove.StepToward;
            return StanceMove.PathToward;
        }
        if (!lineOfSight || dist > RangedMaxTiles) return StanceMove.PathToward;
        if (dist < RangedMinTiles) return StanceMove.StepAway;
        return StanceMove.Hold;
    }

    /// <summary>Is it worth pressing use this tick? Melee: target within reach + 1 tile (it may step in).
    /// Ranged: clear line of sight and within the band's max + slack.</summary>
    public static bool ShouldFire(WeaponStyle style, float dist, float reachTiles, bool lineOfSight) =>
        style == WeaponStyle.Melee
            ? dist <= reachTiles + 1f
            : lineOfSight && dist <= RangedMaxTiles + RangedFireSlackTiles;
}

/// <summary>Modest target leading for projectiles: aim where the target will be after the projectile's straight-line
/// travel time (capped; gravity and target acceleration ignored).</summary>
public static class AimLead
{
    public const float MaxLeadTicks = 30f;

    /// <summary>All values in pixels and pixels/tick. Returns the point to aim at.</summary>
    public static (float X, float Y) AimPoint(float shooterX, float shooterY, float targetX, float targetY,
        float targetVelX, float targetVelY, float projectileSpeed)
    {
        if (projectileSpeed <= 0f) return (targetX, targetY);
        float dx = targetX - shooterX, dy = targetY - shooterY;
        float t = MathF.Min(MathF.Sqrt(dx * dx + dy * dy) / projectileSpeed, MaxLeadTicks);
        return (targetX + targetVelX * t, targetY + targetVelY * t);
    }
}

/// <summary>
/// May the player take a couple of blind steps sideways (kiting / closing in without a path)? Checks the two columns
/// ahead of the 2-wide body: no lava or hazard in body height, and a standable floor at most 3 tiles down whose first
/// non-air tile isn't liquid, a hazard or unknown. Walls are fine (the walker jumps).
/// </summary>
public static class StepSafety
{
    public const int MaxDrop = 3;

    /// <param name="stand">The standing (feet) tile: body occupies stand.X..X+1, Y-2..Y.</param>
    /// <param name="dir">+1 right, -1 left.</param>
    public static bool IsSafe(ITileGrid g, TilePoint stand, int dir)
    {
        int first = dir > 0 ? stand.X + 2 : stand.X - 1;
        for (int i = 0; i < 2; i++)
            if (!ColumnSafe(g, first + i * Math.Sign(dir), stand.Y)) return false;
        return true;
    }

    private static bool ColumnSafe(ITileGrid g, int x, int y)
    {
        for (int by = y - 2; by <= y; by++)
            if (g.Get(x, by).Kind is TileKind.Lava or TileKind.Hazard) return false;
        for (int fy = y + 1; fy <= y + MaxDrop; fy++)
        {
            var k = g.Get(x, fy).Kind;
            if (k == TileKind.Air) continue;
            return k is TileKind.Solid or TileKind.Platform;
        }
        return false;
    }
}
