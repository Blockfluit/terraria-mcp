using LlmPlayer.Control;
using LlmPlayer.Core.Combat;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using LlmPlayer.Core.Tasks;
using LlmPlayer.Nav;
using LlmPlayer.Observe;
using Terraria;
using Terraria.ID;

namespace LlmPlayer.Tasks;

/// <summary>
/// Fight one visible hostile NPC until it dies, or (<c>all</c>) every visible hostile within a radius, nearest first,
/// until none remain. The command validates the first target (never town NPCs or critters); the task re-checks
/// every target every tick.
/// <list type="bullet">
/// <item>Weapon: highest expected DPS usable now (<see cref="CombatKit.BestWeapon"/>), re-chosen every second or when
/// it runs out of ammo/mana. Held through a <see cref="HeldItemLease"/>; the previous item comes back at the end.</item>
/// <item>Position (<see cref="CombatStance"/>): melee holds within reach, walks straight at a close level target and
/// otherwise pathfinds (<see cref="MoveToTask"/> following the entity, no dig/build). Ranged/magic keeps 8–15 tiles.</item>
/// <item>No trading hits (<see cref="Evasion"/>): when the planned movement would touch the target within a fraction
/// of a second (its hitbox and velocity, plus gravity), it backs off or jumps instead, never into an unsafe column,
/// and keeps swinging/shooting meanwhile. Melee therefore closes in, swings and backs off as the enemy steps in.</item>
/// <item>Aim: the target's centre, led by its velocity for projectiles.</item>
/// <item>Retreat: below <c>retreatBelow</c> × max life with no healing available (no potion or Potion Sickness; the
/// quick-heal reflex handles the rest), it stops attacking and runs away from the visible hostiles for up to 3 s
/// (or until none is within 15 tiles), then fails with "low life".</item>
/// <item>Single target: succeeds when it dies; fails if it despawns, stays out of sight for 5 s, no weapon is usable,
/// or the path toward it fails 3 times in a row. <c>all</c>: such a target is skipped instead; succeeds when no
/// visible hostile is left in the radius.</item>
/// <item>Result (<see cref="AttackResult"/>): kills, life lost, duration, dodges, whether it retreated.</item>
/// </list>
/// </summary>
public sealed class AttackTask : IGameTask<GameCtx>
{
    public const int LostSightTicks = 300, AllLostSightTicks = 60, WeaponRecheckTicks = 60, MaxMoveFailures = 3;
    public const int RetreatMaxTicks = 180;
    public const float RetreatClearTiles = 15f, NpcGravityPx = 0.3f;
    private const int RangedPathTolerance = 10;

    private readonly bool _all;
    private readonly float _radius, _retreatBelow;
    private readonly HeldItemLease _lease = new();
    private readonly AttackStats _stats;
    private readonly Evasion _evasion;
    private readonly HashSet<int> _skipped = new();

    private int _id, _type;
    private string _targetName = "";
    private uint _targetSince, _lastSeen;
    private MoveToTask? _move;
    private int _moveFailures;
    private Weapon? _weapon;
    private int _weaponAge;
    private bool _lastUse, _lastJump;
    private int _retreatTicks = -1;

