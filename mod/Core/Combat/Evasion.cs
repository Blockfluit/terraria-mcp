using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;

namespace LlmPlayer.Core.Combat;

/// <summary>
/// An enemy's hitbox and motion, in tiles and tiles/tick (y down). <see cref="Gravity"/> is its fall acceleration in
/// tiles/tick² (0 for flyers); <see cref="MaxFall"/> caps its fall speed.
/// </summary>
public readonly record struct EnemyMotion(float CX, float CY, float HalfW, float HalfH, float VX, float VY,
    float Gravity = 0f, float MaxFall = 10f / 16f);

/// <summary>One way to move for the next fraction of a second: run left/right/stand, with or without a jump.</summary>
public readonly record struct Maneuver(int Dir, bool Jump);

/// <summary>How a maneuver turns out against an enemy: the first tick its hitbox touches ours (null = never within the
/// horizon) and the smallest gap between the two boxes (tiles, negative = overlap).</summary>
public readonly record struct ContactForecast(int? FirstContactTick, float MinGap)
{
    public bool Contact => FirstContactTick != null;
}

/// <summary>
/// Predicts whether the player and an enemy will touch over the next ticks, for a given maneuver. Deliberately simple
/// and generic (nothing type-specific): the enemy continues on its current velocity (plus gravity, never sinking
/// below the player's floor if it started above it); the player follows vanilla run/jump physics
/// (<see cref="PlayerPhysics"/>) on a flat floor at <c>floorBottom</c> (the floor's top edge, in tiles).
/// </summary>
public static class ContactPredictor
{
    /// <summary>Hitboxes closer than this (tiles) count as touching: Terraria's contact damage uses a slightly
    /// inflated box, and our prediction is approximate.</summary>
    public const float ContactMargin = 0.25f;

    private const float HalfW = PlayerPhysics.HitboxWidthPx / 32f, HalfH = PlayerPhysics.HitboxHeightPx / 32f;

    public static ContactForecast Forecast(in PlayerKinematics me, in EnemyMotion e, Maneuver m, float floorBottom,
        PlayerPhysics phys, int horizonTicks)
    {
        float px = me.X + HalfW, bottom = me.Y + 1f, vx = me.VelX, vy = me.VelY;
        float accel = phys.RunAcceleration / 16f, max = phys.MaxRunSpeed / 16f, slow = phys.RunSlowdown / 16f;
        float g = phys.Gravity / 16f, maxFall = phys.MaxFallSpeed / 16f, js = phys.JumpSpeed / 16f;
        bool onGround = me.OnGround;
        int jumpLeft = 0;

        float ex = e.CX, ey = e.CY, evx = e.VX, evy = e.VY;
        bool clampEnemy = e.Gravity > 0f && e.CY + e.HalfH <= floorBottom + 0.01f;

        float minGap = Gap(px, bottom - HalfH, ex, ey, e);
        if (minGap < ContactMargin) return new ContactForecast(0, minGap);
        for (int t = 1; t <= horizonTicks; t++)
        {
            vx = Run(vx, m.Dir, onGround, accel, max, slow);
            if (m.Jump && t == 1 && onGround)
            {
                vy = -js;
                jumpLeft = phys.JumpHeight;
            }
            else if (jumpLeft > 0)
            {
                vy = -js;
                jumpLeft--;
            }
            vy = Math.Min(vy + g, maxFall);
            px += vx;
            bottom += vy;
            onGround = false;
            if (vy > 0f && bottom >= floorBottom && me.Y + 1f <= floorBottom + 0.01f)
            {
                bottom = floorBottom;
                vy = 0f;
                onGround = true;
            }

            evy = Math.Min(evy + e.Gravity, e.Gravity > 0f ? Math.Max(e.MaxFall, evy) : float.MaxValue);
            ex += evx;
            ey += evy;
            if (clampEnemy && ey + e.HalfH > floorBottom)
            {
                ey = floorBottom - e.HalfH;
                evy = 0f;
            }

            float gap = Gap(px, bottom - HalfH, ex, ey, e);
            minGap = Math.Min(minGap, gap);
            if (gap < ContactMargin) return new ContactForecast(t, minGap);
        }
        return new ContactForecast(null, minGap);
    }

    /// <summary>Vanilla HorizontalMovement in tiles/tick: dir +1/-1 runs, 0 slows down (half as fast in the air).</summary>
    public static float Run(float vx, int dir, bool onGround, float accel, float max, float slow)
    {
        if (dir != 0)
        {
            float v = vx * dir; // speed along dir
            if (v < max)
            {
                if (v < -slow) v += slow;
                v += accel;
            }
            else
            {
                float s = onGround ? slow : slow * 0.5f;
                v = v > s ? v - s : 0f;
            }
            return v * dir;
        }
        float f = onGround ? slow : slow * 0.5f;
        return vx > f ? vx - f : vx < -f ? vx + f : 0f;
    }

