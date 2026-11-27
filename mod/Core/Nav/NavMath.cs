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

    /// <summary>
    /// <see cref="StandingTile(PlayerKinematics)"/> that knows about floor slopes and half blocks: a hitbox riding one
    /// has its feet up to (almost) a tile below the block's top, inside the block's own row, so the plain feet row is
    /// that row and the 2x3 body would overlap the block. Then the body really stands on it, one row up (a clear,
    /// supported body there; the neighbouring column on the hitbox's side if the rounded one has no support). Seen in game: on a sloped hillside the plain tile overlapped the slope, the planner exempted
    /// the start cells and planned a Walk the follower rejected at once ("path changed at Walk"), over and over.
    /// A hitbox standing on a liquid surface (water walking) with its feet inside the top liquid cell counts the same way.
    /// </summary>
    public static TilePoint StandingTile(PlayerKinematics k, ITileGrid grid)
    {
        var t = StandingTile(k);
        float bottom = k.Y + 1f;
        if (bottom >= t.Y + 1f - 1f / 32f) return t; // resting on top of the row below: nothing sunk in
        int c0 = (int)MathF.Floor(k.X + 0.01f), c1 = (int)MathF.Floor(k.X + PlayerPhysics.HitboxWidth - 0.01f);
        bool sunk = false;
        // Also standing on a liquid surface (water walking): the grid calls a cell liquid from half full, so the
        // feet rest up to half a tile inside the top liquid cell's row. On the ground mid-row in a liquid cell can't
        // be anything else (a body in liquid on ground rests on the row's bottom, or on a half block / slope).
        for (int c = c0; c <= c1 && !sunk; c++)
        {
            var cell = grid.Get(c, t.Y);
            sunk = cell.IsLowTop || (k.OnGround && cell.Kind is TileKind.Water or TileKind.Honey or TileKind.Lava
                                     && grid.Get(c, t.Y - 1).Kind is TileKind.Air or TileKind.Platform);
        }
        if (!sunk) return t;
        // One row up; the hitbox may rest on a slope in a column the rounded body doesn't cover (it spans 3 columns
        // part of the time), so also try the neighbouring column on the hitbox's side.
        int side = k.X >= t.X + PlayerPhysics.CenterOffset ? 1 : -1;
        TilePoint? clear = null;
        foreach (int dx in new[] { 0, side, -side })
        {
            var c = new TilePoint(t.X + dx, t.Y - 1);
            if (!BodyClear(grid, c)) continue;
            if (grid.Get(c.X, c.Y + 1).IsStandable || grid.Get(c.X + 1, c.Y + 1).IsStandable) return c;
            clear ??= c;
        }
        return clear ?? t;
    }

    /// <summary>No tile of the 2x3 body at p blocks it.</summary>
    public static bool BodyClear(ITileGrid grid, TilePoint p)
    {
        for (int dy = 0; dy < 3; dy++)
            for (int dx = 0; dx < 2; dx++)
                if (grid.Get(p.X + dx, p.Y - dy).BlocksBody) return false;
        return true;
    }

    /// <summary>Kinematics from the game's hitbox: top-left position and velocity in pixels.</summary>
    public static PlayerKinematics FromPixels(float left, float top, float velX, float velY, int heightPx = PlayerPhysics.HitboxHeightPx) =>
        new(left / 16f, (top + heightPx) / 16f - 1f, velX / 16f, velY / 16f, velY == 0f);
}
