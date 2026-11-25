namespace LlmPlayer.Core.Grid;

/// <summary>Read-only tile access. Out-of-bounds must return TileInfo.Unbreakable.</summary>
public interface ITileGrid
{
    TileInfo Get(int x, int y);
}

public static class TileGridExtensions
{
    public static TileInfo Get(this ITileGrid g, TilePoint p) => g.Get(p.X, p.Y);
}
