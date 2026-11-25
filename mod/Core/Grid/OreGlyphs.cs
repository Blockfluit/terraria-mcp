namespace LlmPlayer.Core.Grid;

/// <summary>
/// Stable lowercase map glyph per ore tile type. Vanilla ores get fixed mnemonic letters;
/// other (modded) ores take a letter from the leftover pool by <c>type % pool.Length</c>, so a
/// type always gets the same letter. Pool letters can collide between modded ores, but the map
/// legend lists every name sharing a glyph.
/// Reserved: 'h' (honey) and 'i' (dropped-item marker). That's why iron is 'r'.
/// </summary>
public static class OreGlyphs
{
    public const string Reserved = "hi";
    public const char GemGlyph = 'v';

    public static readonly IReadOnlyDictionary<int, char> Vanilla = new Dictionary<int, char>
    {
        [7] = 'c',   // copper
        [166] = 't', // tin
        [6] = 'r',   // iron
        [167] = 'l', // lead
        [9] = 's',   // silver
        [168] = 'u', // tungsten
        [8] = 'g',   // gold
        [169] = 'p', // platinum
        [22] = 'd',  // demonite
        [204] = 'k', // crimtane
        [37] = 'm',  // meteorite
        [58] = 'e',  // hellstone
        [107] = 'o', // cobalt
        [221] = 'a', // palladium
        [108] = 'y', // mythril
        [222] = 'z', // orichalcum
        [111] = 'j', // adamantite
        [223] = 'w', // titanium
        [211] = 'b', // chlorophyte
        [408] = 'n', // luminite
        // Gems embedded in stone (sapphire, ruby, emerald, topaz, amethyst, diamond) share one glyph.
        [63] = GemGlyph, [64] = GemGlyph, [65] = GemGlyph, [66] = GemGlyph, [67] = GemGlyph, [68] = GemGlyph,
    };

    private static readonly char[] Pool = Enumerable.Range('a', 26).Select(c => (char)c)
        .Where(c => !Reserved.Contains(c) && !Vanilla.Values.Contains(c)).ToArray();

    public static char For(int tileType) =>
        Vanilla.TryGetValue(tileType, out var g) ? g : Pool[(int)((uint)tileType % (uint)Pool.Length)];
}
