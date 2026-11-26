using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;

namespace LlmPlayer.Core.Combat;

/// <summary>
/// The columns a boss fight may use: the player's 2-wide body stays within <see cref="X1"/>..<see cref="X2"/>
/// (inclusive tile columns). Steps that would leave it are refused, with a column of slack for momentum; from
/// outside, steps back toward it are always allowed.
/// </summary>
public readonly record struct ArenaBounds(int X1, int X2)
{
    public const int MinWidth = 6;

    public int Width => X2 - X1 + 1;

    /// <summary>Is a body standing at feet-left column <paramref name="standX"/> inside?</summary>
    public bool Contains(int standX) => standX >= X1 && standX + 1 <= X2;

    /// <summary>May the body at <paramref name="standX"/> move one step in <paramref name="dir"/> (-1/+1)? Keeps one
    /// column of slack (a running player slides about a tile after letting go).</summary>
    public bool Allows(int standX, int dir)
    {
        if (dir == 0) return true;
        if (standX < X1) return dir > 0;
        if (standX + 1 > X2) return dir < 0;
        return dir < 0 ? standX - 2 >= X1 : standX + 3 <= X2;
    }

    public static ArenaBounds Of(int a, int b) => new(Math.Min(a, b), Math.Max(a, b));
}

/// <summary>The default arena: the walkable span around the player.</summary>
public static class ArenaScan
{
    public const int MaxSpan = 50;

    /// <summary>
    /// Walk left and right from <paramref name="stand"/> (feet-left tile) up to <paramref name="maxSpan"/> columns
    /// while each column is walkable from the last: a floor within <see cref="StepSafety.MaxDrop"/> below (no lava,
    /// liquid, hazard or unknown first), or a one-tile step up, with room for the 3-tall body. Platform rows and
    /// ground count alike.
    /// </summary>
    public static ArenaBounds Default(ITileGrid g, TilePoint stand, int maxSpan = MaxSpan)
    {
        int left = stand.X, right = stand.X + 1;
        int row = stand.Y;
        for (int x = stand.X - 1; x >= stand.X - maxSpan; x--)
        {
            if (!Next(g, x, ref row)) break;
            left = x;
        }
        row = stand.Y;
        for (int x = stand.X + 2; x <= stand.X + 1 + maxSpan; x++)
        {
            if (!Next(g, x, ref row)) break;
            right = x;
        }
        return new ArenaBounds(left, right);
    }

    /// <summary>Can the walker continue into column <paramref name="x"/> from standing row <paramref name="row"/>?
    /// Updates the row (step up one, or drop up to MaxDrop).</summary>
    private static bool Next(ITileGrid g, int x, ref int row)
    {
        if (BodyClear(g, x, row) && StepSafety.ColumnSafe(g, x, row))
        {
            int y = row + 1;
            while (g.Get(x, y).Kind == TileKind.Air) y++;
            row = y - 1;
            return true;
        }
        if (g.Get(x, row).IsStandable && BodyClear(g, x, row - 1) && StepSafety.ColumnSafe(g, x, row - 1))
        {
            row--;
            return true;
        }
        return false;
    }

    private static bool BodyClear(ITileGrid g, int x, int row)
    {
        for (int y = row - 2; y <= row; y++)
            if (g.Get(x, y).Kind is TileKind.Solid or TileKind.Unknown or TileKind.Lava or TileKind.Hazard) return false;
        return true;
    }
}

/// <summary>Dropping through the platform underfoot (hold Down): where would the player land?</summary>
public static class DropThrough
{
    public const int MaxDepth = 15;

