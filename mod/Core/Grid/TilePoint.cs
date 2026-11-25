namespace LlmPlayer.Core.Grid;

/// <summary>Absolute tile coordinate. Y grows downward (Terraria convention).</summary>
public readonly record struct TilePoint(int X, int Y)
{
    public static TilePoint operator +(TilePoint a, TilePoint b) => new(a.X + b.X, a.Y + b.Y);
    public static TilePoint operator -(TilePoint a, TilePoint b) => new(a.X - b.X, a.Y - b.Y);
    public override string ToString() => $"({X},{Y})";
}
