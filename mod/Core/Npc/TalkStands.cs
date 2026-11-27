using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;

namespace LlmPlayer.Core.Npc;

/// <summary>
/// Where the player can stand to talk to an NPC: every known standing position (feet-left tile, 2×3 body clear, ground
/// under a foot) from which the NPC is in vanilla's talk range (<see cref="NpcMath.InTalkRange"/>) wherever in that tile
/// the body ends up. Vanilla checks only the distance, not walls in between, so a stand outside an NPC's house works
/// as well as one inside. talk_to_npc / buy plan to the nearest reachable one (<see cref="PathOptions.GoalSet"/>)
/// instead of to the NPC itself, whose house may be closed to the pathfinder.
/// </summary>
public static class TalkStands
{
    public const int PlayerWidthPx = PlayerPhysics.HitboxWidthPx, PlayerHeightPx = PlayerPhysics.HitboxHeightPx;

    /// <summary>Stands around an NPC whose hitbox is (<paramref name="nX"/>, <paramref name="nY"/>,
    /// <paramref name="nWidth"/>, <paramref name="nHeight"/>) in pixels. Only known tiles count (fair vision: the body
    /// cells and the ground must not be Unknown).</summary>
    public static HashSet<TilePoint> Find(ITileGrid grid, int nX, int nY, int nWidth, int nHeight, int rangeX, int rangeY)
    {
        var set = new HashSet<TilePoint>();
        int cx = (nX + nWidth / 2) / 16, cy = (nY + nHeight) / 16;
        int rx = rangeX + 3 + nWidth / 32, ry = rangeY + 4 + nHeight / 32;
        for (int y = cy - ry; y <= cy + ry; y++)
            for (int x = cx - rx; x <= cx + rx; x++)
            {
                var t = new TilePoint(x, y);
                if (InRangeFrom(t, rangeX, rangeY, nX, nY, nWidth, nHeight) && Standable(grid, t)) set.Add(t);
            }
        return set;
    }

    /// <summary>
    /// Is the NPC in talk range from anywhere the body can be while standing on tile <paramref name="t"/>? The feet
    /// rest on top of row t.Y + 1; horizontally the standing tile is the rounded body position
    /// (<see cref="NavMath.StandingTile(PlayerKinematics)"/>), so the hitbox's left edge lies in
    /// [16·x − 2, 16·x + 14) px. The range test is monotone in x, so both ends suffice.
    /// </summary>
    public static bool InRangeFrom(TilePoint t, int rangeX, int rangeY, int nX, int nY, int nWidth, int nHeight)
    {
        float top = (t.Y + 1) * 16f - PlayerHeightPx;
        float shift = PlayerPhysics.CenterOffset * 16f; // 6 px: centred body
        float lo = t.X * 16f + shift - 8f, hi = t.X * 16f + shift + 8f - 0.01f;
        return NpcMath.InTalkRange(lo, top, PlayerWidthPx, PlayerHeightPx, rangeX, rangeY, nX, nY, nWidth, nHeight)
               && NpcMath.InTalkRange(hi, top, PlayerWidthPx, PlayerHeightPx, rangeX, rangeY, nX, nY, nWidth, nHeight);
    }

    private static bool Standable(ITileGrid g, TilePoint t)
    {
        for (int dy = 0; dy < 3; dy++)
            for (int dx = 0; dx < 2; dx++)
                if (g.Get(t.X + dx, t.Y - dy).Kind == TileKind.Unknown) return false;
        return Pathfinder.CanStand(g, t);
    }
}
