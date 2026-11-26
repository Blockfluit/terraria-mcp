namespace LlmPlayer.Core.Grid;

public static class SupportRules
{
    /// <summary>
    /// True if (x,y) touches a protected object orthogonally. Breaking such a block can pop furniture
    /// (work benches, anvils, tables, doors) off as an item, so it's protected too.
    /// </summary>
    public static bool SupportsProtectedObject(Func<int, int, bool> isProtectedObject, int x, int y) =>
        !isProtectedObject(x, y)
        && (isProtectedObject(x, y - 1) || isProtectedObject(x, y + 1) || isProtectedObject(x - 1, y) || isProtectedObject(x + 1, y));
}