    /// <summary>
    /// The top edge (tiles) of the floor the player would land on after dropping through the platform it stands on,
    /// or +∞ when it can't or shouldn't: not on the ground, a solid block underfoot (vanilla only drops through
    /// platforms), or nothing safe within <see cref="MaxDepth"/> tiles (lava, liquid, hazard or unknown first; deeper
    /// falls risk fall damage).
    /// </summary>
    public static float FloorBelow(ITileGrid g, in PlayerKinematics me, int maxDepth = MaxDepth)
    {
        if (!me.OnGround) return float.PositiveInfinity;
        int x0 = (int)MathF.Floor(me.X), x1 = (int)MathF.Floor(me.X + PlayerPhysics.HitboxWidth - 0.001f);
        int floor = (int)MathF.Floor(me.Y) + 1;
        bool platform = false;
        for (int x = x0; x <= x1; x++)
        {
            var k = g.Get(x, floor).Kind;
            if (k == TileKind.Solid) return float.PositiveInfinity;
            if (k == TileKind.Platform) platform = true;
        }
        if (!platform) return float.PositiveInfinity;
        for (int y = floor + 1; y <= floor + maxDepth; y++)
            for (int x = x0; x <= x1; x++)
            {
                var k = g.Get(x, y).Kind;
                if (k == TileKind.Air) continue;
                if (k is TileKind.Solid or TileKind.Platform) return y;
                return float.PositiveInfinity;
            }
        return float.PositiveInfinity;
    }
}

/// <summary>Is an enemy dashing at the player: fast, and headed (within ~37°) at the player's centre?</summary>
public static class DashDetector
{
    /// <summary>Tiles/tick (4.8 px/tick): faster than any walker, slower than a boss charge.</summary>
    public const float MinSpeed = 0.3f;
    public const float MinCos = 0.8f;

    public static bool IsDash(in PlayerKinematics me, in EnemyMotion e)
    {
        float speed = MathF.Sqrt(e.VX * e.VX + e.VY * e.VY);
        if (speed < MinSpeed) return false;
        float px = me.X + PlayerPhysics.HitboxWidth / 2f, py = me.Y + 1f - PlayerPhysics.HitboxHeightPx / 32f;
        float dx = px - e.CX, dy = py - e.CY, d = MathF.Sqrt(dx * dx + dy * dy);
        if (d < 0.01f) return true;
        return (dx * e.VX + dy * e.VY) / (d * speed) >= MinCos;
    }
}

/// <summary>
/// Kiting along an arena: keep running one way (initially away from the boss), turn around near either end
/// (<see cref="EdgeMargin"/> columns before it) or where the next step is unsafe. A moving player is much harder to
/// dash into than a standing one; the shots keep flying meanwhile.
/// </summary>
public sealed class Kiter
{
    public const int EdgeMargin = 4;

    private int _dir;

    public int Dir => _dir;

    public void Reset() => _dir = 0;

    /// <param name="standX">Feet-left column.</param>
    /// <param name="bossDx">Boss centre minus player centre, tiles.</param>
    /// <param name="canLeft">A step left is allowed (safe and inside the arena); likewise <paramref name="canRight"/>.</param>
    public int Plan(int standX, ArenaBounds? arena, float bossDx, bool canLeft, bool canRight)
    {
        if (arena is { } a && !a.Contains(standX)) return _dir = standX < a.X1 ? 1 : -1; // head back in
        if (_dir == 0) _dir = bossDx > 0 ? -1 : 1;
        if (Blocked(_dir, standX, arena, canLeft, canRight))
        {
            if (Blocked(-_dir, standX, arena, canLeft, canRight))
                return (_dir < 0 ? canLeft : canRight) ? _dir : (_dir < 0 ? canRight : canLeft) ? -_dir : 0;
            _dir = -_dir;
        }
        return _dir;
    }

    private static bool Blocked(int dir, int standX, ArenaBounds? arena, bool canLeft, bool canRight)
    {
        if (!(dir < 0 ? canLeft : canRight)) return true;
        if (arena is not { } a) return false;
        return dir < 0 ? standX - EdgeMargin < a.X1 : standX + 1 + EdgeMargin > a.X2;
    }
}

/// <summary>A non-boss hostile near the player, for <see cref="MinionFocus"/>: the vector (tiles) from the player's
/// centre to the nearest point of its hitbox, and its velocity (tiles/tick).</summary>
public readonly record struct Contender(int Id, float Dx, float Dy, float VX, float VY)
{
    public float Dist => MathF.Sqrt(Dx * Dx + Dy * Dy);

    /// <summary>Speed toward the player (tiles/tick; negative = moving away).</summary>
    public float Closing => Dist < 0.01f ? 0f : -(Dx * VX + Dy * VY) / Dist;
}

