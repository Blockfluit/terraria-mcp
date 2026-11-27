using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Build;

public enum DoorSide { Left, Right }

/// <summary>
/// Geometry of a one-room house for <c>build_house</c>. (<see cref="X"/>, <see cref="FloorY"/>) is the bottom-left
/// tile of the frame (the floor row); <see cref="Width"/> × <see cref="Height"/> are outer sizes including the frame.
/// <list type="bullet">
/// <item>Frame: the floor row, the ceiling row and both side columns. The door replaces the 3 side-column tiles just
/// above the floor on the <see cref="Door"/> side (a vanilla door is 1×3 and needs a solid tile above and below).</item>
/// <item>Interior: everything inside the frame; every interior tile gets a background wall (vanilla's room check
/// needs a house wall or a solid tile within 2 tiles of each open tile, row- and column-wise; walls everywhere is the
/// simple way to be sure).</item>
/// <item>Furniture sits on the floor in the corner away from the door: chair in the last interior column, the table
/// (3×2, origin bottom-middle) or work bench (2×1, origin left) aimed two columns in from it. Both aims cover the same
/// tile, so either item works. The torch hangs on the back wall in the top interior row above the chair.</item>
/// <item>The free columns next to the door are where the player stands and where a town NPC can have its home spot
/// (vanilla ScoreRoom wants a floor tile with 3×3 of open space above it).</item>
/// </list>
/// Vanilla counts the room as every tile its flood fill touches, frame included, so Width × Height must be at least
/// <see cref="MinTiles"/> (60) and below <see cref="MaxTiles"/> (750).
/// </summary>
public sealed record HouseLayout(int X, int FloorY, int Width, int Height, DoorSide Door)
{
    public const int DefaultWidth = 10, DefaultHeight = 6;
    public const int MinWidth = 8, MaxWidth = 30, MinHeight = 5;
    /// <summary>Ceiling must stay within vanilla block reach (tileRangeY 4) of a player standing on the floor.</summary>
    public const int MaxHeight = 7;
    public const int MinTiles = 60, MaxTiles = 749;

    public int Left => X;
    public int Right => X + Width - 1;
    public int CeilingY => FloorY - Height + 1;
    public int InnerLeft => X + 1;
    public int InnerRight => Right - 1;
    public int InnerTop => CeilingY + 1;
    public int InnerBottom => FloorY - 1;
    public int DoorX => Door == DoorSide.Left ? Left : Right;
    public int FarX => Door == DoorSide.Left ? Right : Left;

    /// <summary>Why this size can't make a valid house (null = fine).</summary>
    public static string? SizeProblem(int width, int height)
    {
        if (width < MinWidth || width > MaxWidth) return $"width must be {MinWidth}..{MaxWidth} (outer, frame included)";
        if (height < MinHeight || height > MaxHeight)
            return $"height must be {MinHeight}..{MaxHeight} (outer, frame included; the ceiling must stay within reach from the floor)";
        if (width * height < MinTiles) return $"{width}x{height} = {width * height} tiles; vanilla needs at least {MinTiles} (frame included), e.g. 10x6";
        if (width * height > MaxTiles) return $"{width}x{height} is too big for vanilla housing (at most {MaxTiles} tiles)";
        return null;
    }

    /// <summary>The 3 door tiles, top to bottom.</summary>
    public IReadOnlyList<TilePoint> DoorTiles => new[] { new TilePoint(DoorX, FloorY - 3), new TilePoint(DoorX, FloorY - 2), new TilePoint(DoorX, FloorY - 1) };

    /// <summary>Where to aim the door item: the middle tile (vanilla picks the door's origin row from the anchors).</summary>
    public TilePoint DoorAim => new(DoorX, FloorY - 2);

    public bool IsDoorTile(TilePoint t) => t.X == DoorX && t.Y >= FloorY - 3 && t.Y <= FloorY - 1;