    /// <summary>Box gap: the larger of the horizontal and vertical separations (negative when overlapping).</summary>
    private static float Gap(float px, float py, float ex, float ey, in EnemyMotion e) =>
        Math.Max(Math.Abs(px - ex) - HalfW - e.HalfW, Math.Abs(py - ey) - HalfH - e.HalfH);

    /// <summary>The top edge (tiles) of the first floor at or below the player's feet, scanning
    /// <paramref name="maxDepth"/> rows below the player's hitbox columns; +∞ if none.</summary>
    public static float FloorBelow(ITileGrid g, in PlayerKinematics me, int maxDepth = 12)
    {
        int x0 = (int)MathF.Floor(me.X), x1 = (int)MathF.Floor(me.X + PlayerPhysics.HitboxWidth - 0.001f);
        int start = (int)MathF.Floor(me.Y) + 1;
        for (int y = start; y <= start + maxDepth; y++)
            for (int x = x0; x <= x1; x++)
                if (g.Get(x, y).IsStandable) return y;
        return float.PositiveInfinity;
    }
}

/// <summary>Inputs for one <see cref="Evasion"/> tick. <see cref="PlanDir"/>/<see cref="PlanJump"/> is what the
/// attack would do this tick without evasion (-1/0/+1).</summary>
public readonly record struct EvasionInputs(
    PlayerKinematics Me, EnemyMotion Enemy, WeaponStyle Style, int PlanDir, bool PlanJump, float FloorBottom,
    bool SafeLeft, bool SafeRight);

/// <param name="Evading">True: move with <see cref="Dir"/>/<see cref="Jump"/> instead of the attack's plan.</param>
/// <param name="Started">A new dodge began this tick (for counting).</param>
public readonly record struct EvasionDecision(bool Evading, int Dir, bool Jump, bool Started);

/// <summary>
/// "Don't trade hits": each tick, forecast the attack's planned movement against the target
/// (<see cref="ContactPredictor"/>). If it would touch the target within the horizon, try every maneuver (left /
/// stand / right, × jump when on the ground; never into an unsafe column) and take the best: contact-free first, then
/// away from the target, the planned direction, no jump, and the widest gap; if every maneuver touches, the one that
/// touches latest. The dodge sticks until the plan has been contact-free for <see cref="ReleaseTicks"/> ticks, so it
/// doesn't flicker. Melee looks less far ahead (<see cref="MeleeHorizon"/>) so it still gets close enough to swing:
/// it closes in, swings, and backs off as the enemy steps in — a hit-and-run rhythm. Generic: driven only by the
/// target's hitbox and velocity.
/// </summary>
public sealed class Evasion
{
    public const int MeleeHorizon = 18, RangedHorizon = 36, ReleaseTicks = 8;

    private readonly PlayerPhysics _phys;
    private bool _evading;
    private int _clearTicks;

    public Evasion(PlayerPhysics? phys = null) => _phys = phys ?? PlayerPhysics.Vanilla;

    public bool Evading => _evading;

    public EvasionDecision Tick(in EvasionInputs i)
    {
        int horizon = i.Style == WeaponStyle.Melee ? MeleeHorizon : RangedHorizon;
        var plan = new Maneuver(Allowed(i, i.PlanDir) ? i.PlanDir : 0, i.PlanJump && i.Me.OnGround);
        var planF = ContactPredictor.Forecast(i.Me, i.Enemy, plan, i.FloorBottom, _phys, horizon);

        if (!planF.Contact)
        {
            if (!_evading) return new EvasionDecision(false, plan.Dir, plan.Jump, false);
            if (++_clearTicks >= ReleaseTicks)
            {
                _evading = false;
                return new EvasionDecision(false, plan.Dir, plan.Jump, false);
            }
        }
        else _clearTicks = 0;

        bool started = !_evading;
        _evading = true;
        var best = Best(i, horizon);
        return new EvasionDecision(true, best.Dir, best.Jump, started);
    }

    /// <summary>The attack's own movement for a stance, as a direction: toward the target for StepToward/PathToward,
    /// away for StepAway (or against <paramref name="facing"/> when level with it), 0 for Hold.</summary>
    public static int PlanDir(StanceMove move, float dx, int facing) => move switch
    {
        StanceMove.StepToward or StanceMove.PathToward => Math.Sign(dx),
        StanceMove.StepAway => dx != 0f ? -Math.Sign(dx) : -facing,
        _ => 0,
    };

