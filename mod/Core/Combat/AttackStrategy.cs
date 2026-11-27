namespace LlmPlayer.Core.Combat;

/// <summary>
/// How attack moves, chosen by the LLM (strategy is its job; the mod only reacts within the second: aiming, dodging,
/// stepping). Every strategy keeps <see cref="Evasion"/> on.
/// <list type="bullet">
/// <item><see cref="Auto"/>: by weapon style (melee closes in, ranged keeps 8-15 tiles; boss fights kite / step in).</item>
/// <item><see cref="Kite"/> (ranged): keep about keep_distance tiles and keep running along the ground/arena.</item>
/// <item><see cref="HitAndRun"/> (melee): close in, swing, back off (<see cref="HitAndRunRhythm"/>), repeat.</item>
/// <item><see cref="Hold"/>: stand your ground; only dodging moves the player.</item>
/// <item><see cref="FleeAndShoot"/> (ranged): keep moving away from the target, shooting backwards.</item>
/// </list>
/// </summary>
public enum AttackStrategy { Auto, Kite, HitAndRun, Hold, FleeAndShoot }

public static class Strategies
{
    public const float DefaultKeepTiles = 12f, MinKeepTiles = 3f, MaxKeepTiles = 30f;

    /// <summary>Kite band around keep_distance: from keep - <see cref="KiteBelow"/> to keep + <see cref="KiteAbove"/>.</summary>
    public const float KiteBelow = 2f, KiteAbove = 3f;

    /// <summary>flee_and_shoot keeps running away while the target is closer than keep × this.</summary>
    public const float FleeFactor = 1.5f;

    public static readonly string[] Names = { "auto", "kite", "hit_and_run", "hold", "flee_and_shoot" };

    public static string Name(AttackStrategy s) => Names[(int)s];

    public static bool TryParse(string? s, out AttackStrategy strategy)
    {
        strategy = AttackStrategy.Auto;
        if (s == null) return false;
        int i = Array.IndexOf(Names, s.Trim().ToLowerInvariant());
        if (i < 0) return false;
        strategy = (AttackStrategy)i;
        return true;
    }

    /// <summary>The weapon style a strategy needs, or null (any).</summary>
    public static WeaponStyle? RequiredStyle(AttackStrategy s) => s switch
    {
        AttackStrategy.Kite or AttackStrategy.FleeAndShoot => WeaponStyle.Ranged,
        AttackStrategy.HitAndRun => WeaponStyle.Melee,
        _ => null,
    };

    /// <summary>Why <paramref name="weaponName"/> doesn't suit the strategy, or null when it does.</summary>
    public static string? CheckWeapon(AttackStrategy s, WeaponStyle style, string weaponName) =>
        RequiredStyle(s) is { } need && need != style
            ? $"strategy {Name(s)} needs a {(need == WeaponStyle.Ranged ? "ranged/magic" : "melee")} weapon; {weaponName} is {(style == WeaponStyle.Ranged ? "ranged/magic" : "melee")}"
            : null;

    /// <summary>The strategy actually applied with a weapon of <paramref name="style"/>: one that needs the other
    /// style (e.g. kite after a fallback to a sword) runs as <see cref="AttackStrategy.Auto"/>.</summary>
    public static AttackStrategy Effective(AttackStrategy s, WeaponStyle style) =>
        RequiredStyle(s) is { } need && need != style ? AttackStrategy.Auto : s;

    public static float Keep(float? keepDistance) =>
        Math.Clamp(keepDistance ?? DefaultKeepTiles, MinKeepTiles, MaxKeepTiles);

    /// <summary>Distance band (tiles) a ranged weapon keeps: auto 8-15, kite keep-2..keep+3.</summary>
    public static (float Min, float Max) Band(AttackStrategy s, float keep) => s switch
    {
        AttackStrategy.Kite => (Math.Max(2f, keep - KiteBelow), keep + KiteAbove),
        AttackStrategy.FleeAndShoot => (keep * FleeFactor, keep * FleeFactor),
        _ => (CombatStance.RangedMinTiles, CombatStance.RangedMaxTiles),
    };

    /// <summary>How far ranged weapons fire: the band's max plus slack, at least auto's.</summary>
    public static float FireRange(AttackStrategy s, float keep) =>
        Math.Max(CombatStance.RangedMaxTiles, Band(s, keep).Max) + CombatStance.RangedFireSlackTiles;

