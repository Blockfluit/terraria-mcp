using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Inventory;

/// <summary>Tile reach for using/placing items, in pixel units like the game.</summary>
public static class ReachMath
{
    /// <summary>
    /// Vanilla <c>Player.IsTargetTileInItemRange</c> (tModLoader 1.4.4): the hitbox expanded by
    /// rangeX/rangeY (<c>Player.tileRangeX/Y</c>, 5/4 plus range accessories) + the item's <c>tileBoost</c>.
    /// </summary>
    public static bool InItemRange(float posX, float posY, int width, int height, int rangeX, int rangeY, int tileBoost,
        int tx, int ty) =>
        posX / 16f - rangeX - tileBoost <= tx
        && (posX + width) / 16f + rangeX + tileBoost - 1f >= tx
        && posY / 16f - rangeY - tileBoost <= ty
        && (posY + height) / 16f + rangeY + tileBoost - 2f >= ty;

    /// <summary>Does tile (tx,ty) intersect the player's hitbox? (Solid tiles can't be placed there.)</summary>
    public static bool OverlapsBody(float posX, float posY, int width, int height, int tx, int ty)
    {
        float l = tx * 16f, t = ty * 16f;
        return l < posX + width && l + 16f > posX && t < posY + height && t + 16f > posY;
    }

    /// <summary>
    /// Where to stand so the body (2 tiles wide) clears <paramref name="target"/> but it stays in reach: 2 tiles of
    /// gap on the side the player is already on. Safe with a move tolerance of 1.
    /// </summary>
    public static TilePoint StepAsideGoal(TilePoint standing, TilePoint target) =>
        standing.X >= target.X ? new TilePoint(target.X + 2, standing.Y) : new TilePoint(target.X - 3, standing.Y);
}