    /// <summary>Every frame tile (door tiles excluded).</summary>
    public IEnumerable<TilePoint> FrameTiles()
    {
        for (int x = Left; x <= Right; x++) yield return new TilePoint(x, FloorY);
        for (int x = Left; x <= Right; x++) yield return new TilePoint(x, CeilingY);
        foreach (int x in new[] { Left, Right })
            for (int y = InnerTop; y <= InnerBottom; y++)
            {
                var t = new TilePoint(x, y);
                if (!IsDoorTile(t)) yield return t;
            }
    }

    public IEnumerable<TilePoint> InteriorTiles()
    {
        for (int y = InnerTop; y <= InnerBottom; y++)
            for (int x = InnerLeft; x <= InnerRight; x++)
                yield return new TilePoint(x, y);
    }

    public bool IsInterior(TilePoint t) => t.X >= InnerLeft && t.X <= InnerRight && t.Y >= InnerTop && t.Y <= InnerBottom;

    private int Inward => Door == DoorSide.Left ? -1 : 1; // from the far wall toward the door

    /// <summary>Chair: bottom tile (1×2, origin bottom), last interior column on the far side.</summary>
    public TilePoint ChairAim => new(FarX + Inward, InnerBottom);

    /// <summary>Table/work bench aim on the floor, two columns in from the chair.</summary>
    public TilePoint TableAim => new(FarX + 3 * Inward, InnerBottom);

    /// <summary>Tiles a 3×2 table (the larger option) covers.</summary>
    public IEnumerable<TilePoint> TableFootprint()
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = 0; dy <= 1; dy++)
                yield return new TilePoint(TableAim.X + dx, TableAim.Y - dy);
    }

    /// <summary>Torch: top interior row, above the chair.</summary>
    public TilePoint TorchTile => new(ChairAim.X, InnerTop);

    /// <summary>An open interior tile next to the door, to run vanilla's room check from.</summary>
    public TilePoint CheckTile => new(Door == DoorSide.Left ? Left + 2 : Right - 2, InnerBottom);

    /// <summary>Feet tiles the player can stand on inside (body 2 wide, on the floor).</summary>
    public IEnumerable<TilePoint> InsideStands()
    {
        for (int x = InnerLeft; x <= InnerRight - 1; x++) yield return new TilePoint(x, InnerBottom);
    }

    /// <summary>Feet tile just outside the door, where the player walks out to when done.</summary>
    public TilePoint OutsideStand => Door == DoorSide.Left ? new TilePoint(Left - 2, InnerBottom) : new TilePoint(Right + 1, InnerBottom);

    public override string ToString() => $"{Width}x{Height} house at ({X},{FloorY}), door {(Door == DoorSide.Left ? "left" : "right")}";
}

/// <summary>What one build step places.</summary>
/// <remarks><see cref="Flatten"/>: hammer an existing half block / slope in the frame back to a full block.</remarks>
public enum HousePart { Frame, Door, BackWall, Table, Chair, Torch, Flatten }

public readonly record struct HouseStep(HousePart Part, TilePoint Target);

