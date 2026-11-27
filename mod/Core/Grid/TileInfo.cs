namespace LlmPlayer.Core.Grid;

public enum TileKind { Air, Solid, Platform, Water, Lava, Honey, Hazard, Unknown }

/// <summary>A solid block's slope, as vanilla's <c>SlopeType</c> (same order and values). The name says which sides stay
/// solid: DownLeft = bottom and left (a floor slope whose surface falls toward the right, high at the left), DownRight =
/// bottom and right (high at the right); UpLeft / UpRight are ceiling slopes (top and left / top and right).</summary>
public enum TileSlope : byte { None = 0, DownLeft = 1, DownRight = 2, UpLeft = 3, UpRight = 4 }

/// <summary>
/// What the AI knows about one tile. PickPowerRequired == int.MaxValue means unbreakable.
/// Glyph/Name are optional overrides for the ASCII map (e.g. 'c' / "Copper Ore").
/// Door: a closed door the player can open by interacting with it (Kind stays Solid: it blocks until opened).
/// Locked doors are not flagged.
/// Slope / HalfBlock: the shape of a solid block (vanilla Tile.Slope / Tile.IsHalfBlock). Kind stays Solid; the
/// planner treats shaped blocks as full blocks for body clearance (conservative) and only the follower / sim care
/// that the player's feet ride on a floor slope or half block up to (almost) a tile lower.
/// </summary>
public readonly record struct TileInfo(
    TileKind Kind,
    int PickPowerRequired = 0,
    bool Protected = false,
    char Glyph = '\0',
    string? Name = null,
    bool Door = false,
    bool Wall = false,
    bool Occupied = false,
    TileSlope Slope = TileSlope.None,
    bool HalfBlock = false)
{
    public static readonly TileInfo Air = new(TileKind.Air);
    public static readonly TileInfo Unbreakable = new(TileKind.Solid, int.MaxValue);

    public bool IsSolid => Kind == TileKind.Solid;
    public bool IsStandable => Kind is TileKind.Solid or TileKind.Platform;
    public bool BlocksBody => Kind == TileKind.Solid;
    public bool IsClosedDoor => Door && Kind == TileKind.Solid;

    /// <summary>A solid block whose top surface lies below the tile's top: a half block or a floor slope
    /// (DownLeft/DownRight). The player walks over these without jumping and its feet sink into the tile's row.</summary>
    public bool IsLowTop => Kind == TileKind.Solid && (HalfBlock || Slope is TileSlope.DownLeft or TileSlope.DownRight);

    /// <summary>Short shape name for inspect ("half", "slope down-left"…), null for a full block.</summary>
    public string? ShapeName => HalfBlock ? "half block"
        : Slope switch
        {
            TileSlope.DownLeft => "slope down-left (floor, high at left)",
            TileSlope.DownRight => "slope down-right (floor, high at right)",
            TileSlope.UpLeft => "slope up-left (ceiling)",
            TileSlope.UpRight => "slope up-right (ceiling)",
            _ => null,
        };
}
