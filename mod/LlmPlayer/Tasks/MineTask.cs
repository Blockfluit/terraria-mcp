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
/// comes are counted as done. Fails on the first tile it can't reach or break, or if a tile becomes protected.
/// Progress: "mined 3/9" (+ "walking: N tiles remaining"). Validate with <see cref="Validate"/> before starting.
/// </summary>
public sealed class MineTask : IGameTask<GameCtx>
{
    private readonly IReadOnlyList<TilePoint> _tiles;
    private readonly HashSet<TilePoint> _pending;
    private readonly HeldItemLease _lease = new();
    private readonly TileWorker _worker;
    private int _index = -1;

    public string Name { get; }
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }

    public MineTask(IReadOnlyList<TilePoint> tiles, string name = "mine")
    {
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
                return Finish(ctx, GameTaskStatus.Failed, $"{t} became protected; stopped");

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
        if (_index >= 0 && _index < _tiles.Count) _pending.Remove(_tiles[_index]);
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
    /// Check tiles before mining; throws <see cref="CommandError"/> for the first offender (in the given order):
    /// outside the world → bad_params; not visible → not_visible; protected (or holding up an object vanilla won't let
    /// break) → protected_tile; too hard for the best pickaxe → missing_item with the pick power needed.
    /// Air: skipped when <paramref name="area"/>, else bad_params. Trees and hammer-only tiles: same (a pickaxe can't
    /// break them). Returns the tiles to mine and how many were skipped.
    /// </summary>
    public static List<TilePoint> Validate(Player p, IEnumerable<TilePoint> tiles, bool area, out int skipped)
    {
        skipped = 0;
        var result = new List<TilePoint>();
        var grid = new GameTileGrid(p);
        int power = HeldItemLease.PickPower(p);
        foreach (var t in tiles)
        {
            if (!WorldGen.InWorld(t.X, t.Y, 10))
                throw new CommandError(ErrorCodes.BadParams, $"{t} is outside the world");
            if (!Visibility.IsTileVisible(t.X, t.Y))
                throw new CommandError(ErrorCodes.NotVisible, $"{t} is not visible (unexplored or dark)");

            var tile = Main.tile[t.X, t.Y];
            if (!tile.HasTile)
            {
                if (area) { skipped++; continue; }
                throw new CommandError(ErrorCodes.BadParams, $"nothing to mine at {t} (air)");
            }
            string name = TileClass.Name(tile.TileType);
            if (Protection.IsProtected(t.X, t.Y))
                throw new CommandError(ErrorCodes.ProtectedTile, $"{t} ({name}) is protected (furniture, station, door, NPC house or near spawn)");
            if (Main.tileAxe[tile.TileType] || Main.tileHammer[tile.TileType])
            {
                if (area) { skipped++; continue; }
                throw new CommandError(ErrorCodes.BadParams,
                    Main.tileAxe[tile.TileType] ? $"{t} ({name}) needs an axe: use chop_tree" : $"{t} ({name}) needs a hammer, not a pickaxe");
            }
            if (!WorldGen.CanKillTile(t.X, t.Y))
                throw new CommandError(ErrorCodes.ProtectedTile, $"{t} ({name}) can't be broken right now (it holds up an object or is otherwise locked)");
            int need = grid.Get(t.X, t.Y).PickPowerRequired;
            if (need > power)
            {
                string has = power > 0 ? $"best pickaxe has {power}" : "no pickaxe in inventory";
                string what = need == int.MaxValue ? "can't be mined with a pickaxe" : $"needs pick power {need}";
                throw new CommandError(ErrorCodes.MissingItem, $"{t} ({name}) {what} ({has})");
            }
            if (power <= 0) throw new CommandError(ErrorCodes.MissingItem, "no pickaxe in inventory");
            result.Add(t);
        }
        return result;
    }
}
