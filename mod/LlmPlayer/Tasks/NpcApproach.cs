using LlmPlayer.Control;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Npc;
using LlmPlayer.Core.Tasks;
using LlmPlayer.Observe;
using Terraria;

namespace LlmPlayer.Tasks;

/// <summary>
/// The walk of talk_to_npc and buy: get within vanilla talk range of a town NPC. Plans to the nearest reachable stand
/// from which the NPC is in range (<see cref="TalkStands"/>, through walls as vanilla allows) rather than to the NPC
/// itself, so an NPC inside a house the pathfinder can't enter is still talked to from outside. While the NPC wanders,
/// the stands are recomputed and the walk retargeted (<see cref="MoveToTask.Retarget"/>) every
/// <see cref="RetargetTicks"/> ticks. Fails when no known stand is in range, the walk fails (with a hint), the NPC is out
/// of sight, or it is still out of range after <see cref="MaxWalks"/> walks.
/// </summary>
internal sealed class NpcApproach
{
    public const int MaxWalks = 6, RetargetTicks = 60;

    private readonly int _npc;
    private readonly string _npcName, _taskName;
    private MoveToTask? _move;
    private int _walks, _sinceRetarget;
    private TilePoint _plannedFor;

    public string? Progress { get; private set; }

    public NpcApproach(NPC npc, string taskName)
    {
        _npc = npc.whoAmI;
        _npcName = npc.GivenOrTypeName;
        _taskName = taskName;
    }

    public static bool InRange(Player p, NPC npc) =>
        NpcMath.InTalkRange(p.position.X, p.position.Y, p.width, p.height, Player.tileRangeX, Player.tileRangeY,
            (int)npc.position.X, (int)npc.position.Y, npc.width, npc.height);

    /// <summary>One tick of walking (call only while out of range). Running, or Failed with <paramref name="reason"/>;
    /// Succeeded means a walk ended at a stand: check the range again next tick.</summary>
    public GameTaskStatus Tick(GameCtx ctx, out string? reason)
    {
        reason = null;
        var npc = Main.npc[_npc];
        if (!Visibility.IsEntityVisible(npc))
        {
            reason = $"{_npcName} is no longer visible";
            return GameTaskStatus.Failed;
        }
        var (nx, ny) = EntityReader.TileOf(npc);
        var npcTile = new TilePoint(nx, ny);

        if (_move == null)
        {
            if (_walks >= MaxWalks)
            {
                reason = $"could not get within talk range of {_npcName} (it keeps moving)";
                return GameTaskStatus.Failed;
            }
            var stands = Stands(ctx, npc);
            if (stands.Count == 0)
            {
                reason = $"no known spot to stand within talk range of {_npcName} at {npcTile} (talk range: {Player.tileRangeX} tiles across, " +
                         $"{Player.tileRangeY} up/down from your centre): explore around it, wait for it to come outside, or build a platform nearby";
                return GameTaskStatus.Failed;
            }
            _walks++;
            _move = new MoveToTask(npcTile, tolerance: 0, name: _taskName) { GoalSet = stands };
            _plannedFor = npcTile;
            _sinceRetarget = 0;
        }
        else if (++_sinceRetarget >= RetargetTicks && Chebyshev(npcTile, _plannedFor) > 1)
        {
            var stands = Stands(ctx, npc);
            if (stands.Count > 0)
            {
                _move.Retarget(npcTile, stands);
                _plannedFor = npcTile;
            }
            _sinceRetarget = 0;
        }

        var s = _move.Tick(ctx);
        Progress = $"walking to {_npcName}" + (_move.Progress is { } mp ? $": {mp}" : "");
        if (s == GameTaskStatus.Running) return GameTaskStatus.Running;
        var why = _move.Reason;
        _move = null; // finished cleanly on its own
        ControlState.Pending = default;
        if (s == GameTaskStatus.Succeeded) return GameTaskStatus.Succeeded;
        reason = $"no reachable spot within talk range of {_npcName} at {npcTile} ({why ?? "walk failed"}). " +
                 "Talking works through walls, but every stand within range is cut off: if it is shut in its house, wait for it " +
                 "to come outside (town NPCs wander by day), or open a way (door, platforms/blocks to stand on within " +
                 $"{Player.tileRangeX} tiles across and {Player.tileRangeY} up/down of it)";
        return GameTaskStatus.Failed;
    }

    public void Cancel(GameCtx ctx)
    {
        _move?.OnCancel(ctx);
        _move = null;
    }

    private static HashSet<TilePoint> Stands(GameCtx ctx, NPC npc) =>
        TalkStands.Find(ctx.Grid, (int)npc.position.X, (int)npc.position.Y, npc.width, npc.height, Player.tileRangeX, Player.tileRangeY);

    private static int Chebyshev(TilePoint a, TilePoint b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