/// <summary>
/// Placement order for <c>build_house</c>. Vanilla only places a block or wall next to something to attach to: a
/// block needs a neighbouring solid tile (or a wall behind it / next to it), a wall needs a neighbouring tile or wall,
/// a door needs solid tiles above and below, furniture needs floor, a torch a wall behind it. So:
/// <list type="number">
/// <item>Frame, in a preferred order (floor; far wall bottom-up; ceiling from the far side; door-side wall top-down,
/// which hangs from the ceiling), each next tile being the first one in that order that is anchored by what exists or
/// was placed before (<see cref="AnchoredOrder"/>). Tiles that never get an anchor are returned separately.</item>
/// <item>Flatten: existing half blocks and slopes in the frame are hammered to full blocks (a door or furniture
/// can't stand on one; the game refuses the placement), floor first.</item>
/// <item>Door (its anchors are frame tiles).</item>
/// <item>Background walls, bottom row first, each attached to the frame or an earlier wall.</item>
/// <item>Table, chair, then the torch (on the back wall).</item>
/// </list>
/// </summary>
public static class HousePlan
{
    /// <summary>Frame tiles in placement order. <paramref name="toPlace"/>: frame tiles still empty;
    /// <paramref name="anchorAt"/>: does an existing tile/wall at this position anchor a neighbour?
    /// <paramref name="unanchored"/> gets the tiles that can never be attached.</summary>
    public static List<TilePoint> FrameOrder(HouseLayout l, ISet<TilePoint> toPlace, Func<TilePoint, bool> anchorAt,
        out List<TilePoint> unanchored)
    {
        var pref = new List<TilePoint>();
        void Add(TilePoint t) { if (toPlace.Contains(t) && !pref.Contains(t)) pref.Add(t); }
        int dir = l.Door == DoorSide.Left ? -1 : 1; // far side → door side
        // Floor from the far side toward the door.
        for (int i = 0; i < l.Width; i++) Add(new TilePoint(l.FarX + dir * i, l.FloorY));
        for (int y = l.InnerBottom; y >= l.InnerTop; y--) Add(new TilePoint(l.FarX, y));
        for (int i = 0; i < l.Width; i++) Add(new TilePoint(l.FarX + dir * i, l.CeilingY));
        for (int y = l.InnerTop; y <= l.InnerBottom; y++)
        {
            var t = new TilePoint(l.DoorX, y);
            if (!l.IsDoorTile(t)) Add(t);
        }
        return AnchoredOrder(pref, anchorAt, out unanchored);
    }

    /// <summary>Background walls (interior tiles in <paramref name="toPlace"/>) bottom row first, each next to a
    /// tile or wall (the frame is complete by then, so <paramref name="anchorAt"/> is usually true at its tiles).</summary>
    public static List<TilePoint> WallOrder(HouseLayout l, ISet<TilePoint> toPlace, Func<TilePoint, bool> anchorAt, out List<TilePoint> unanchored)
    {
        var pref = new List<TilePoint>();
        int dir = l.Door == DoorSide.Left ? -1 : 1;
        for (int y = l.InnerBottom; y >= l.InnerTop; y--)
            for (int i = 1; i < l.Width - 1; i++)
            {
                var t = new TilePoint(l.FarX + dir * i, y);
                if (toPlace.Contains(t)) pref.Add(t);
            }
        return AnchoredOrder(pref, anchorAt, out unanchored);
    }

    /// <summary>
    /// Greedy anchored order: repeatedly take the first tile in <paramref name="preferred"/> that has a 4-neighbour (or
    /// itself) anchored — an existing anchor (<paramref name="anchorAt"/>) or a tile taken earlier.
    /// </summary>
    public static List<TilePoint> AnchoredOrder(IReadOnlyList<TilePoint> preferred, Func<TilePoint, bool> anchorAt, out List<TilePoint> unanchored)
    {
        var remaining = new List<TilePoint>(preferred);
        var placed = new HashSet<TilePoint>();
        var order = new List<TilePoint>(preferred.Count);
        bool Anchors(TilePoint p) => placed.Contains(p) || anchorAt(p);
        bool Anchored(TilePoint t) => Anchors(t) || Anchors(new TilePoint(t.X - 1, t.Y)) || Anchors(new TilePoint(t.X + 1, t.Y))
            || Anchors(new TilePoint(t.X, t.Y - 1)) || Anchors(new TilePoint(t.X, t.Y + 1));
        while (remaining.Count > 0)
        {
            int i = PlatformPlan.NextIndex(remaining, Anchored);
            if (i < 0) break;
            order.Add(remaining[i]);
            placed.Add(remaining[i]);
            remaining.RemoveAt(i);
        }
        unanchored = remaining;
        return order;
    }