    /// <summary>
    /// Where to stand (normal fights; boss fights use <see cref="BossMovement"/>). dx/dy: target minus player, tiles.
    /// <paramref name="backOff"/>: hit_and_run's rhythm says back off now. Apply <see cref="Effective"/> first.
    /// </summary>
    public static StanceMove Decide(AttackStrategy s, WeaponStyle style, float dx, float dy, float reachTiles, bool los,
        float keep, bool backOff = false)
    {
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        switch (s)
        {
            case AttackStrategy.Hold:
                return StanceMove.Hold;
            case AttackStrategy.HitAndRun when backOff:
                return StanceMove.StepAway;
            case AttackStrategy.FleeAndShoot:
                if (dist < keep * FleeFactor) return StanceMove.StepAway;
                return !los || dist > FireRange(s, keep) ? StanceMove.PathToward : StanceMove.Hold;
        }
        if (style == WeaponStyle.Melee) return CombatStance.Decide(style, dx, dy, reachTiles, los);
        var (min, max) = Band(s, keep);
        if (!los || dist > max) return StanceMove.PathToward;
        if (dist < min) return StanceMove.StepAway;
        return StanceMove.Hold;
    }

    /// <summary>Is it worth pressing use this tick? Melee within reach + 1; ranged in line of sight within
    /// <see cref="FireRange"/>.</summary>
    public static bool ShouldFire(AttackStrategy s, WeaponStyle style, float dist, float reachTiles, bool los, float keep) =>
        style == WeaponStyle.Melee ? dist <= reachTiles + 1f : los && dist <= FireRange(s, keep);

    /// <summary>Kite keeps running (<see cref="Kiter"/>) instead of standing while inside its band.</summary>
    public static bool KeepsMoving(AttackStrategy s) => s == AttackStrategy.Kite;

    /// <summary>
    /// Boss fights: the planned direction before evasion. <paramref name="kiterDir"/> is the <see cref="Kiter"/>'s
    /// plan (runs across the arena, turning at the ends), <paramref name="dx"/> the boss's nearest point minus the
    /// player centre, <paramref name="canLeft"/>/<paramref name="canRight"/> whether a step that way is allowed.
    /// Auto: ranged kites, melee steps toward. Kite: like auto ranged, but runs away while closer than the band.
    /// HitAndRun: toward, or away while backing off. Hold: 0. FleeAndShoot: away, else along the kiter.
    /// </summary>
    public static int BossPlan(AttackStrategy s, WeaponStyle style, float dx, float dist, float reachTiles, float keep,
        bool backOff, int kiterDir, bool canLeft, bool canRight)
    {
        int away = dx > 0 ? -1 : 1;
        bool Can(int d) => d < 0 ? canLeft : d > 0 ? canRight : true;
        int toward = MathF.Abs(dx) > reachTiles * 0.5f ? Math.Sign(dx) : 0;
        int plan = s switch
        {
            AttackStrategy.Hold => 0,
            AttackStrategy.FleeAndShoot => Can(away) ? away : kiterDir,
            AttackStrategy.Kite => dist < Band(s, keep).Min && Can(away) ? away : kiterDir,
            AttackStrategy.HitAndRun => backOff ? (Can(away) ? away : 0) : toward,
            _ => style == WeaponStyle.Ranged ? kiterDir : toward,
        };
        return plan == kiterDir || Can(plan) ? plan : 0; // the kiter checks its own steps (and heads back into the arena)
    }
}

/// <summary>
/// hit_and_run's rhythm: once a swing starts with the target in reach, back off for <see cref="BackoffTicks"/> (or
/// until <see cref="ClearTiles"/> beyond reach), then close in again.
/// </summary>
public sealed class HitAndRunRhythm
{
    public const int BackoffTicks = 24;
    public const float ClearTiles = 2.5f;

    private int _left;

    public bool BackingOff => _left > 0;

    public void Reset() => _left = 0;

    /// <param name="swingStarted">The weapon's use animation began this tick.</param>
    /// <param name="dist">Player centre to the target's nearest point, tiles.</param>
    public bool Tick(bool swingStarted, float dist, float reachTiles)
    {
        if (_left > 0)
        {
            _left--;
            if (dist >= reachTiles + ClearTiles) _left = 0;
            return _left > 0;
        }
        if (swingStarted && dist <= reachTiles + 1f) _left = BackoffTicks;
        return _left > 0;
    }
}
