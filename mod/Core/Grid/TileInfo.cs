namespace LlmPlayer.Core.Grid;

public enum TileKind { Air, Solid, Platform, Water, Lava, Honey, Hazard, Unknown }

/// <summary>
/// What the AI knows about one tile. PickPowerRequired == int.MaxValue means unbreakable.
/// Glyph/Name are optional overrides for the ASCII map (e.g. 'c' / "Copper Ore").
/// Door: a closed door the player can open by interacting with it (Kind stays Solid: it blocks until opened).
/// Locked doors are not flagged.
/// </summary>
public readonly record struct TileInfo(
    TileKind Kind,
    int PickPowerRequired = 0,
    bool Protected = false,
    char Glyph = '\0',
    string? Name = null,
    bool Door = false,
    bool Wall = false,
    bool Occupied = false)
{
    public static readonly TileInfo Air = new(TileKind.Air);
    public static readonly TileInfo Unbreakable = new(TileKind.Solid, int.MaxValue);

    public bool IsSolid => Kind == TileKind.Solid;
    public bool IsStandable => Kind is TileKind.Solid or TileKind.Platform;
    public bool BlocksBody => Kind == TileKind.Solid;
    public bool IsClosedDoor => Door && Kind == TileKind.Solid;
}
