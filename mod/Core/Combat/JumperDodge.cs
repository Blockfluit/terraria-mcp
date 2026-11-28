using LlmPlayer.Core.Nav;

namespace LlmPlayer.Core.Combat;

/// <summary>Where a falling enemy comes down: ballistic flight (its velocity, its gravity, capped fall speed) until its
/// hitbox bottom reaches a floor.</summary>
public static class Ballistic
{
    /// <summary>Resting: no vertical speed and its bottom on a tile edge (an NPC standing on tiles). A jumper at the top
    /// of its arc has (almost) no vertical speed too, but hardly ever on a whole tile row.</summary>
    public static bool Grounded(in EnemyMotion e)
    {
        if (MathF.Abs(e.VY) > 0.001f) return false;
        float bottom = e.CY + e.HalfH, frac = bottom - MathF.Floor(bottom);
        return frac < 0.02f || frac > 0.98f;
    }

    /// <summary>Ticks until the enemy's bottom reaches <paramref name="floorBottom"/> (tiles) and its centre x then,
    /// or null when it isn't above that floor or won't land within <paramref name="maxTicks"/>. Horizontal speed is
    /// kept (a jumping slime doesn't steer much in the air).</summary>
    public static (int Ticks, float X)? Landing(in EnemyMotion e, float floorBottom, int maxTicks = 240)
    {
        if (e.Gravity <= 0f || e.CY + e.HalfH > floorBottom + 0.01f) return null;
        float y = e.CY + e.HalfH, x = e.CX, vy = e.VY;
        for (int t = 1; t <= maxTicks; t++)
        {
            vy = Math.Min(vy + e.Gravity, Math.Max(e.MaxFall, vy));
            x += e.VX;
            y += vy;
            if (y >= floorBottom && vy > 0f) return (t, x);
        }
        return null;
    }
}

/// <summary>What <see cref="JumperDodge"/> wants this tick. <see cref="Override"/>: the boss is in the air and coming
/// down on the player's floor, so this plan replaces <see cref="Evasion"/> against the boss (it looks further ahead,
/// to the landing and a little past it). <see cref="Crossing"/>: the plan runs under the boss to its other side.
/// <see cref="LandInTicks"/>/<see cref="LandX"/>: the predicted landing (null when it is on the ground).</summary>
public readonly record struct JumperPlan(int Dir, bool Jump, bool Override, bool Crossing, bool Cornered,
    int? LandInTicks = null, float LandX = 0f, bool Contact = false);

/// <summary>A jumper's take-off speed (tiles/tick): <see cref="VX"/> toward the player (≥ 0), <see cref="VY"/> up (&lt; 0).</summary>
public readonly record struct Hop(float VX, float VY)
{
    /// <summary>Same kind of jump (the vertical launch speed tells them apart).</summary>
    public bool Like(Hop o) => MathF.Abs(VY - o.VY) < 1.2f / 16f;
}

/// <summary>
/// The jumps a hopping boss has made, to guess the next one the way a player learns its rhythm. <see cref="Next"/>:
/// the longest recent run of jumps (at least <see cref="MinSuffix"/>) seen before decides what followed it then; with
/// no such match, every kind seen so far plus the prior's (all must be dodgeable). The history starts with a prior,
/// King Slime's cycle twice (vanilla aiStyle 15: small, small, medium, big; px/tick small 4/-8, medium 4.5/-6, big
/// 3.5/-13), so a fresh fight expects a small jump first; jumps that don't fit soon outweigh it.
/// </summary>
public sealed class HopPattern
{
    public const int MaxHistory = 24, MaxSuffix = 6, MinSuffix = 2;

    public static readonly Hop Small = new(4f / 16f, -8f / 16f), Medium = new(4.5f / 16f, -6f / 16f), Big = new(3.5f / 16f, -13f / 16f);
    public static readonly Hop[] KingSlimeCycle = { Small, Small, Medium, Big };

    private readonly List<Hop> _seen = new();
    private readonly Hop[] _prior;

    /// <param name="prior">The cycle assumed before any jump is seen (default <see cref="KingSlimeCycle"/>).</param>
    public HopPattern(Hop[]? prior = null)
    {
        _prior = prior ?? KingSlimeCycle;
        Reset();
    }

    public IReadOnlyList<Hop> History => _seen;

    public void Reset()
    {
        _seen.Clear();
        _seen.AddRange(_prior);
        _seen.AddRange(_prior);
    }

    public void Observe(Hop h)
    {
        _seen.Add(h);
        if (_seen.Count > MaxHistory) _seen.RemoveAt(0);
    }