/// <summary>
/// During a boss fight, which minion (if any) to shoot before going back to the boss: a hostile within
/// <see cref="Range"/> that is about to reach the player (within <see cref="CloseTiles"/>, or closing in), the one
/// that would arrive first. Once picked it stays the focus until it dies or gets beyond <see cref="KeepRange"/>.
/// </summary>
public sealed class MinionFocus
{
    public const float Range = 12f, KeepRange = 16f, CloseTiles = 5f, MinClosing = 0.05f;

    private int? _id;

    public int? Current => _id;

    public void Reset() => _id = null;

    public int? Tick(IEnumerable<Contender> minions)
    {
        var list = minions as IReadOnlyCollection<Contender> ?? minions.ToList();
        if (_id is { } cur)
        {
            foreach (var m in list)
                if (m.Id == cur && m.Dist <= KeepRange) return cur;
            _id = null;
        }
        float bestEta = float.MaxValue;
        foreach (var m in list)
        {
            float d = m.Dist;
            if (d > Range) continue;
            float closing = m.Closing;
            if (d > CloseTiles && closing < MinClosing) continue;
            float eta = d / MathF.Max(closing, MinClosing);
            if (eta < bestEta || (eta == bestEta && m.Id < _id))
            {
                bestEta = eta;
                _id = m.Id;
            }
        }
        return _id;
    }
}

/// <summary>Inputs for one <see cref="BossMovement"/> tick. <see cref="Boss"/> is the boss's hitbox and motion (even
/// off-screen: its position is public, like the vanilla boss bar); <see cref="CanSeeBoss"/> gates firing.</summary>
public readonly record struct BossMoveInputs(
    PlayerKinematics Me, ITileGrid Grid, ArenaBounds? Arena, EnemyMotion Boss, IReadOnlyList<EnemyMotion>? Others,
    WeaponStyle Style, float ReachTiles, bool CanSeeBoss, bool LineOfSight);

/// <param name="Drop">Hold Down this tick to drop through the platform underfoot.</param>
/// <param name="Fire">Worth shooting/swinging at the boss now.</param>
/// <param name="DistTiles">Player centre to the nearest point of the boss's hitbox.</param>
public readonly record struct BossMoveDecision(int Dir, bool Jump, bool Drop, bool Evading, bool DodgeStarted, bool Fire,
    float DistTiles);

/// <summary>
/// How attack moves against a boss (and the boss sim drives the same code): never pathfinding, never leaving the
/// arena, never stepping into an unsafe column.
/// <list type="bullet">
/// <item>Ranged: kite (<see cref="Kiter"/>) across the arena and shoot whenever the boss is visible, in line of sight
/// and within <see cref="FireTiles"/>.</item>
/// <item>Melee: step toward the boss horizontally, swing in reach; <see cref="Evasion"/> makes it hit-and-run.</item>
/// <item>Both: on solid ground under a platform row (3-5 tiles up) with no dash coming, jump up onto it: from a
/// platform, dropping through is one more way out.</item>
/// <item>Both: <see cref="Evasion"/> against the boss and nearby minions overrides the plan when contact is coming:
/// sideways, a jump, or dropping through the platform underfoot to the row below.</item>
/// <item>Near an arena edge, brake (<see cref="BrakeFor"/>) rather than slide past it.</item>
/// </list>
/// </summary>
public sealed class BossMovement
{
    public const float FireTiles = 25f;

    private readonly Evasion _evasion;
    private readonly Kiter _kiter = new();

    public BossMovement(PlayerPhysics? phys = null) => _evasion = new Evasion(phys);

    public bool Evading => _evasion.Evading;

    public void Reset()
    {
        _evasion.Reset();
        _kiter.Reset();
    }

