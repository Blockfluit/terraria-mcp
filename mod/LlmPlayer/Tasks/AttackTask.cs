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
/// <item>Weapon and ammo (<see cref="WeaponLoadout"/>): the LLM's choice, used exactly; a fallback (with an event) only
/// when it becomes unusable, or a stop when fallback is off. Without a choice, the highest expected DPS usable now
/// (<see cref="CombatKit.BestWeapon"/>), re-chosen every second. Held through a <see cref="HeldItemLease"/>; the
/// previous item comes back at the end, and moved ammo stacks go back too.</item>
/// <item>Position (<see cref="Strategies"/>, the LLM's <c>strategy</c>): auto = melee holds within reach, walks
/// straight at a close level target and otherwise pathfinds (<see cref="MoveToTask"/> following the entity, no
/// dig/build), ranged/magic keeps 8–15 tiles; kite keeps keep_distance and keeps running (<see cref="Kiter"/>);
/// hit_and_run backs off after each swing (<see cref="HitAndRunRhythm"/>); hold never moves except to dodge;
/// flee_and_shoot runs away while shooting.</item>
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
/// <item>Result (<see cref="AttackResult"/>): kills, life lost, duration, dodges, whether it retreated, potions used,
/// ammo left; for a boss also its name, whether it died and (if not) its life left in percent.</item>
/// <item>Boss fight (the target is a boss, <see cref="EntityReader.IsBoss"/>): see <see cref="TickBoss"/>.</item>
/// <item>heal_below: raises the quick-heal reflex's threshold while the task runs
/// (<see cref="Reflexes.SetHealOverride"/>); quick-heal fires during attack like at any other time.</item>
/// </list>
/// </summary>
public sealed class AttackTask : IGameTask<GameCtx>
{
    public const int LostSightTicks = 300, AllLostSightTicks = 60, MaxMoveFailures = 3;
    public const int RetreatMaxTicks = 180;
    public const float RetreatClearTiles = 15f, NpcGravityPx = 0.3f;
    private const int RangedPathTolerance = 10;

    private readonly bool _all;
    private readonly float _radius, _retreatBelow;
    private readonly float? _healBelow;
    private readonly int _potionsAtStart;
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
    private readonly WeaponLoadout _loadout;
    private readonly float _keep;
    private readonly Kiter _kiter = new();
    private readonly HitAndRunRhythm _rhythm = new();
    private int _lastAnim;
    private float _lastBossDist = float.MaxValue;
    private bool _lastUse, _lastJump;
    private int _retreatTicks = -1;

    // Boss fight state.
    private bool _boss;
    private ArenaBounds? _arena;
    private readonly BossMovement _bossMove;
    private readonly MinionFocus _focus;
    private (int Id, int Type, uint Since)? _minion;
    private int _dropTicks;

