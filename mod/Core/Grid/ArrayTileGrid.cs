namespace LlmPlayer.Core.Grid;

/// <summary>In-memory grid. Used by tests (via Parse) and for snapshots.</summary>
public sealed class ArrayTileGrid : ITileGrid
{
    private readonly int _originX, _originY;
    private readonly TileInfo[,] _tiles; // [x, y] relative to origin

    public ArrayTileGrid(int originX, int originY, TileInfo[,] tiles)
    {
        _originX = originX;
        _originY = originY;
        _tiles = tiles;
    }

    public int Width => _tiles.GetLength(0);
    public int Height => _tiles.GetLength(1);

    public TileInfo Get(int x, int y)
    {
        int lx = x - _originX, ly = y - _originY;
        if (lx < 0 || ly < 0 || lx >= Width || ly >= Height) return TileInfo.Unbreakable;
        return _tiles[lx, ly];
    }

    public void Set(int x, int y, TileInfo t) => _tiles[x - _originX, y - _originY] = t;

    private const string MarkerLetters = "SGABCD";

    /// <summary>
    /// Parse ASCII rows (origin 0,0, row 0 = top). Glyphs: '#' solid, 'X' unbreakable,
    /// 'P' protected solid, '=' platform, '.' air, '~' water, 'L' lava, '^' hazard,
    /// '?' unknown; any of "SGABCD" is air recorded as a marker.
    /// </summary>
    public static (ArrayTileGrid Grid, Dictionary<char, TilePoint> Markers) Parse(params string[] rows)
    {
        int h = rows.Length, w = rows.Length == 0 ? 0 : rows.Max(r => r.Length);
        var tiles = new TileInfo[w, h];
        var markers = new Dictionary<char, TilePoint>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                char c = x < rows[y].Length ? rows[y][x] : '.';
                if (MarkerLetters.Contains(c))
                {
                    markers[c] = new TilePoint(x, y);
                    tiles[x, y] = TileInfo.Air;
                    continue;
                }
                tiles[x, y] = c switch
                {
                    '#' => new TileInfo(TileKind.Solid),
                    'X' => TileInfo.Unbreakable,
                    'P' => new TileInfo(TileKind.Solid, Protected: true),
                    '=' => new TileInfo(TileKind.Platform),
                    '.' => TileInfo.Air,
                    '~' => new TileInfo(TileKind.Water),
                    'L' => new TileInfo(TileKind.Lava),
                    'h' => new TileInfo(TileKind.Honey),
                    '^' => new TileInfo(TileKind.Hazard),
                    '?' => new TileInfo(TileKind.Unknown),
                    _ => throw new ArgumentException($"Unknown glyph '{c}' at ({x},{y})"),
                };
            }
        return (new ArrayTileGrid(0, 0, tiles), markers);
    }
}