    /// <summary>The jump after the next <paramref name="ahead"/> ones (0 = the very next): one when the pattern is
    /// clear, else every kind seen.</summary>
    public IReadOnlyList<Hop> Next(int ahead = 0)
    {
        if (ahead == 0) return Predict(_seen);
        var seq = new List<Hop>(_seen);
        for (int i = 0; i < ahead; i++)
        {
            var p = Predict(seq);
            if (p.Count != 1) return Kinds(seq);
            seq.Add(p[0]);
        }
        return Predict(seq);
    }

    private static IReadOnlyList<Hop> Predict(List<Hop> seq)
    {
        int n = seq.Count;
        for (int len = Math.Min(MaxSuffix, n - 1); len >= MinSuffix; len--)
        {
            // The latest earlier place where the last `len` jumps happened too: what came next there.
            for (int end = n - 2; end >= len - 1; end--)
            {
                bool match = true;
                for (int k = 0; k < len && match; k++) match = seq[end - k].Like(seq[n - 1 - k]);
                if (match) return new[] { seq[end + 1] };
            }
        }
        return Kinds(seq);
    }

    private static IReadOnlyList<Hop> Kinds(List<Hop> seq)
    {
        var all = new List<Hop>();
        foreach (var h in seq)
            if (!all.Any(a => a.Like(h))) all.Add(h);
        return all;
    }
}

/// <summary>
/// Moving against a boss that hops at the player (gravity-bound, contact damage, e.g. King Slime: small, medium and big
/// jumps toward the player at ~4 px/tick, faster than the player runs, with a hitbox ~7 tiles across and ~7 tall).
/// The player needs half a second to reach full speed, so where it stands and which way it already moves when the
/// boss takes off decide the dodge: a medium jump can only be outrun (from ~8 tiles away), small and big ones can also
/// be passed under.
/// <list type="bullet">
/// <item>In the air (coming down on the player's floor): try running left, right or standing (a jump only as a last
/// resort) until it has landed and slid to a stop (<see cref="Ballistic.Landing"/> + <see cref="LandPadTicks"/>),
/// with the arena's ends as walls and its minions in the way. Contact-free plans beat any contact; among them the one
/// that ends with the widest gap and the most room behind the player, and from where the NEXT jump
/// (<see cref="HopPattern"/>) can be dodged too: running UNDER it to the other side while it is high in the air
/// rather than being driven into a corner.</item>
/// <item>On the ground (ranged, within <see cref="FarTiles"/>): step away, stand or step closer, whichever leaves the
/// player best placed to dodge the next jump if it came in <see cref="ReadyTicks"/>.</item>
/// </list>
/// Strategy-neutral apart from that: melee keeps its plan on the ground; the airborne dodge applies to every strategy.
/// </summary>
public sealed class JumperDodge
{
    public const int LandPadTicks = 15, MaxHorizon = 150, ReadyTicks = 12, ReadyLongTicks = 36, GroundDepth = 1, AirDepth = 1;
    public const float ReadyMarginTiles = 0.75f, FarTiles = 60f, RoomCapTiles = 20f, KeepPlanGapTiles = 6f;
    public const double GapWeight = 0.5, RoomWeight = 1.6, ReadyWeight = 1.0, MinionPenalty = 3.0;

    private readonly PlayerPhysics _phys;
    private readonly HopPattern _pattern = new();
    private int _lastDir;
    private bool _wasGrounded;

    public JumperDodge(PlayerPhysics? phys = null) => _phys = phys ?? PlayerPhysics.Vanilla;

    public HopPattern Pattern => _pattern;

    public void Reset()
    {
        _lastDir = 0;
        _wasGrounded = false;
        _pattern.Reset();
    }

    /// <summary>Hops under gravity (not a flyer).</summary>
    public static bool IsJumper(in EnemyMotion e) => e.Gravity > 0f;

    private const float HalfW = PlayerPhysics.HitboxWidth / 2f;

    private readonly record struct Ctx(float Floor, float MinLeft, float MaxRight, IReadOnlyList<EnemyMotion>? Others);

