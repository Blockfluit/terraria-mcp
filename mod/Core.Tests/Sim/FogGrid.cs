using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Tests.Sim;

/// <summary>
/// The player's view of a world in the sim, like the in-game grid: tiles not revealed yet read as Unknown. Starts from a
/// "known" template (true where the map is already revealed) and reveals a square of <see cref="Radius"/> tiles around
/// the player as it moves (<see cref="Reveal"/>). Physics keeps using the true grid.
/// </summary>
public sealed class FogGrid : ITileGrid
{
    private readonly ArrayTileGrid _truth;
    private readonly bool[,] _known;

    public FogGrid(ArrayTileGrid truth, bool[,] known, int radius = 10)
    {
        _truth = truth;
        _known = known;
        Radius = radius;
    }

    public int Radius { get; }

    public TileInfo Get(int x, int y)
    {
        if (x < 0 || y < 0 || x >= _truth.Width || y >= _truth.Height) return TileInfo.Unbreakable;
        return _known[x, y] ? _truth.Get(x, y) : new TileInfo(TileKind.Unknown);
    }

    public bool IsKnown(int x, int y) => x >= 0 && y >= 0 && x < _truth.Width && y < _truth.Height && _known[x, y];

    /// <summary>Reveal the square of <see cref="Radius"/> around the body standing at <paramref name="feet"/>.</summary>
    public void Reveal(TilePoint feet)
    {
        int cx = feet.X, cy = feet.Y - 1;
        for (int y = Math.Max(0, cy - Radius); y <= Math.Min(_truth.Height - 1, cy + Radius); y++)
            for (int x = Math.Max(0, cx - Radius); x <= Math.Min(_truth.Width - 1, cx + 1 + Radius); x++)
                _known[x, y] = true;
    }
}
