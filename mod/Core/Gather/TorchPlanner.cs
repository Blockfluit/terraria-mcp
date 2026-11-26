using System.Text.Json.Serialization;
using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Gather;

/// <summary>What the game says about a cell for torch placement: empty (no tile, no liquid) and whether a background
/// wall is behind it.</summary>
public readonly record struct TorchCell(bool Free, bool Wall);

/// <summary>
/// When and where explore places a torch, like a player lighting their way underground.
/// <list type="bullet">
/// <item>When: the player's body is dark (<see cref="DarkBrightness"/>, vanilla <c>Lighting.Brightness</c> 0..1) and
/// no torch is within <see cref="SpacingTiles"/>.</item>
/// <item>Where: vanilla torch rules: an empty, dry cell with a background wall behind it or a solid block left,
/// right or below it (not a block about to be dug: the torch would drop). Within the torch's reach from where the
/// player stands, so no walking is needed. Closest to the focus (the dark tile to light, else the player's head);
/// cells the path needs clear (a shaft: torches block platforms) cost extra.</item>
/// </list>
/// </summary>
public static class TorchPlanner
{
    public const int SpacingTiles = 10;
    public const float DarkBrightness = 0.2f;
    private const int KeepClearPenalty = 20, SearchRadius = 6;

    public static bool ShouldPlace(float bodyBrightness, int? nearestTorchDistance) =>
        bodyBrightness < DarkBrightness && (nearestTorchDistance == null || nearestTorchDistance > SpacingTiles);

    public static TilePoint? BestSpot(ITileGrid grid, Func<TilePoint, TorchCell> cell, TilePoint stand, ReachSpec reach,
        TilePoint? focus = null, IReadOnlySet<TilePoint>? pending = null, IReadOnlySet<TilePoint>? keepClear = null)
    {
        var aim = focus ?? new TilePoint(stand.X, stand.Y - 1);
        TilePoint? best = null;
        int bestScore = int.MaxValue;
        for (int y = stand.Y - SearchRadius; y <= stand.Y + SearchRadius; y++)
            for (int x = stand.X - SearchRadius; x <= stand.X + SearchRadius + 1; x++)
            {
                var t = new TilePoint(x, y);
                if (!Reach.FromStand(stand, t, reach, margin: 1f)) continue;
                if (grid.Get(t).Kind != TileKind.Air) continue; // known, empty of blocks and liquid
                var c = cell(t);
                if (!c.Free) continue;
                if (!c.Wall && !Anchored(grid, t, pending)) continue;
                int score = (Math.Abs(x - aim.X) + Math.Abs(y - aim.Y)) * 2;
                if (keepClear != null && keepClear.Contains(t)) score += KeepClearPenalty;
                if (score >= bestScore) continue;
                bestScore = score;
                best = t;
            }
        return best;
    }

    private static bool Anchored(ITileGrid grid, TilePoint t, IReadOnlySet<TilePoint>? pending)
    {
        foreach (var n in new[] { new TilePoint(t.X - 1, t.Y), new TilePoint(t.X + 1, t.Y), new TilePoint(t.X, t.Y + 1) })
            if (grid.Get(n).Kind == TileKind.Solid && (pending == null || !pending.Contains(n))) return true;
        return false;
    }
}

/// <summary>explore's structured result (report.result).</summary>
/// <param name="Covered">Tiles of progress in the requested direction.</param>
/// <param name="Dug">Tiles broken while digging.</param>
/// <param name="TorchesPlaced">Torches placed.</param>
/// <param name="StoppedBecause">Why it stopped short (null on success).</param>
public sealed record ExploreResult(int Covered, int Dug, int TorchesPlaced,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StoppedBecause = null);