    public void Reset()
    {
        _evading = false;
        _clearTicks = 0;
    }

    private Maneuver Best(in EvasionInputs i, int horizon)
    {
        int away = AwayDir(i);
        Maneuver best = default;
        double bestScore = double.NegativeInfinity;
        foreach (int dir in new[] { away, 0, -away })
        {
            if (!Allowed(i, dir)) continue;
            foreach (bool jump in new[] { false, true })
            {
                if (jump && !i.Me.OnGround) continue;
                var m = new Maneuver(dir, jump);
                var f = ContactPredictor.Forecast(i.Me, i.Enemy, m, i.FloorBottom, _phys, horizon);
                double score = f.Contact
                    ? -1000 + f.FirstContactTick!.Value + Math.Clamp(f.MinGap, -5f, 0f)
                    : Math.Min(f.MinGap, 3f) + (dir == away ? 1.0 : 0) + (dir == i.PlanDir ? 0.3 : 0) - (jump ? 0.5 : 0);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = m;
                }
            }
        }
        return best;
    }

    private static int AwayDir(in EvasionInputs i)
    {
        float dx = i.Me.X + PlayerPhysics.HitboxWidth / 2f - i.Enemy.CX;
        if (MathF.Abs(dx) > 0.05f) return Math.Sign(dx);
        return i.Enemy.VX != 0f ? Math.Sign(i.Enemy.VX) : 1; // dead centre: get out of its way
    }

    private static bool Allowed(in EvasionInputs i, int dir) =>
        dir == 0 || !i.Me.OnGround || (dir < 0 ? i.SafeLeft : i.SafeRight);
}

/// <summary>Jump key timing: press on the ground when wanted (after a released tick, as vanilla requires); once a jump
/// is under way keep holding while rising, for full height; release otherwise.</summary>
public static class JumpPulse
{
    public static bool Next(bool want, bool onGround, float velY, ref bool last)
    {
        bool press = (want && onGround && !last) || (!onGround && last && velY < 0f);
        last = press;
        return press;
    }
}

/// <summary>When to stop fighting and get away, and which way.</summary>
public static class Retreat
{
    /// <summary>Life below <paramref name="threshold"/> × max and no way to heal right now (no healing potion, or
    /// Potion Sickness). threshold ≤ 0 disables retreating.</summary>
    public static bool ShouldRetreat(int life, int lifeMax, float threshold, bool canHealNow) =>
        threshold > 0f && life > 0 && life < threshold * lifeMax && !canHealNow;

    /// <summary>Direction to flee (-1/0/+1): away from the hostiles' weighted side (nearer ones weigh more, by 1/|dx|).
    /// If that side is unsafe, the other safe side; 0 if neither is safe.</summary>
    /// <param name="hostileDx">Hostile x minus player x (tiles), one per visible hostile.</param>
    public static int Direction(IEnumerable<float> hostileDx, bool safeLeft, bool safeRight)
    {
        double pull = 0;
        foreach (var dx in hostileDx) pull += Math.Sign(dx) / Math.Max(1.0, Math.Abs(dx));
        int away = pull > 0 ? -1 : pull < 0 ? 1 : 0;
        bool Safe(int d) => d < 0 ? safeLeft : safeRight;
        if (away != 0 && Safe(away)) return away;
        if (away == 0) return safeRight ? 1 : safeLeft ? -1 : 0;
        return Safe(-away) ? -away : 0;
    }
}

/// <summary>The structured outcome attack reports in the task's result.</summary>
public sealed record AttackResult(int Killed, string[] Kills, int DamageTaken, float DurationS, int Dodges, bool Retreated);

/// <summary>Running totals for one attack task: life lost (decreases only; healing doesn't offset it), kills, dodges.</summary>
public sealed class AttackStats
{
    private readonly uint _startTick;
    private int? _lastLife;
    private readonly List<string> _kills = new();

    public AttackStats(uint startTick) => _startTick = startTick;

    public int DamageTaken { get; private set; }
    public int Dodges { get; private set; }
    public bool Retreated { get; set; }
    public IReadOnlyList<string> Kills => _kills;

    public void ObserveLife(int life)
    {
        if (_lastLife is { } prev && life < prev) DamageTaken += prev - life;
        _lastLife = life;
    }

    public void AddKill(string name) => _kills.Add(name);
    public void AddDodge() => Dodges++;

    public AttackResult Result(uint nowTick) =>
        new(_kills.Count, _kills.ToArray(), DamageTaken, MathF.Round((nowTick - _startTick) / 60f, 1), Dodges, Retreated);
}