    /// <summary>
    /// The whole step list: frame, flatten (<paramref name="flatten"/>), door, walls, table, chair, torch. <paramref name="frameToPlace"/> /
    /// <paramref name="wallsToPlace"/>: tiles still missing; <paramref name="tileAnchor"/>: an existing tile (or wall)
    /// that anchors blocks; <paramref name="wallAnchor"/>: an existing tile or wall that anchors walls (frame tiles
    /// count as placed). Problems (unanchorable tiles) go to <paramref name="problem"/>.
    /// </summary>
    public static List<HouseStep> Steps(HouseLayout l, ISet<TilePoint> frameToPlace, ISet<TilePoint> wallsToPlace,
        Func<TilePoint, bool> tileAnchor, Func<TilePoint, bool> wallAnchor, out string? problem, ISet<TilePoint>? flatten = null)
    {
        problem = null;
        var steps = new List<HouseStep>();
        var frame = FrameOrder(l, frameToPlace, tileAnchor, out var loose);
        if (loose.Count > 0)
            problem = $"nothing to attach {loose.Count} frame tile(s) to (first {loose[0]}): build on solid ground";
        steps.AddRange(frame.Select(t => new HouseStep(HousePart.Frame, t)));
        if (flatten != null)
            steps.AddRange(flatten.OrderByDescending(t => t.Y).ThenBy(t => t.X).Select(t => new HouseStep(HousePart.Flatten, t)));
        steps.Add(new HouseStep(HousePart.Door, l.DoorAim));
        var frameSet = new HashSet<TilePoint>(l.FrameTiles());
        var walls = WallOrder(l, wallsToPlace, p => frameSet.Contains(p) || l.IsDoorTile(p) || wallAnchor(p), out var looseWalls);
        if (looseWalls.Count > 0 && problem == null)
            problem = $"nothing to attach {looseWalls.Count} background wall(s) to (first {looseWalls[0]})";
        steps.AddRange(walls.Select(t => new HouseStep(HousePart.BackWall, t)));
        steps.Add(new HouseStep(HousePart.Table, l.TableAim));
        steps.Add(new HouseStep(HousePart.Chair, l.ChairAim));
        steps.Add(new HouseStep(HousePart.Torch, l.TorchTile));
        return steps;
    }

    /// <summary>
    /// Where to stand (inside the house, on the floor) to place <c>upcoming[0]</c> and as many following targets as
    /// possible: candidates are <see cref="HouseLayout.InsideStands"/> that <paramref name="canStand"/>, reach the
    /// target with <paramref name="margin"/> to spare and (for a solid block) don't cover it with the body; best =
    /// highest <see cref="PlatformPlan.Coverage"/> (capped at 6), then nearest to <paramref name="here"/>. Null if none
    /// qualifies (e.g. the floor isn't built yet).
    /// </summary>
    public static TilePoint? InsideStand(HouseLayout l, TilePoint here, IReadOnlyList<TilePoint> upcoming, ReachSpec reach, bool solid,
        Func<TilePoint, bool> canStand, float margin = 0.25f)
    {
        if (upcoming.Count == 0) return null;
        TilePoint? best = null;
        int bestCover = -1, bestDist = int.MaxValue;
        foreach (var s in l.InsideStands())
        {
            if (solid && PlatformPlan.InBody(s, upcoming[0])) continue;
            if (!Reach.FromStand(s, upcoming[0], reach, margin) || !canStand(s)) continue;
            int cover = Math.Min(6, PlatformPlan.Coverage(s, upcoming, reach, solid, margin));
            int dist = Math.Abs(s.X - here.X) + Math.Abs(s.Y - here.Y);
            if (cover > bestCover || (cover == bestCover && dist < bestDist))
            {
                best = s;
                bestCover = cover;
                bestDist = dist;
            }
        }
        return best;
    }
}