    public string Name => "attack";
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }
    public object? Result { get; private set; }

    /// <param name="all">After this target, keep fighting the nearest visible hostile within <paramref name="radius"/>
    /// until none remain.</param>
    /// <param name="retreatBelow">Life fraction to retreat below when no healing is available (0 = never).</param>
    public AttackTask(NPC target, bool all = false, float radius = 25f, float retreatBelow = 0.3f)
    {
        _all = all;
        _radius = radius;
        _retreatBelow = retreatBelow;
        _stats = new AttackStats(Main.GameUpdateCount);
        _evasion = new Evasion(ProfileBuilder.Physics(Main.LocalPlayer));
        SetTarget(target);
    }

    public GameTaskStatus Tick(GameCtx ctx)
    {
        var p = ctx.Player;
        _stats.ObserveLife(p.statLife);
        Result = _stats.Result(Main.GameUpdateCount);

        if (_retreatTicks >= 0) return TickRetreat(ctx);
        bool canHeal = p.QuickHeal_GetItemToUse() != null && !p.HasBuff(BuffID.PotionSickness) && p.potionDelay <= 0;
        if (Retreat.ShouldRetreat(p.statLife, p.statLifeMax2, _retreatBelow, canHeal))
        {
            StopMove(ctx);
            _evasion.Reset();
            _retreatTicks = 0;
            _stats.Retreated = true;
            return TickRetreat(ctx);
        }

        var npc = Main.npc[_id];
        if (!npc.active || npc.type != _type)
        {
            bool killed = CombatKit.WasKilled(_id, _type, _targetSince);
            if (killed) _stats.AddKill(_targetName);
            if (!_all)
                return killed
                    ? Finish(ctx, GameTaskStatus.Succeeded, $"killed {_targetName}")
                    : Finish(ctx, GameTaskStatus.Failed, $"{_targetName} despawned");
            return NextTarget(ctx);
        }
        if (!TargetRules.IsAttackable(CombatKit.Traits(npc)))
            return _all ? Skip(ctx) : Finish(ctx, GameTaskStatus.Failed, $"{_targetName} is no longer a valid target");

        if (!Visibility.IsEntityVisible(npc))
        {
            StopMove(ctx);
            ControlState.Pending = default;
            uint lost = Main.GameUpdateCount - _lastSeen;
            if (_all && lost > AllLostSightTicks) return Skip(ctx);
            if (lost > LostSightTicks) return Finish(ctx, GameTaskStatus.Failed, $"{_targetName} not visible for 5 s");
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

        // Evasion: would the planned movement touch the target soon? Then back off / jump instead.
        var k = NavMath.FromPixels(p.position.X, p.position.Y, p.velocity.X, p.velocity.Y, p.height);
        var (sx, sy) = StateReader.StandingTile(p);
        var stand = new TilePoint(sx, sy);
        var dodge = _evasion.Tick(new EvasionInputs(k, Motion(npc), w.Style, Evasion.PlanDir(stance, off.X, p.direction), false,
            ContactPredictor.FloorBelow(ctx.Grid, k), StepSafety.IsSafe(ctx.Grid, stand, -1), StepSafety.IsSafe(ctx.Grid, stand, 1)));
        if (dodge.Started) _stats.AddDodge();

        Progress = $"{_targetName} {npc.life}/{npc.lifeMax} life, {dist:0} tiles, {w.Item.Name}"
            + (dodge.Evading ? ", dodging" : "")
            + (_all ? $"; killed {_stats.Kills.Count}" : "") + $", dodges {_stats.Dodges}";

        ControlIntent intent = default;
        if (dodge.Evading)
        {
            StopMove(ctx);
            if (dodge.Dir != 0) intent = CombatKit.Step(intent, p, ctx.Grid, dodge.Dir, wantUp: dodge.Jump, ref _lastJump);
            else intent.Jump = JumpPulse.Next(dodge.Jump, k.OnGround, p.velocity.Y, ref _lastJump);
        }
        else if (stance == StanceMove.PathToward)
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
                        return _all ? Skip(ctx) : Finish(ctx, GameTaskStatus.Failed, $"can't reach {_targetName}: {why}");
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

    /// <summary>The target's hitbox and motion for <see cref="ContactPredictor"/>, in tiles.</summary>
    private static EnemyMotion Motion(NPC n) =>
        new(n.Center.X / 16f, n.Center.Y / 16f, n.width / 32f, n.height / 32f, n.velocity.X / 16f, n.velocity.Y / 16f,
            n.noGravity ? 0f : NpcGravityPx / 16f);

    /// <summary>Run away from the visible hostiles (no attacking), then fail with "low life".</summary>
    private GameTaskStatus TickRetreat(GameCtx ctx)
    {
        var p = ctx.Player;
        var c = p.Center / 16f;
        var dxs = new List<float>();
        float nearest = float.MaxValue;
        foreach (var n in CombatKit.VisibleNpcs(p))
        {
            if (!TargetRules.IsAttackable(n)) continue;
            float dx = n.X - c.X, dy = n.Y - c.Y;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            if (d > 30f) continue;
            dxs.Add(dx);
            nearest = Math.Min(nearest, d);
        }
        string life = $"life {p.statLife}/{p.statLifeMax2}";
        Progress = $"retreating ({life}, no healing available)";
        if (++_retreatTicks >= RetreatMaxTicks || nearest >= RetreatClearTiles)
            return Finish(ctx, GameTaskStatus.Failed, $"low life: retreated ({life}, no healing available)");

        var (sx, sy) = StateReader.StandingTile(p);
        var stand = new TilePoint(sx, sy);
        int dir = Retreat.Direction(dxs, StepSafety.IsSafe(ctx.Grid, stand, -1), StepSafety.IsSafe(ctx.Grid, stand, 1));
        ControlIntent intent = default;
        intent = CombatKit.Step(intent, p, ctx.Grid, dir, wantUp: false, ref _lastJump);
        ControlState.Pending = intent;
        return GameTaskStatus.Running;
    }

    private void SetTarget(NPC npc)
    {
        _id = npc.whoAmI;
        _type = npc.type;
        _targetName = npc.GivenOrTypeName;
        _targetSince = _lastSeen = Main.GameUpdateCount;
        _moveFailures = 0;
        _evasion.Reset();
    }

    /// <summary>Give up on the current target (all mode) and move on.</summary>
    private GameTaskStatus Skip(GameCtx ctx)
    {
        _skipped.Add(_id);
        return NextTarget(ctx);
    }

    private GameTaskStatus NextTarget(GameCtx ctx)
    {
        StopMove(ctx);
        ControlState.Pending = default;
        var p = ctx.Player;
        var c = p.Center / 16f;
        var candidates = CombatKit.VisibleNpcs(p).Where(n => !_skipped.Contains(n.Id));
        if (TargetRules.PickNearest(candidates, c.X, c.Y, _radius) is not { } next)
        {
            int n = _stats.Kills.Count;
            string skipped = _skipped.Count > 0 ? $", skipped {_skipped.Count} out of reach/sight" : "";
            return Finish(ctx, GameTaskStatus.Succeeded, $"no hostiles left within {_radius:0} tiles: killed {n}{skipped}");
        }
        SetTarget(Main.npc[next.Id]);
        return GameTaskStatus.Running;
    }

    private void StopMove(GameCtx ctx)
    {
        if (_move == null) return;
        _move.OnCancel(ctx);
        _move = null;
    }

    private GameTaskStatus Finish(GameCtx ctx, GameTaskStatus status, string reason)
    {
        Reason = reason;
        Result = _stats.Result(Main.GameUpdateCount);
        Cleanup(ctx);
        return status;
    }

    private void Cleanup(GameCtx ctx)
    {
        StopMove(ctx);
        ControlState.Pending = default;
        _lease.Restore(ctx.Player);
        Result = _stats.Result(Main.GameUpdateCount);
    }
}