    /// <param name="floorBottom">The player's floor (top edge, tiles; <see cref="ContactPredictor.FloorBelow"/>).</param>
    /// <param name="planDir">What the strategy would do (kiter / step toward / 0).</param>
    /// <param name="canLeft">A step left is allowed now (arena and safety); likewise <paramref name="canRight"/>.</param>
    /// <param name="others">Minions to keep clear of too.</param>
    public JumperPlan Plan(in PlayerKinematics me, in EnemyMotion boss, float floorBottom, ArenaBounds? arena,
        WeaponStyle style, int planDir, bool canLeft, bool canRight, IReadOnlyList<EnemyMotion>? others = null)
    {
        float pcx = me.X + HalfW;
        var c = new Ctx(floorBottom, arena is { } a1 ? a1.X1 : float.NegativeInfinity,
            arena is { } a2 ? a2.X2 + 1f : float.PositiveInfinity, others);
        float gapX = MathF.Abs(pcx - boss.CX) - HalfW - boss.HalfW;
        bool cornered = BossMovement.Cornered(arena, pcx, boss);

        bool grounded = Ballistic.Grounded(boss);
        if (_wasGrounded && !grounded && boss.VY < 0f)
            _pattern.Observe(new Hop(MathF.Abs(boss.VX), boss.VY - boss.Gravity)); // its launch speed, before a tick of gravity
        _wasGrounded = grounded;

        if (!float.IsFinite(floorBottom) || grounded || Ballistic.Landing(boss, floorBottom) is not { } land)
        {
            if (style == WeaponStyle.Melee || gapX > FarTiles || !float.IsFinite(floorBottom))
                return new JumperPlan(planDir, false, false, false, cornered);
            return Remember(Ground(me, boss, c, planDir, canLeft, canRight, cornered));
        }

        var (best, _, plan, planOk) = Evaluate(me, boss, c, planDir, canLeft, canRight, depth: AirDepth, style);
        var pick = planOk ? plan : best;
        return Remember(pick with { Cornered = cornered, LandInTicks = land.Ticks, LandX = land.X, Override = true });
    }

    private JumperPlan Remember(JumperPlan p)
    {
        if (p.Dir != 0) _lastDir = p.Dir;
        return p;
    }

    /// <summary>
    /// The best constant maneuver against an airborne boss (and the strategy's own, whether it is good enough to
    /// keep). Score: contact = -1000 + ticks until it; clear = <see cref="GapWeight"/> × gap at the end (≤ 10) +
    /// <see cref="RoomWeight"/> × room behind (≤ <see cref="RoomCapTiles"/>), plus (depth ≥ 1) how well the next jump
    /// could be dodged from there (<see cref="ReadinessTerm"/>), a little for sticking with the current direction, and
    /// minus <see cref="MinionPenalty"/> with a minion in the way.
    /// </summary>
    /// <param name="ahead">Which jump <paramref name="boss"/> is, counted from the next one to come (0); the
    /// readiness look-ahead asks the pattern for the one after it.</param>
    private (JumperPlan Best, double Score, JumperPlan Plan, bool PlanOk) Evaluate(in PlayerKinematics me,
        in EnemyMotion boss, in Ctx c, int planDir, bool canLeft, bool canRight, int depth, WeaponStyle style, int ahead = 0)
    {
        int away = me.X + HalfW >= boss.CX ? 1 : -1;
        int horizon = Ballistic.Landing(boss, c.Floor) is { } land ? Math.Min(land.Ticks + LandPadTicks, MaxHorizon) : MaxHorizon / 2;
        JumperPlan best = default, plan = default;
        double bestScore = double.NegativeInfinity;
        bool planOk = false;
        foreach (int dir in new[] { -1, 0, 1 })
        {
            if (dir < 0 && !canLeft || dir > 0 && !canRight) continue;
            for (int j = 0; j < 2; j++)
            {
                if (j == 1 && (!me.OnGround || ahead > 0)) continue; // a jump of its own only for the dodge at hand
                var m = new Maneuver(dir, j == 1);
                var tr = ContactPredictor.Trace(me, boss, m, c.Floor, _phys, horizon, 0, c.MinLeft, c.MaxRight);
                var f = tr.Forecast;
                bool minionInWay = false;
                if (!f.Contact && depth > 0 && c.Others is { Count: > 0 } others)
                    foreach (var o in others)
                        if (ContactPredictor.Forecast(me, o, m, c.Floor, _phys, Math.Min(horizon, Evasion.RangedHorizon)).Contact)
                        {
                            minionInWay = true; // a minion in the way: a small hit, nothing like the boss's
                            break;
                        }
                int side = tr.PlayerCX >= tr.EnemyCX ? 1 : -1;
                float endGap = MathF.Abs(tr.PlayerCX - tr.EnemyCX) - HalfW - boss.HalfW;
                float room = side > 0 ? c.MaxRight - (tr.PlayerCX + HalfW) : (tr.PlayerCX - HalfW) - c.MinLeft;
                double score = f.Contact
                    ? -1000 + f.FirstContactTick!.Value + Math.Clamp(f.MinGap, -5f, 0f)
                    : GapWeight * Math.Min(endGap, 10f) + RoomWeight * Math.Min(room, RoomCapTiles);
                if (!f.Contact && depth > 0)
                {
                    var endMe = new PlayerKinematics(tr.PlayerCX - HalfW, c.Floor - 1f, tr.PlayerVX, 0f, true);
                    var landed = boss with { CX = tr.EnemyCX, CY = c.Floor - boss.HalfH, VX = 0f, VY = 0f };
                    score += ReadinessTerm(Readiness(endMe, landed, c, depth - 1, ahead));
                    score += (dir == _lastDir ? 0.75 : 0) + (dir == planDir ? 0.25 : 0);
                }
                if (j == 1) score -= 2.0; // a jump only when running alone fails
                if (minionInWay) score -= MinionPenalty;
                bool crossing = dir != 0 && dir == -away && side != away && !f.Contact;
                var candidate = new JumperPlan(dir, j == 1, true, crossing, false, null, 0f, f.Contact);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
                if (j == 0 && dir == planDir)
                {
                    plan = candidate;
                    planOk = !f.Contact && (style == WeaponStyle.Melee || endGap >= KeepPlanGapTiles) && score >= bestScore - 1.0;
                }
            }
        }
        if (planOk && plan.Dir == planDir) planOk = true;
        return (best, bestScore, plan, planOk);
    }

