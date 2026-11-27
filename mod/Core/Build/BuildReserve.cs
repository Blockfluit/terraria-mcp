namespace LlmPlayer.Core.Build;

/// <summary>What a build step of a walk places.</summary>
public enum BuildPick { None, Platform, Block }

/// <summary>
/// Keeps a reserve of platforms through long walks. Seen in game: BuildDown descents, bridges and climbs used up all
/// 266 Wood Platforms; later descents and build_platforms failed with "no 'Wood Platform' in inventory".
/// <list type="bullet">
/// <item>Budget: the pathfinder may plan with every cheap block plus the platforms above the reserve. When no plan
/// fits that budget the walk releases the reserve and plans with everything (<see cref="ShouldRelease"/>): the
/// trip comes first, but only when it can't succeed otherwise.</item>
/// <item>Pick: a BuildDown step needs a platform (the player drops through it to the next one), so it takes one
/// whenever there is any. Bridges and climbs work with blocks too: they use platforms only above the reserve, then
/// blocks (dirt, stone …), and the reserve only when no block is left.</item>
/// </list>
/// Pure.
/// </summary>
public static class BuildReserve
{
    public const int DefaultPlatforms = 20;

    public static int Budget(int platforms, int blocks, int reserve, bool released) =>
        Math.Max(0, blocks) + (released ? Math.Max(0, platforms) : Math.Max(0, platforms - Math.Max(0, reserve)));

    /// <summary>Platforms within <see cref="Budget"/> that bridges and climbs place before any block (what
    /// <see cref="Pick"/> takes first): the planner's MovementProfile.BuildPlatformCount.</summary>
    public static int PlatformBudget(int platforms, int reserve, bool released) =>
        released ? Math.Max(0, platforms) : Math.Max(0, platforms - Math.Max(0, reserve));

    public static BuildPick Pick(bool buildDown, int platforms, int blocks, int reserve)
    {
        if (buildDown) return platforms > 0 ? BuildPick.Platform : blocks > 0 ? BuildPick.Block : BuildPick.None;
        if (platforms > Math.Max(0, reserve)) return BuildPick.Platform;
        if (blocks > 0) return BuildPick.Block;
        return platforms > 0 ? BuildPick.Platform : BuildPick.None;
    }

    /// <summary>Retry a failed plan without the reserve? Only when the reserve actually held items back and the failure
    /// isn't one that more items can't fix (a goal inside solid ground).</summary>
    public static bool ShouldRelease(bool released, bool allowBuild, int platforms, int blocks, int reserve, string? failure) =>
        !released && allowBuild && Budget(platforms, blocks, reserve, false) < Budget(platforms, blocks, reserve, true)
        && failure?.StartsWith("goal_not_standable") != true;

    /// <summary>move_to's note when the walk ate into the reserve or left it short; null when all is well.</summary>
    public static string? Note(int platformsLeft, int reserve, bool released)
    {
        if (released) return $"used the platform reserve: no route fitted the {reserve}-platform reserve; {platformsLeft} platforms left";
        if (reserve > 0 && platformsLeft <= reserve) return $"{platformsLeft} platforms left (reserve {reserve}): bridges and climbs use blocks now; craft more platforms for descents";
        return null;
    }
}
