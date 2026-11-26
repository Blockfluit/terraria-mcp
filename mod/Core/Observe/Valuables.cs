namespace LlmPlayer.Core.Observe;

/// <summary>Things worth finding in caves (find category "valuable", explore_caves stop_on).</summary>
public enum ValuableKind { LifeCrystal, LifeFruit, Pot, Chest, EnchantedSword, Altar, Orb, Ore, FallenStar }

/// <summary>
/// Pure rules about natural loot tiles (vanilla 1.4.4 tile ids and frames, 18 px per sub-tile):
/// <list type="bullet">
/// <item><see cref="IsBreakableLoot"/>: world-generated multi-tile objects that are loot, not the user's builds, so
/// Protection doesn't protect them by type (they stay protected inside NPC houses and near spawn): pots, life
/// crystals, life fruit and the sword in stone of an Enchanted Sword shrine.</item>
/// <item>Never loot: Shadow Orbs / Crimson Hearts (breaking one has world effects: meteorite, goblin army, and the
/// third summons the Eater of Worlds / Brain of Cthulhu) and Demon / Crimson Altars (need a hammer and Hardmode;
/// they're a crafting station for boss summons). Chests, doors and stations stay protected too.</item>
/// </list>
/// </summary>
public static class Valuables
{
    // Vanilla TileID values.
    public const int Heart = 12, DemonAltar = 26, Pots = 28, ShadowOrbs = 31, LargePiles2 = 187, LifeFruit = 236;

    /// <summary>LargePiles2 style (frameX / 54) of the sword in stone that drops the Enchanted Sword.</summary>
    public const int SwordStyle = 17;
    /// <summary>The style next to it: the same shrine sprite (decoy shrines use it; may drop nothing).</summary>
    public const int SwordDecoyStyle = 18;
    private const int LargePileFrameWidth = 54;

    public static bool IsSwordInStone(int type, int frameX) =>
        type == LargePiles2 && frameX / LargePileFrameWidth is SwordStyle or SwordDecoyStyle;

    /// <summary>Natural loot the AI may break (by type; the location rules of Protection still apply).</summary>
    public static bool IsBreakableLoot(int type, int frameX) =>
        type is Heart or Pots or LifeFruit || IsSwordInStone(type, frameX);

    /// <summary>Type-only part of <see cref="IsBreakableLoot"/> (for callers without the frame).</summary>
    public static bool IsLootType(int type) => type is Heart or Pots or LifeFruit;

    /// <summary>Which valuable a tile is (containers come from the game: <paramref name="isContainer"/>), or null.
    /// Ores aren't classified here (find has its own ore category).</summary>
    public static ValuableKind? Classify(int type, int frameX, bool isContainer)
    {
        if (isContainer) return ValuableKind.Chest;
        return type switch
        {
            Heart => ValuableKind.LifeCrystal,
            LifeFruit => ValuableKind.LifeFruit,
            Pots => ValuableKind.Pot,
            ShadowOrbs => ValuableKind.Orb,
            DemonAltar => ValuableKind.Altar,
            LargePiles2 when IsSwordInStone(type, frameX) => ValuableKind.EnchantedSword,
            _ => null,
        };
    }

    /// <summary>Display name for tile valuables other than chests (whose name comes from the game).</summary>
    public static string Name(ValuableKind kind, int frameX) => kind switch
    {
        ValuableKind.LifeCrystal => "Life Crystal",
        ValuableKind.LifeFruit => "Life Fruit",
        ValuableKind.Pot => "Pot",
        ValuableKind.Orb => frameX >= 36 ? "Crimson Heart (don't break)" : "Shadow Orb (don't break)",
        ValuableKind.Altar => frameX >= 54 ? "Crimson Altar" : "Demon Altar",
        ValuableKind.EnchantedSword => frameX / LargePileFrameWidth == SwordStyle
            ? "Enchanted Sword (in stone)"
            : "Sword in Stone (maybe a decoy)",
        ValuableKind.FallenStar => "Fallen Star",
        ValuableKind.Chest => "Chest",
        ValuableKind.Ore => "Ore",
        _ => kind.ToString(),
    };

    /// <summary>Kinds the AI can break and pick up (collect).</summary>
    public static bool IsCollectible(ValuableKind kind) =>
        kind is ValuableKind.LifeCrystal or ValuableKind.LifeFruit or ValuableKind.Pot or ValuableKind.EnchantedSword;

    private static readonly (ValuableKind Kind, string Key)[] Keys =
    {
        (ValuableKind.LifeCrystal, "life_crystal"), (ValuableKind.LifeFruit, "life_fruit"), (ValuableKind.Pot, "pot"),
        (ValuableKind.Chest, "chest"), (ValuableKind.EnchantedSword, "enchanted_sword"), (ValuableKind.Altar, "altar"),
        (ValuableKind.Orb, "orb"), (ValuableKind.Ore, "ore"), (ValuableKind.FallenStar, "fallen_star"),
    };

    /// <summary>All wire keys ("life_crystal", "pot", …), in enum order.</summary>
    public static IReadOnlyList<string> AllKeys { get; } = Keys.Select(k => k.Key).ToArray();

    public static string Key(ValuableKind kind) => Keys.First(k => k.Kind == kind).Key;

    public static bool TryParse(string? key, out ValuableKind kind)
    {
        var k = (key ?? "").Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        foreach (var (kd, name) in Keys)
            if (name == k) { kind = kd; return true; }
        kind = default;
        return false;
    }

    /// <summary>Default stop_on for explore_caves: the things worth walking over to.</summary>
    public static readonly ValuableKind[] DefaultStopOn =
        { ValuableKind.LifeCrystal, ValuableKind.Chest, ValuableKind.EnchantedSword, ValuableKind.LifeFruit };
}