    public BossMoveDecision Tick(in BossMoveInputs i)
    {
        var me = i.Me;
        var stand = NavMath.StandingTile(me);
        bool leftOk = i.Arena?.Allows(stand.X, -1) ?? true, rightOk = i.Arena?.Allows(stand.X, 1) ?? true;
        bool safeL = StepSafety.IsSafe(i.Grid, stand, -1), safeR = StepSafety.IsSafe(i.Grid, stand, 1);

        float pcx = me.X + PlayerPhysics.HitboxWidth / 2f, pcy = me.Y + 1f - PlayerPhysics.HitboxHeightPx / 32f;
        var b = i.Boss;
        float nx = Math.Clamp(pcx, b.CX - b.HalfW, b.CX + b.HalfW), ny = Math.Clamp(pcy, b.CY - b.HalfH, b.CY + b.HalfH);
        float dx = nx - pcx, dy = ny - pcy, dist = MathF.Sqrt(dx * dx + dy * dy);

        int plan;
        if (i.Style == WeaponStyle.Ranged)
            plan = _kiter.Plan(stand.X, i.Arena, b.CX - pcx, leftOk && safeL, rightOk && safeR);
        else
        {
            plan = MathF.Abs(dx) > i.ReachTiles * 0.5f ? Math.Sign(dx) : 0;
            if (plan < 0 && !(leftOk && safeL) || plan > 0 && !(rightOk && safeR)) plan = 0;
        }

        // Climb to a platform row above when nothing threatens: up there, dropping through is one more way out.
        float dropFloor = DropThrough.FloorBelow(i.Grid, me);
        bool climb = me.OnGround && !float.IsFinite(dropFloor) && PlatformAbove(i.Grid, me) && !DashDetector.IsDash(me, b);

        var d = _evasion.Tick(new EvasionInputs(me, b, i.Style, plan, climb, ContactPredictor.FloorBelow(i.Grid, me),
            safeL, safeR, dropFloor, i.Others, leftOk, rightOk));

        bool fire = i.CanSeeBoss && (i.Style == WeaponStyle.Ranged
            ? i.LineOfSight && dist <= FireTiles
            : dist <= i.ReachTiles + 1f);
        int dir = d.Dir;
        if (i.Arena is { } a && BrakeFor(a, me) is var brake && brake != 0) dir = brake;
        return d.Evading
            ? new BossMoveDecision(dir, d.Jump, d.Drop, true, d.Started, fire, dist)
            : new BossMoveDecision(dir, d.Jump, false, false, false, fire, dist);
    }

    /// <summary>A platform 3..<see cref="MaxClimbTiles"/> rows above the player's feet, over its hitbox columns,
    /// with nothing solid in between: a full jump lands on it.</summary>
    public static bool PlatformAbove(ITileGrid g, in PlayerKinematics me)
    {
        int x0 = (int)MathF.Floor(me.X), x1 = (int)MathF.Floor(me.X + PlayerPhysics.HitboxWidth - 0.001f);
        int feet = (int)MathF.Floor(me.Y);
        for (int y = feet - 1; y >= feet - MaxClimbTiles; y--)
        {
            bool platform = true;
            for (int x = x0; x <= x1; x++)
            {
                var k = g.Get(x, y).Kind;
                if (k == TileKind.Solid) return false;
                if (k != TileKind.Platform) platform = false;
            }
            if (platform) return feet - y >= 3;
        }
        return false;
    }

    public const int MaxClimbTiles = 5;

    /// <summary>Brake (press the opposite key: -0.28 px/tick²) when the player would otherwise slide past an arena
    /// edge: in the air, letting go only slows by 0.1 px/tick², ~3 tiles from full speed. Returns the key to press, or 0.</summary>
    public static int BrakeFor(ArenaBounds a, in PlayerKinematics me)
    {
        const float brakeDecel = 0.28f / 16f, slack = 0.5f;
        float vx = me.VelX, stop = vx * vx / (2f * brakeDecel);
        if (vx < 0f && me.X - stop - slack < a.X1) return 1;
        if (vx > 0f && me.X + PlayerPhysics.HitboxWidth + stop + slack > a.X2 + 1) return -1;
        return 0;
    }
}
