using System.Text;
using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Map;

/// <summary>
/// Compact ASCII map for the LLM. Output:
/// <code>
/// origin: x=&lt;left&gt; y=&lt;top&gt;
/// &lt;2r+1 rows of 2r+1 glyphs, top row first&gt;
/// legend:
/// &lt;glyph&gt; &lt;name&gt;   (one line per glyph present, in order of first appearance)
/// </code>
/// Precedence per cell: player '@' &gt; marker &gt; TileInfo.Glyph &gt; kind default.
/// </summary>
public static class MapRenderer
{
    public const int MaxRadius = 40;

    public static string Render(ITileGrid grid, TilePoint center, int radius, TilePoint player,
        IReadOnlyDictionary<TilePoint, char>? markers = null)
    {
        int r = Math.Clamp(radius, 0, MaxRadius);
        int left = center.X - r, top = center.Y - r, size = 2 * r + 1;

        // glyph -> names (insertion-ordered)
        var legend = new List<(char Glyph, List<string> Names)>();
        void Note(char glyph, string name)
        {
            int i = legend.FindIndex(e => e.Glyph == glyph);
            if (i < 0) legend.Add((glyph, new List<string> { name }));
            else if (!legend[i].Names.Contains(name)) legend[i].Names.Add(name);
        }

        var sb = new StringBuilder(size * (size + 1) + 128);
        sb.Append("origin: x=").Append(left).Append(" y=").Append(top).Append('\n');
        for (int y = top; y < top + size; y++)
        {
            for (int x = left; x < left + size; x++)
            {
                var p = new TilePoint(x, y);
                char c;
                if (p == player) { c = '@'; Note(c, "player"); }
                else if (markers != null && markers.TryGetValue(p, out var m)) { c = m; Note(c, "marker"); }
                else
                {
                    var t = grid.Get(p);
                    c = t.Glyph != '\0' ? t.Glyph : DefaultGlyph(t.Kind);
                    Note(c, t.Glyph != '\0' && t.Name != null ? t.Name : KindName(t.Kind));
                }
                sb.Append(c);
            }
            sb.Append('\n');
        }
        sb.Append("legend:\n");
        foreach (var (glyph, names) in legend)
            sb.Append(glyph).Append(' ').Append(string.Join('/', names)).Append('\n');
        return sb.ToString();
    }

    private static char DefaultGlyph(TileKind kind) => kind switch
    {
        TileKind.Air => '.',
        TileKind.Solid => '#',
        TileKind.Platform => '=',
        TileKind.Water => '~',
        TileKind.Lava => 'L',
        TileKind.Honey => 'h',
        TileKind.Hazard => '^',
        _ => '?',
    };

    private static string KindName(TileKind kind) => kind switch
    {
        TileKind.Air => "air",
        TileKind.Solid => "solid",
        TileKind.Platform => "platform",
        TileKind.Water => "water",
        TileKind.Lava => "lava",
        TileKind.Honey => "honey",
        TileKind.Hazard => "hazard",
        _ => "unknown",
    };
}
