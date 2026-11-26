using LlmPlayer.Control;
using LlmPlayer.Core.Combat;
using LlmPlayer.Core.Nav;
using LlmPlayer.Core.Tasks;
using LlmPlayer.Observe;
using Terraria;

namespace LlmPlayer.Tasks;

/// <summary>
/// Fight one visible hostile NPC until it dies. The command validates the target (never town NPCs or critters);
/// the task re-checks it every tick.
/// <list type="bullet">
/// <item>Weapon: highest expected DPS usable now (<see cref="CombatKit.BestWeapon"/>), re-chosen every second or when
/// it runs out of ammo/mana. Held through a <see cref="HeldItemLease"/>; the previous item comes back at the end.</item>
/// <item>Position (<see cref="CombatStance"/>): melee holds within reach, walks straight at a close level target and
/// otherwise pathfinds (<see cref="MoveToTask"/> following the entity, no dig/build). Ranged/magic keeps 8–15 tiles:
/// pathfinds closer when farther or without line of sight, steps back when nearer (only onto safe ground).</item>
/// <item>Aim: the target's centre, led by its velocity for projectiles. Use is pressed while the target is in
/// reach / in range with line of sight, also while walking a plain path segment.</item>
/// <item>Succeeds when the target dies; fails if it despawns, stays out of sight for 5 s, no weapon is usable, or
/// the path toward it fails 3 times in a row.</item>
/// </list>
/// </summary>
public sealed class AttackTask : IGameTask<GameCtx>
{
    public const int LostSightTicks = 300, WeaponRecheckTicks = 60, MaxMoveFailures = 3;
    private const int RangedPathTolerance = 10;

    private readonly int _id, _type;
    private readonly string _targetName;
    private readonly uint _startTick;
    private readonly HeldItemLease _lease = new();

    private MoveToTask? _move;
    private int _moveFailures;
    private uint _lastSeen;
    private Weapon? _weapon;
    private int _weaponAge;
    private bool _lastUse, _lastJump;

    public string Name => "attack";
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }

    public AttackTask(NPC target)
    {
        _id = target.whoAmI;
        _type = target.type;
        _targetName = target.GivenOrTypeName;
        _startTick = _lastSeen = Main.GameUpdateCount;
    }

    public GameTaskStatus Tick(GameCtx ctx)
    {
        var p = ctx.Player;
        var npc = Main.npc[_id];
        if (!npc.active || npc.type != _type)
        {
            return CombatKit.WasKilled(_id, _type, _startTick)
                ? Finish(ctx, GameTaskStatus.Succeeded, $"killed {_targetName}")
                : Finish(ctx, GameTaskStatus.Failed, $"{_targetName} despawned");
        }
        if (!TargetRules.IsAttackable(CombatKit.Traits(npc)))
            return Finish(ctx, GameTaskStatus.Failed, $"{_targetName} is no longer a valid target");

        if (!Visibility.IsEntityVisible(npc))
        {
            StopMove(ctx);
            ControlState.Pending = default;
            if (Main.GameUpdateCount - _lastSeen > LostSightTicks)
                return Finish(ctx, GameTaskStatus.Failed, $"{_targetName} not visible for 5 s");
            Progress = $"{_targetName} out of sight";
            return GameTaskStatus.Running;
        }
        _lastSeen = Main.GameUpdateCount;

        if (_weapon == null || --_weaponAge <= 0 || !CombatKit.StillUsable(p, _weapon))
        {
            _weapon = CombatKit.BestWeapon(p);
            _weaponAge = WeaponRecheckTicks;
        }
        if (_weapon is not { } w) return Finish(ctx, GameTaskStatus.Failed, "no usable weapon (out of ammo or mana?)");

        var off = CombatKit.Offset(p, npc);
        float dist = off.Length();
        bool los = CombatKit.LineOfSight(p, npc);
        var stance = CombatStance.Decide(w.Style, off.X, off.Y, w.Reach, los);
        bool fire = CombatStance.ShouldFire(w.Style, dist, w.Reach, los);
        Progress = $"{_targetName} {npc.life}/{npc.lifeMax} life, {dist:0} tiles, {w.Item.Name}";

        ControlIntent intent = default;
        if (stance == StanceMove.PathToward)
        {
            int tolerance = w.Style == WeaponStyle.Melee || !los ? 1 : RangedPathTolerance;
            _move ??= new MoveToTask(_id, allowDig: false, allowBuild: false, tolerance: tolerance, name: "attack");
            var st = _move.Tick(ctx);
            if (st == GameTaskStatus.Running) intent = ControlState.Pending;
            else
            {
                string? why = _move.Reason;
                _move = null;
                if (st == GameTaskStatus.Failed)
                {
                    if (++_moveFailures >= MaxMoveFailures)
                        return Finish(ctx, GameTaskStatus.Failed, $"can't reach {_targetName}: {why}");
                }
                else _moveFailures = 0;
            }
            // Only aim/shoot on plain walking segments (the sub-move never digs or builds, but stay defensive).
            if (intent.UseItem || intent.CursorTile != null)
            {
                ControlState.Pending = intent;
                return GameTaskStatus.Running;
            }
        }
        else
        {
            StopMove(ctx);
            int toward = Math.Sign(off.X);
            if (stance == StanceMove.StepToward)
                intent = CombatKit.Step(intent, p, ctx.Grid, toward, wantUp: off.Y < -1.5f, ref _lastJump);
            else if (stance == StanceMove.StepAway)
                intent = CombatKit.Step(intent, p, ctx.Grid, toward != 0 ? -toward : -p.direction, wantUp: false, ref _lastJump);
            else _lastJump = false;
        }

        bool held = _lease.Hold(p, w.Slot);
        intent = CombatKit.Aim(intent, p, w, npc, fire && held, ref _lastUse);
        ControlState.Pending = intent;
        return GameTaskStatus.Running;
    }

    public void OnCancel(GameCtx ctx) => Cleanup(ctx);

    private void StopMove(GameCtx ctx)
    {
        if (_move == null) return;
        _move.OnCancel(ctx);
        _move = null;
    }

    private GameTaskStatus Finish(GameCtx ctx, GameTaskStatus status, string reason)
    {
        Reason = reason;
        Cleanup(ctx);
        return status;
    }

    private void Cleanup(GameCtx ctx)
    {
        StopMove(ctx);
        ControlState.Pending = default;
        _lease.Restore(ctx.Player);
    }
}
