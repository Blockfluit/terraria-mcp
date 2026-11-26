using LlmPlayer.Bridge;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Tasks;
using LlmPlayer.Observe;
using LlmPlayer.Safety;
using Terraria;

namespace LlmPlayer.Tasks;

/// <summary>
/// Mine a list of tiles in the given order (<see cref="MiningOrder"/> for areas) with the best pickaxe, by walking
/// within reach and swinging at each one (<see cref="TileWorker"/>). Tiles that are already gone when their turn
/// comes are counted as done. Fails on the first tile it can't reach or break. A tile that becomes protected fails a
/// single-tile mine; an area mine (constructed with a <see cref="SkipReport"/>) skips it and goes on.
/// Progress: "mined 3/9" (+ "walking: N tiles remaining"). Validate with <see cref="Validate"/> before starting.
/// Area mines report <c>{mined, skipped, skippedReasons, skippedTiles}</c> as the task result.
/// </summary>
public sealed class MineTask : IGameTask<GameCtx>
{
    private readonly IReadOnlyList<TilePoint> _tiles;
    private readonly HashSet<TilePoint> _pending;
    private readonly HeldItemLease _lease = new();
    private readonly TileWorker _worker;
    private readonly SkipReport? _skips;
    private int _index = -1, _mined;

    public string Name { get; }
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }

    public object? Result => _skips == null ? null : new
    {
        Mined = _mined,
        Skipped = _skips.Count,
        SkippedReasons = _skips.ByReason,
        SkippedTiles = _skips.Examples,
    };

    /// <param name="skips">Area mode: tiles skipped by <see cref="Validate"/>; tiles that become protected are added.</param>
    public MineTask(IReadOnlyList<TilePoint> tiles, string name = "mine", SkipReport? skips = null)
    {
        _skips = skips;
        _tiles = tiles;
        _pending = new HashSet<TilePoint>(tiles);
        Name = name;
        _worker = new TileWorker(name, HeldItemLease.BestPickaxe, "pickaxe", _lease);
        Progress = $"mined 0/{tiles.Count}";
    }

    public GameTaskStatus Tick(GameCtx ctx)
    {
        if (_index < 0) Next(ctx);
        while (_index < _tiles.Count)
        {
            var t = _tiles[_index];
            bool gone = !HasMinableTile(t.X, t.Y);
            if (!gone && Protection.IsProtected(t.X, t.Y))
            {
                if (_skips == null) return Finish(ctx, GameTaskStatus.Failed, $"{t} became protected; stopped");
                _skips.Add(t, "protected");
                _mined--; // Next counts the tile as done
                Next(ctx);
                continue;
            }

            var s = _worker.Tick(ctx, gone);
            if (s == GameTaskStatus.Running)
            {
                Progress = _worker.MoveProgress is { } mp ? $"mined {_index}/{_tiles.Count} (walking: {mp})" : $"mined {_index}/{_tiles.Count}";
                return GameTaskStatus.Running;
            }
            if (s == GameTaskStatus.Failed) return Finish(ctx, GameTaskStatus.Failed, _worker.Reason);
            Next(ctx); // succeeded: tile gone; move on in the same tick
        }
        Progress = $"mined {_tiles.Count}/{_tiles.Count}";
        return Finish(ctx, GameTaskStatus.Succeeded, null);
    }

    public void OnCancel(GameCtx ctx) => Cleanup(ctx);

    private void Next(GameCtx ctx)
    {
        if (_index >= 0 && _index < _tiles.Count)
        {
            _pending.Remove(_tiles[_index]);
            _mined++;
        }
        _index++;
        if (_index < _tiles.Count) _worker.Begin(ctx, _tiles[_index], _pending);
    }

    private GameTaskStatus Finish(GameCtx ctx, GameTaskStatus status, string? reason)
    {
        Reason = reason;
        Cleanup(ctx);
        return status;
    }

    private void Cleanup(GameCtx ctx)
    {
        _worker.Cancel(ctx);
        _lease.Restore(ctx.Player);
    }

    /// <summary>Is there something a pickaxe can break at (x,y)? (A tile that isn't a tree/axe or hammer-only tile.)</summary>
    public static bool HasMinableTile(int x, int y)
    {
        if (!WorldGen.InWorld(x, y)) return false;
        var tile = Main.tile[x, y];
        return tile.HasTile && !Main.tileAxe[tile.TileType] && !Main.tileHammer[tile.TileType];
    }

    /// <summary>
    /// Check tiles before mining. Single tile (<paramref name="area"/> false): throws <see cref="CommandError"/> for the
    /// first offender: outside the world or air → bad_params; not visible → not_visible; protected (or holding up an
    /// object vanilla won't let break) → protected_tile; tree or hammer-only tile → bad_params; too hard for the best
    /// pickaxe (or no pickaxe) → missing_item with the pick power needed.
    /// Area: every such tile is skipped instead and recorded in <paramref name="skipped"/> with its reason ("air",
    /// "not visible", "protected", "locked" (holds up an object), "tree" / "hammer-only", "too hard (needs pick power N)").
    /// Outside the world and having no pickaxe at all still throw. Returns the tiles to mine, in the given order.
    /// </summary>
    public static List<TilePoint> Validate(Player p, IEnumerable<TilePoint> tiles, bool area, out SkipReport skipped)
    {
        skipped = new SkipReport("air");
        var result = new List<TilePoint>();
        var grid = new GameTileGrid(p);
        int power = HeldItemLease.PickPower(p);
        if (area && power <= 0) throw new CommandError(ErrorCodes.MissingItem, "no pickaxe in inventory");
        foreach (var t in tiles)
        {
            if (!WorldGen.InWorld(t.X, t.Y, 10))
                throw new CommandError(ErrorCodes.BadParams, $"{t} is outside the world");
            if (!Visibility.IsTileVisible(t.X, t.Y))
            {
                if (area) { skipped.Add(t, "not visible"); continue; }
                throw new CommandError(ErrorCodes.NotVisible, $"{t} is not visible (unexplored or dark)");
            }

            var tile = Main.tile[t.X, t.Y];
            if (!tile.HasTile)
            {
                if (area) { skipped.Add(t, "air"); continue; }
                throw new CommandError(ErrorCodes.BadParams, $"nothing to mine at {t} (air)");
            }
            string name = TileClass.Name(tile.TileType);
            if (Protection.IsProtected(t.X, t.Y))
            {
                if (area) { skipped.Add(t, "protected"); continue; }
                throw new CommandError(ErrorCodes.ProtectedTile, $"{t} ({name}) is protected (furniture, station, door, NPC house or near spawn)");
            }
            if (Main.tileAxe[tile.TileType] || Main.tileHammer[tile.TileType])
            {
                if (area) { skipped.Add(t, Main.tileAxe[tile.TileType] ? "tree" : "hammer-only"); continue; }
                throw new CommandError(ErrorCodes.BadParams,
                    Main.tileAxe[tile.TileType] ? $"{t} ({name}) needs an axe: use chop_tree" : $"{t} ({name}) needs a hammer, not a pickaxe");
            }
            if (!WorldGen.CanKillTile(t.X, t.Y))
            {
                if (area) { skipped.Add(t, "locked (holds up an object)"); continue; }
                throw new CommandError(ErrorCodes.ProtectedTile, $"{t} ({name}) can't be broken right now (it holds up an object or is otherwise locked)");
            }
            int need = grid.Get(t.X, t.Y).PickPowerRequired;
            if (need > power)
            {
                string what = need == int.MaxValue ? "can't be mined with a pickaxe" : $"needs pick power {need}";
                if (area) { skipped.Add(t, need == int.MaxValue ? "unminable" : $"too hard ({what})"); continue; }
                string has = power > 0 ? $"best pickaxe has {power}" : "no pickaxe in inventory";
                throw new CommandError(ErrorCodes.MissingItem, $"{t} ({name}) {what} ({has})");
            }
            if (power <= 0) throw new CommandError(ErrorCodes.MissingItem, "no pickaxe in inventory");
            result.Add(t);
        }
        return result;
    }
}