    /// <summary>A readiness score (see <see cref="Readiness"/>) as a bonus: an undodgeable next jump costs about 25.</summary>
    private static double ReadinessTerm(double r) => r < -500 ? -25.0 + Math.Clamp((r + 1000) / 20.0, 0, 5) : ReadyWeight * r;

    /// <summary>How well the player at <paramref name="me"/> could dodge the boss's jump number
    /// <paramref name="ahead"/> (<see cref="HopPattern.Next"/>; the worst of them when unsure) launched right now from
    /// where it sits, looking <paramref name="depth"/> jumps further.</summary>
    private double Readiness(in PlayerKinematics me, in EnemyMotion sitting, in Ctx c, int depth = 0, int ahead = 0)
    {
        double worst = double.PositiveInfinity;
        int toward = me.X + HalfW >= sitting.CX ? 1 : -1;
        foreach (var h in _pattern.Next(ahead))
        {
            // A margin on its width: the stance should survive a slightly different jump or a late reaction.
            var launched = sitting with
            {
                VX = h.VX * toward, VY = h.VY + sitting.Gravity, HalfW = sitting.HalfW + (ahead == 0 ? ReadyMarginTiles : 0f),
            };
            var (_, score, _, _) = Evaluate(me, launched, c, 0, true, true, depth, WeaponStyle.Ranged, ahead + 1);
            worst = Math.Min(worst, score);
        }
        return float.IsFinite((float)worst) ? worst : 0;
    }

    /// <summary>The boss sits on the ground, about to jump at the player: step away, stand or step closer, held for
    /// <see cref="ReadyTicks"/> (or, when that isn't enough, <see cref="ReadyLongTicks"/>), whichever leaves the best
    /// readiness for its next jump (never into it).</summary>
    private JumperPlan Ground(in PlayerKinematics me, in EnemyMotion boss, in Ctx c, int planDir, bool canLeft, bool canRight,
        bool cornered)
    {
        int bestDir = 0;
        double bestScore = double.NegativeInfinity;
        foreach (int ticks in new[] { ReadyTicks, ReadyLongTicks })
        {
            if (bestScore > -500) break; // something short already works
            foreach (int dir in new[] { 0, -1, 1 })
            {
                if (dir < 0 && !canLeft || dir > 0 && !canRight) continue;
                var tr = ContactPredictor.Trace(me, boss, new Maneuver(dir, false), c.Floor, _phys, ticks, 0, c.MinLeft, c.MaxRight);
                double score;
                if (tr.Forecast.Contact) score = -2000 + tr.Forecast.FirstContactTick!.Value;
                else
                {
                    var endMe = new PlayerKinematics(tr.PlayerCX - HalfW, c.Floor - 1f, tr.PlayerVX, 0f, true);
                    var sitting = boss with { CX = tr.EnemyCX, VX = 0f, VY = 0f };
                    score = Readiness(endMe, sitting, c, GroundDepth) + (dir == 0 ? 0.3 : 0) + (dir == _lastDir ? 0.2 : 0) + (dir == planDir ? 0.1 : 0);
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    bestDir = dir;
                }
            }
        }
        return new JumperPlan(bestDir, false, false, false, cornered);
    }
}
