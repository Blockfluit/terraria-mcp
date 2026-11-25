namespace LlmPlayer.Core.Grid;

public enum TileKind { Air, Solid, Platform, Water, Lava, Honey, Hazard, Unknown }

/// <summary>
/// What the AI knows about one tile. PickPowerRequired == int.MaxValue means unbreakable.
/// Glyph/Name are optional overrides for the ASCII map (e.g. 'c' / "Copper Ore").
/// </summary>
public readonly record struct TileInfo(
    TileKind Kind,
    int PickPowerRequired = 0,
    bool Protected = false,
    char Glyph = '\0',
    string? Name = null)
{
    public static readonly TileInfo Air = new(TileKind.Air);
    public static readonly TileInfo Unbreakable = new(TileKind.Solid, int.MaxValue);

    public bool IsSolid => Kind == TileKind.Solid;
    public bool IsStandable => Kind is TileKind.Solid or TileKind.Platform;
    public bool BlocksBody => Kind == TileKind.Solid;
}
