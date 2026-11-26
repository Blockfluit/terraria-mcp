using LlmPlayer.Core.Observe;

namespace LlmPlayer.Core.Interact;

/// <summary>One stack in a container or the inventory, as reported to the LLM.</summary>
public sealed record SlotItem(int Slot, string Name, int Stack);

/// <summary>Net change of one item name in a container (positive = more in the container).</summary>
public sealed record ItemDelta(string Name, int Count);

/// <summary>
/// Pure helpers for chest/bed/door interactions, mirroring vanilla 1.4.4 arithmetic from
/// Player.TileInteractionsUse (decompiled). Frames are in pixels (18 px per sub-tile).
/// </summary>
public static class InteractMath
{
    /// <summary>Player.IsInTileInteractionRange with TileReachCheckSettings.Simple (range capped at 20): the
    /// hitbox (px) widened by rangeX/rangeY tiles must reach the target tile.</summary>
    public static bool InTileInteractionRange(float px, float py, int width, int height, int rangeX, int rangeY, int tx, int ty)
    {
        int x = Math.Min(rangeX, 20), y = Math.Min(rangeY, 20);
        return px / 16f - x <= tx && (px + width) / 16f + x - 1f >= tx
            && py / 16f - y <= ty && (py + height) / 16f + y - 2f >= ty;
    }

    /// <summary>Top-left of a 2-wide basic chest (or piggy bank / safe) from a clicked tile: vanilla strips
    /// frameX/18 down to 0..1 and subtracts frameY/18.</summary>
    public static (int X, int Y) ChestOrigin(int x, int y, int frameX, int frameY) =>
        (x - (frameX / 18) % 2, y - frameY / 18);

    /// <summary>Top-left of a 3-wide dresser from a clicked tile (vanilla: frameX/18 % 3, frameY/18).</summary>
    public static (int X, int Y) DresserOrigin(int x, int y, int frameX, int frameY) =>
        (x - (frameX / 18) % 3, y - frameY / 18);

    /// <summary>
    /// Where right-clicking a bed tile sets the spawn point (vanilla bed branch): x from the frame (left-facing
    /// beds: 2 right of the bed's left edge as seen from the frame, right-facing: +5 variant), y = the row under the bed.
    /// </summary>
    public static (int X, int Y) BedSpawnPoint(int x, int y, int frameX, int frameY)
    {
        int sx = x - frameX / 18 + (frameX >= 72 ? 5 : 2);
        int sub = frameY / 18;
        while (sub > 1) sub -= 2;
        return (sx, y - sub + 2);
    }

    /// <summary>Player.IsHoveringOverABottomSideOfABed: the foot half of the bed sets spawn, the head half sleeps.</summary>
    public static bool IsBedFootSide(int frameX)
    {
        bool flipped = frameX / 72 == 1;
        bool foot = frameX % 72 < 36;
        return flipped ? !foot : foot;
    }

    /// <summary>
    /// How much to move from each matching slot to transfer <paramref name="count"/> items (null = everything),
    /// first matching slot first. Returns (slot index, amount) pairs; amounts are positive.
    /// </summary>
    public static List<(int Slot, int Amount)> PlanTake(IReadOnlyList<(int Slot, int Stack)> matches, int? count)
    {
        var plan = new List<(int, int)>();
        long left = count ?? long.MaxValue;
        foreach (var (slot, stack) in matches)
        {
            if (left <= 0) break;
            if (stack <= 0) continue;
            int take = (int)Math.Min(stack, left);
            plan.Add((slot, take));
            left -= take;
        }
        return plan;
    }

    /// <summary>Items whose name equals the query case-insensitively (display name), else by internal name, as
    /// <see cref="ItemMatcher"/> does for one slot, but every matching slot (in order).</summary>
    public static List<int> MatchAll(IReadOnlyList<ItemName?> slots, string query)
    {
        var q = query.Trim();
        var list = new List<int>();
        if (q.Length == 0) return list;
        for (int i = 0; i < slots.Count; i++)
            if (slots[i] is { } s && string.Equals(s.Display, q, StringComparison.OrdinalIgnoreCase)) list.Add(i);
        if (list.Count > 0) return list;
        for (int i = 0; i < slots.Count; i++)
            if (slots[i] is { } s && string.Equals(s.Internal, q, StringComparison.OrdinalIgnoreCase)) list.Add(i);
        return list;
    }

    /// <summary>Per-name change between two snapshots of a container, in order of first appearance; unchanged
    /// names are left out.</summary>
    public static List<ItemDelta> Diff(IEnumerable<SlotItem> before, IEnumerable<SlotItem> after)
    {
        var order = new List<string>();
        var totals = new Dictionary<string, int>();
        void Add(SlotItem it, int sign)
        {
            if (!totals.ContainsKey(it.Name))
            {
                totals[it.Name] = 0;
                order.Add(it.Name);
            }
            totals[it.Name] += sign * it.Stack;
        }
        foreach (var it in before) Add(it, -1);
        foreach (var it in after) Add(it, +1);
        return order.Where(n => totals[n] != 0).Select(n => new ItemDelta(n, totals[n])).ToList();
    }

    /// <summary>Readable text for WorldGen.roomCheckFailureReason (enum name), as a reason a bed can't set spawn.</summary>
    public static string RoomFailureText(string reason) => reason switch
    {
        "TooCloseToWorldEdge" => "the house is too close to the world edge",
        "RoomIsTooBig" => "the room is too big (or not enclosed: walls/background walls missing)",
        "RoomIsTooSmall" => "the room is too small (needs at least 60 tiles inside)",
        "HoleInWallIsTooBig" => "there is a hole in the background wall",
        "RoomCheckStartedInASolidTile" => "the spot above the bed is solid",
        _ => "this is not valid housing (needs walls, background walls, a door, a light source, a table and a chair)",
    };
}