    public string Name => "attack";
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }
    public object? Result { get; private set; }

    /// <param name="all">After this target, keep fighting the nearest visible hostile within <paramref name="radius"/>
    /// until none remain.</param>
    /// <param name="retreatBelow">Life fraction to retreat below when no healing is available (0 = never).</param>
    /// <param name="arena">Boss fights: the columns to stay within (default: the walkable span around the player).</param>
    /// <param name="killMinions">Boss fights: shoot minions that are about to reach the player first. Off (default):
    /// the boss first, a minion only when it is right next to the player (<see cref="MinionFocus.AdjacentOnly"/>).</param>
    /// <param name="healBelow">Quick-heal below this life fraction while the task runs (if above the config's).</param>
    /// <param name="loadout">Weapon/ammo/strategy the LLM chose (default: auto weapon, vanilla ammo order, auto).</param>
    /// <param name="keepDistance">kite / flee_and_shoot distance in tiles (default 12).</param>
    public AttackTask(NPC target, bool all = false, float radius = 25f, float retreatBelow = 0.3f,
        ArenaBounds? arena = null, bool killMinions = false, float? healBelow = null, WeaponLoadout? loadout = null,
        float? keepDistance = null)
    {
        _loadout = loadout ?? new WeaponLoadout(null, null, true, AttackStrategy.Auto);
        _keep = Strategies.Keep(keepDistance);
        _all = all;
        _radius = radius;
        _retreatBelow = retreatBelow;
        _arena = arena;
        _focus = new MinionFocus(adjacentOnly: !killMinions);
        _healBelow = healBelow;
        _potionsAtStart = Reflexes.PotionsUsed;
        _stats = new AttackStats(Main.GameUpdateCount);
        var phys = ProfileBuilder.Physics(Main.LocalPlayer);
        _evasion = new Evasion(phys);
        _bossMove = new BossMovement(phys);
        SetTarget(target);
    }

    public GameTaskStatus Tick(GameCtx ctx)
    {
        var p = ctx.Player;
        if (_healBelow is { } hb) Reflexes.SetHealOverride(this, hb);
        _stats.ObserveLife(p.statLife);
        _stats.PotionsUsed = Reflexes.PotionsUsed - _potionsAtStart;
        if (_weapon != null) _stats.AmmoLeft = CombatKit.AmmoCount(p, _weapon.Item);
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

        if (_boss) return TickBoss(ctx);

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

        _weapon = _loadout.Tick(p, npc.defense);
        if (_weapon is not { } w) return Finish(ctx, GameTaskStatus.Failed, _loadout.StopReason ?? "no usable weapon (out of ammo or mana?)");
        var strategy = Strategies.Effective(_loadout.Strategy, w.Style);
        UpdateStats(p, w, strategy);

        var off = CombatKit.Offset(p, npc);
        float dist = off.Length();
        bool los = CombatKit.LineOfSight(p, npc);
        bool backOff = SwingRhythm(p, strategy, dist, w.Reach);
        var stance = Strategies.Decide(strategy, w.Style, off.X, off.Y, w.Reach, los, _keep, backOff);
        bool fire = Strategies.ShouldFire(strategy, w.Style, dist, w.Reach, los, _keep);

        // Evasion: would the planned movement touch the target soon? Then back off / jump instead.
        var k = NavMath.FromPixels(p.position.X, p.position.Y, p.velocity.X, p.velocity.Y, p.height);
        var (sx, sy) = StateReader.StandingTile(p);
        var stand = new TilePoint(sx, sy);
        bool safeL = StepSafety.IsSafe(ctx.Grid, stand, -1), safeR = StepSafety.IsSafe(ctx.Grid, stand, 1);
        int planDir = Evasion.PlanDir(stance, off.X, p.direction);
        // Kite: inside the band keep running along the ground (turning at walls, drops and the arena's ends).
        bool kiting = stance == StanceMove.Hold && Strategies.KeepsMoving(strategy);
        if (kiting) planDir = _kiter.Plan(sx, _arena, off.X, safeL && (_arena?.Allows(sx, -1) ?? true), safeR && (_arena?.Allows(sx, 1) ?? true));
        var dodge = _evasion.Tick(new EvasionInputs(k, Motion(npc), w.Style, planDir, false,
            ContactPredictor.FloorBelow(ctx.Grid, k), safeL, safeR));
        if (dodge.Started) _stats.AddDodge();

        Progress = $"{_targetName} {npc.life}/{npc.lifeMax} life, {dist:0} tiles, {_loadout.Label(p)}, {Strategies.Name(strategy)}"
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
            else if (kiting && planDir != 0)
                intent = CombatKit.Step(intent, p, ctx.Grid, planDir, wantUp: false, ref _lastJump);
            else _lastJump = false;
        }

        bool held = _lease.Hold(p, w.Slot);
        intent = CombatKit.Aim(intent, p, w, npc, fire && held, ref _lastUse);
        ControlState.Pending = intent;
        return GameTaskStatus.Running;
    }

    /// <summary>
    /// Boss fight (<see cref="BossMovement"/>): never pathfinds and never leaves the arena (<c>arena</c>, default the
    /// walkable span of ground/platforms around the player within ±50 tiles, measured once the player stands).
    /// <list type="bullet">
    /// <item>Ranged: kites across the arena (turning near its ends), shooting with lead aim whenever the boss is in
    /// sight; melee: steps toward it, hit-and-run. Evasion against the boss and nearby minions overrides the plan:
    /// sideways, a jump, or a drop through the platform underfoot (Down for a few ticks).</item>
    /// <item>Minions: the boss is the target; only a minion right next to the player is shot first. With
    /// <c>kill_minions</c> any minion about to reach the player (<see cref="MinionFocus"/>) is, then back to the boss.</item>
    /// <item>A boss that hops (King Slime; <see cref="JumperDodge"/>): its landing is predicted from its jump, and the
    /// player runs out from under it or under it to the other side; on the ground it keeps a stance from which the
    /// next jump (its learnt rhythm) can be dodged. Progress says when the player is cornered at an arena end.</item>
    /// <item>The boss stays the target when it leaves the screen, but fairly: the player can't see where it is, so
    /// movement uses the last sighting carried along its last velocity for a second, then held there
    /// (<see cref="LastSeen"/>); shooting waits until it's visible again. Its life (the vanilla boss bar) and whether it
    /// died or fled (the game announces both) stay known. Succeeds when it dies; fails with "boss fled" when it
    /// despawns without dying (dawn, or the player went too far).</item>
    /// <item>Strategy: auto/kite/flee_and_shoot kite across the arena (kite also runs away inside keep_distance,
    /// flee_and_shoot always runs away when it can), hit_and_run steps in and backs off after each swing, hold stays put
    /// (and doesn't climb); <see cref="Strategies.BossPlan"/>. Evasion overrides every plan.</item>
    /// <item>Ammo: the chosen ammo goes first (<see cref="WeaponLoadout"/>); otherwise vanilla picks the next matching
    /// stack by itself. A weapon that becomes unusable falls back (with an event) or stops (fallback off).</item>
    /// </list>
    /// </summary>
    private EnemyMotion? _bossSeen;

    private GameTaskStatus TickBoss(GameCtx ctx)
    {
        var p = ctx.Player;
        var npc = Main.npc[_id];
        if (!npc.active || npc.type != _type || npc.life <= 0)
        {
            if (CombatKit.WasKilled(_id, _type, _targetSince))
            {
                _stats.AddKill(_targetName);
                _stats.BossKilled = true;
                _stats.ObserveBoss(_targetName, 0, npc.lifeMax);
                if (_all) return NextTarget(ctx);
                return Finish(ctx, GameTaskStatus.Succeeded, $"killed {_targetName}");
            }
            return Finish(ctx, GameTaskStatus.Failed,
                $"boss fled: {_targetName} despawned with {_stats.BossLifeLeftPct}% life left (dawn, or the player was too far away)");
        }
        _stats.ObserveBoss(_targetName, npc.life, npc.lifeMax);
        bool visible = Visibility.IsEntityVisible(npc);
        if (visible)
        {
            _lastSeen = Main.GameUpdateCount;
            _bossSeen = Motion(npc);
        }
        // Fair vision: out of sight, use where it was last seen (carried along its velocity), never its real position.
        var bossMotion = visible || _bossSeen is not { } seenMotion
            ? Motion(npc)
            : LastSeen.Extrapolate(seenMotion, Main.GameUpdateCount - _lastSeen);
        TrackMinionKill();

        _weapon = _loadout.Tick(p, npc.defense);
        if (_weapon is not { } w) return Finish(ctx, GameTaskStatus.Failed, _loadout.StopReason ?? "no usable weapon (out of ammo or mana?)");
        var strategy = Strategies.Effective(_loadout.Strategy, w.Style);
        UpdateStats(p, w, strategy);

        var k = NavMath.FromPixels(p.position.X, p.position.Y, p.velocity.X, p.velocity.Y, p.height);
        if (_arena == null && k.OnGround)
        {
            var (sx, sy) = StateReader.StandingTile(p);
            _arena = ArenaScan.Default(ctx.Grid, new TilePoint(sx, sy));
        }

        // Nearby hostiles other than the boss: evasion keeps clear of them; one about to arrive may be shot first.
        var others = new List<EnemyMotion>();
        var contenders = new List<Contender>();
        var screen = Visibility.Screen;
        foreach (var n in Main.npc)
        {
            if (!n.active || n.whoAmI == _id) continue;
            if (!TargetRules.IsAttackable(CombatKit.Traits(n)) || !Visibility.IsEntityVisible(n, screen, p)) continue;
            var o = CombatKit.Offset(p, n);
            if (o.Length() > MinionFocus.KeepRange) continue;
            if (others.Count < MaxOthers) others.Add(Motion(n));
            contenders.Add(new Contender(n.whoAmI, o.X, o.Y, n.velocity.X / 16f, n.velocity.Y / 16f));
        }
        int? minionId = _focus.Tick(contenders);

        bool los = CombatKit.LineOfSight(p, npc);
        // hit_and_run's rhythm goes by last tick's distance (from the fair boss motion, not its hidden position).
        bool backOff = SwingRhythm(p, strategy, _lastBossDist, w.Reach);
        var d = _bossMove.Tick(new BossMoveInputs(k, ctx.Grid, _arena, bossMotion, others, w.Style, w.Reach, visible, los,
            strategy, _keep, backOff));
        _lastBossDist = d.DistTiles;
        if (d.DodgeStarted) _stats.AddDodge();

        ControlIntent intent = default;
        intent.Left = d.Dir < 0;
        intent.Right = d.Dir > 0;
        // Jump over a wall we are stuck at; not merely because the speed passes zero while turning around.
        var (bsx, bsy) = StateReader.StandingTile(p);
        bool blocked = k.OnGround && Math.Abs(p.velocity.X) < 0.1f && StepSafety.WallAhead(ctx.Grid, new TilePoint(bsx, bsy), d.Dir);
        intent.Jump = JumpPulse.Next(d.Jump || blocked, k.OnGround, p.velocity.Y, ref _lastJump);
        if (d.CutJump && !k.OnGround) intent.Jump = _lastJump = false;
        if (d.Drop) _dropTicks = DropHoldTicks;
        if (_dropTicks > 0)
        {
            intent.Down = true;
            _dropTicks--;
        }

        // Aim: the focused minion if it can be hit, else the boss.
        NPC aimAt = npc;
        bool fire = d.Fire;
        if (minionId is { } mid && Main.npc[mid] is { active: true } m)
        {
            var mo = CombatKit.Offset(p, m);
            bool mlos = CombatKit.LineOfSight(p, m);
            if (CombatStance.ShouldFire(w.Style, mo.Length(), w.Reach, mlos))
            {
                aimAt = m;
                fire = true;
                if (_minion?.Id != mid) _minion = (mid, m.type, Main.GameUpdateCount);
            }
        }
        bool held = _lease.Hold(p, w.Slot);
        if (visible || aimAt != npc) intent = CombatKit.Aim(intent, p, w, aimAt, fire && held, ref _lastUse);
        else _lastUse = false;
        ControlState.Pending = intent;

        int? ammo = CombatKit.AmmoCount(p, w.Item);
        _stats.AmmoLeft = ammo;
        bool sick = p.HasBuff(BuffID.PotionSickness);
        Progress = $"{_targetName} {_stats.BossLifeLeftPct}% ({npc.life}/{npc.lifeMax}), {d.DistTiles:0} tiles"
            + (visible ? "" : $" (out of sight {(Main.GameUpdateCount - _lastSeen) / 60} s, going by its last sighting)")
            + $", {_loadout.Label(p)}" + (ammo is { } a ? $" ({a} ammo)" : "") + $", {Strategies.Name(strategy)}"
            + (aimAt != npc ? $", shooting {aimAt.GivenOrTypeName} first" : "")
            + (d.Evading ? ", dodging" : "") + (d.Crossing ? ", running under it" : "")
            + (d.Cornered ? ", CORNERED (little arena left behind you)" : "")
            + $"; life {p.statLife}/{p.statLifeMax2}" + (sick ? " (potion sickness)" : "")
            + $", potions {_stats.PotionsUsed}, dodges {_stats.Dodges}";
        return GameTaskStatus.Running;
    }

    private const int MaxOthers = 8, DropHoldTicks = 3;

    /// <summary>hit_and_run: did a swing just start in reach? Then back off for a moment (<see cref="HitAndRunRhythm"/>).</summary>
    private bool SwingRhythm(Player p, AttackStrategy strategy, float dist, float reach)
    {
        bool started = p.itemAnimation > 0 && _lastAnim == 0;
        _lastAnim = p.itemAnimation;
        if (strategy != AttackStrategy.HitAndRun)
        {
            _rhythm.Reset();
            return false;
        }
        return _rhythm.Tick(started, dist, reach);
    }

    /// <summary>What the result reports: weapon, ammo and strategy in use, and every switch so far.</summary>
    private void UpdateStats(Player p, Weapon w, AttackStrategy strategy)
    {
        _stats.Weapon = w.Item.Name;
        _stats.Ammo = _loadout.AmmoInUse(p);
        _stats.Strategy = Strategies.Name(strategy);
        while (_stats.Switches.Count < _loadout.Switches.Count) _stats.AddSwitch(_loadout.Switches[_stats.Switches.Count]);
        Result = _stats.Result(Main.GameUpdateCount);
    }

    /// <summary>Count a focused minion that died since it was picked.</summary>
    private void TrackMinionKill()
    {
        if (_minion is not { } m) return;
        var n = Main.npc[m.Id];
        if (n.active && n.type == m.Type) return;
        if (CombatKit.WasKilled(m.Id, m.Type, m.Since)) _stats.AddKill(Lang.GetNPCNameValue(m.Type));
        _minion = null;
    }

    public void OnCancel(GameCtx ctx) => Cleanup(ctx);

    /// <summary>The target's hitbox and motion for <see cref="ContactPredictor"/>, in tiles. Slimes and King Slime
    /// lose 20% of their horizontal speed per tick on the ground (vanilla slime AI), so a landing doesn't slide on.</summary>
    private static EnemyMotion Motion(NPC n) =>
        new(n.Center.X / 16f, n.Center.Y / 16f, n.width / 32f, n.height / 32f, n.velocity.X / 16f, n.velocity.Y / 16f,
            n.noGravity ? 0f : NpcGravityPx / 16f, GroundFriction: n.aiStyle is NPCAIStyleID.Slime or NPCAIStyleID.KingSlime ? 0.8f : 1f);

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
        bool safeL = StepSafety.IsSafe(ctx.Grid, stand, -1) && (_arena?.Allows(sx, -1) ?? true);
        bool safeR = StepSafety.IsSafe(ctx.Grid, stand, 1) && (_arena?.Allows(sx, 1) ?? true);
        int dir = Retreat.Direction(dxs, safeL, safeR);
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
        _kiter.Reset();
        _rhythm.Reset();
        _lastBossDist = float.MaxValue;
        _boss = EntityReader.IsBoss(npc);
        if (_boss)
        {
            _bossMove.Reset();
            _focus.Reset();
            _minion = null;
            _stats.ObserveBoss(_targetName, npc.life, npc.lifeMax);
        }
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
        Reflexes.ClearHealOverride(this);
        StopMove(ctx);
        ControlState.Pending = default;
        _loadout.Restore(ctx.Player); // ammo swaps are newer than the lease's swap: undo them first
        _lease.Restore(ctx.Player);
        while (_stats.Switches.Count < _loadout.Switches.Count) _stats.AddSwitch(_loadout.Switches[_stats.Switches.Count]);
        Result = _stats.Result(Main.GameUpdateCount);
    }
}
