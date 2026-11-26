using LlmPlayer.Core.Combat;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;
using Xunit.Abstractions;

namespace LlmPlayer.Core.Tests.Sim;

/// <summary>
/// The boss fight movement (<see cref="BossMovement"/> + <see cref="MinionFocus"/>, as AttackTask wires them) in the
/// vanilla-physics sim against an Eye-of-Cthulhu-like dasher (<see cref="EyeDasher"/>) with servants, on a platform
/// arena (ground + 2 platform rows, x 20..100, lava pits just outside). Compared with standing still and shooting,
/// and with the old non-boss movement (stand in the ranged band + evasion). Hits count with vanilla's 40-tick
/// immunity.
/// </summary>
public class BossSimTests(ITestOutputHelper output)
{
    private const int Ground = 40, Row1 = 35, Row2 = 30, ArenaX1 = 20, ArenaX2 = 100, ImmunityTicks = 40;
    private static readonly ArenaBounds Arena = new(ArenaX1, ArenaX2);

    /// <summary>Ground at y=40 (lava pits at x 14..19 and 101..106), platform rows at y=35 and y=30 over the arena.</summary>
    public static ArrayTileGrid PlatformArena(int rows = 2)
    {
        var b = new GridBuilder(140, 46)
            .Fill(0, Ground, 139, 45, '#')
            .Fill(14, Ground, 19, Ground, 'L').Fill(101, Ground, 106, Ground, 'L')
            .Fill(0, 0, 0, 45, 'X').Fill(139, 0, 139, 45, 'X');
        if (rows >= 1) b.Fill(ArenaX1, Row1, ArenaX2, Row1, '=');
        if (rows >= 2) b.Fill(ArenaX1, Row2, ArenaX2, Row2, '=');
        return b.Parse().Grid;
    }

    public enum Strategy { StandStill, OldEvasion, Kite }

    public sealed record Outcome(int Hits, int Damage, int Dodges, int Dashes, int BossHits, int ServantsKilled,
        int ServantsSpawned, bool LeftArena, string? Hurt, int RowChanges);

    private Outcome Fight(Strategy strategy, int ticks = 3600, int startX = 60, int rows = 2, float dash1 = 10f, float dash2 = 11f)
    {
        var g = PlatformArena(rows);
        var sim = new MiniPhysics(g, new TilePoint(startX, Ground - 1));
        var boss = new EyeDasher(startX * 16f + 200f, (Ground - 20) * 16f) { Dash1Speed = dash1, Dash2Speed = dash2 };
        var bow = new SimBow();
        var move = new BossMovement();
        var oldEvasion = new Evasion();
        var focus = new MinionFocus();
        var arrowHits = new Dictionary<int, int>();
        bool lastJump = false, leftArena = false;
        int hits = 0, damage = 0, dodges = 0, immune = 0, servantsKilled = 0, rowChanges = 0;
        int lastFloor = Ground;

        for (int t = 0; t < ticks; t++)
        {
            var k = sim.Kinematics;
            float pcx = sim.X + MiniPhysics.W / 2f, pcy = sim.Y + MiniPhysics.H / 2f;
            var others = boss.Servants.Select(s => s.Motion).ToList();
            ControlIntent intent = default;
            bool fireBoss;

            int? minion = focus.Tick(boss.Servants.Select(s => Contender(s, pcx, pcy)));
            if (strategy == Strategy.Kite)
            {
                var d = move.Tick(new BossMoveInputs(k, g, Arena, boss.Eye.Motion, others, WeaponStyle.Ranged,
                    CombatStance.RangedMaxTiles, CanSeeBoss: true, LineOfSight: true));
                if (d.DodgeStarted) dodges++;
                bool blocked = k.OnGround && d.Dir != 0 && MathF.Abs(k.VelX) < 0.005f && t > 0;
                intent = new ControlIntent(d.Dir < 0, d.Dir > 0, JumpPulse.Next(d.Jump || blocked, k.OnGround, k.VelY, ref lastJump),
                    d.Drop, false, null);
                fireBoss = d.Fire;
            }
            else if (strategy == Strategy.OldEvasion)
            {
                var e = boss.Eye;
                float nx = Math.Clamp(pcx, e.X - e.HalfW, e.X + e.HalfW), ny = Math.Clamp(pcy, e.Y - e.HalfH, e.Y + e.HalfH);
                float dx = (nx - pcx) / 16f, dy = (ny - pcy) / 16f;
                var stance = CombatStance.Decide(WeaponStyle.Ranged, dx, dy, CombatStance.RangedMaxTiles, true);
                int plan = stance == StanceMove.PathToward ? 0 : Evasion.PlanDir(stance, dx, 1);
                var stand = NavMath.StandingTile(k);
                var d = oldEvasion.Tick(new EvasionInputs(k, e.Motion, WeaponStyle.Ranged, plan, false,
                    ContactPredictor.FloorBelow(g, k), StepSafety.IsSafe(g, stand, -1), StepSafety.IsSafe(g, stand, 1)));
                if (d.Started) dodges++;
                bool blocked = k.OnGround && d.Dir != 0 && MathF.Abs(k.VelX) < 0.005f && t > 0;
                intent = new ControlIntent(d.Dir < 0, d.Dir > 0, JumpPulse.Next(d.Jump || blocked, k.OnGround, k.VelY, ref lastJump),
                    false, false, null);
                fireBoss = MathF.Sqrt(dx * dx + dy * dy) <= CombatStance.RangedMaxTiles + CombatStance.RangedFireSlackTiles;
            }
            else fireBoss = true;

            // Shoot the focused servant first, else the boss.
            var target = minion is { } mid ? boss.Servants.First(s => s.Id == mid) : fireBoss ? boss.Eye : null;
            if (target != null) bow.Fire(pcx, pcy, target);

            sim.Step(intent);
            pcx = sim.X + MiniPhysics.W / 2f;
            pcy = sim.Y + MiniPhysics.H / 2f;
            boss.Step(pcx, pcy);
            bow.Step(boss.Servants.Append(boss.Eye), arrowHits);
            servantsKilled += boss.Servants.RemoveAll(s => s.Life <= 0);

            if (sim.X < ArenaX1 * 16f || sim.X + MiniPhysics.W > (ArenaX2 + 1) * 16f) leftArena = true;
            var kin = sim.Kinematics;
            if (kin.OnGround)
            {
                int floor = (int)MathF.Floor(kin.Y) + 1;
                if (floor != lastFloor) rowChanges++;
                lastFloor = floor;
            }

            if (immune > 0) immune--;
            else
            {
                var touching = boss.Servants.Prepend(boss.Eye).FirstOrDefault(f => f.Touches(sim.X, sim.Y, MiniPhysics.W, MiniPhysics.H));
                if (touching != null)
                {
                    hits++;
                    damage += touching.Contact;
                    immune = ImmunityTicks;
                }
            }
        }
        return new Outcome(hits, damage, dodges, boss.DashesStarted, arrowHits.GetValueOrDefault(0), servantsKilled,
            boss.ServantsSpawned, leftArena, sim.Hurt, rowChanges);
    }

    private static Contender Contender(SimFlyer s, float pcx, float pcy)
    {
        float nx = Math.Clamp(pcx, s.X - s.HalfW, s.X + s.HalfW), ny = Math.Clamp(pcy, s.Y - s.HalfH, s.Y + s.HalfH);
        return new Contender(s.Id, (nx - pcx) / 16f, (ny - pcy) / 16f, s.VX / 16f, s.VY / 16f);
    }

    /// <summary>The dash speeds the task description asks for (~10 px/tick, faster in phase 2), on the 3-level arena.</summary>
    [Theory]
    [InlineData(60)]
    [InlineData(30)]
    [InlineData(90)]
    public void FastDashes_KitingTakesFarFewerHits_AndStaysInTheArena(int startX)
    {
        var still = Fight(Strategy.StandStill, startX: startX);
        var old = Fight(Strategy.OldEvasion, startX: startX);
        var kite = Fight(Strategy.Kite, startX: startX);
        output.WriteLine($"still {still}\nold   {old}\nkite  {kite}");
        AssertKiteWorks(kite, still, maxRatio: 0.65);
        Assert.True(kite.Damage < old.Damage, $"kiting {kite.Damage} beats the non-boss movement {old.Damage}");
    }

    /// <summary>Vanilla normal-mode charge speeds (about 6 px/tick, 6.8 in phase 2).</summary>
    [Theory]
    [InlineData(60)]
    [InlineData(30)]
    [InlineData(90)]
    public void VanillaSpeedDashes_KitingTakesFewerHits(int startX)
    {
        var still = Fight(Strategy.StandStill, startX: startX, dash1: 6f, dash2: 6.8f);
        var kite = Fight(Strategy.Kite, startX: startX, dash1: 6f, dash2: 6.8f);
        output.WriteLine($"still {still}\nkite  {kite}");
        AssertKiteWorks(kite, still, maxRatio: 0.8);
    }

    [Fact]
    public void FlatGround_KitingStillBeatsStandingStill()
    {
        var still = Fight(Strategy.StandStill, rows: 0);
        var kite = Fight(Strategy.Kite, rows: 0);
        output.WriteLine($"still {still}\nkite  {kite}");
        AssertKiteWorks(kite, still, maxRatio: 0.8);
    }

    [Fact]
    public void Kiting_UsesThePlatformRows()
    {
        var kite = Fight(Strategy.Kite);
        output.WriteLine($"kite {kite}");
        Assert.True(kite.RowChanges >= 10, $"jumps up to / drops through the platform rows to dodge: {kite.RowChanges}");
    }

    private static void AssertKiteWorks(Outcome kite, Outcome still, double maxRatio)
    {
        Assert.False(kite.LeftArena, "kiting never leaves the arena");
        Assert.Null(kite.Hurt);
        Assert.True(kite.Hits <= still.Hits * maxRatio, $"kite {kite.Hits} hits vs standing still {still.Hits}");
        Assert.True(kite.Damage <= still.Damage * maxRatio, $"kite {kite.Damage} damage vs standing still {still.Damage}");
        Assert.True(kite.BossHits >= still.BossHits * 0.7, $"kept shooting the boss: {kite.BossHits} arrows hit (still {still.BossHits})");
        Assert.True(kite.ServantsKilled >= kite.ServantsSpawned * 3 / 4, $"shot servants: {kite.ServantsKilled}/{kite.ServantsSpawned}");
    }

    [Fact]
    public void Kiting_InANarrowArena_NeverLeavesIt()
    {
        var g = PlatformArena();
        var arena = new ArenaBounds(50, 70);
        var sim = new MiniPhysics(g, new TilePoint(60, Ground - 1));
        var boss = new EyeDasher(70 * 16f, (Ground - 20) * 16f);
        var move = new BossMovement();
        bool lastJump = false;
        for (int t = 0; t < 2400; t++)
        {
            var k = sim.Kinematics;
            var d = move.Tick(new BossMoveInputs(k, g, arena, boss.Eye.Motion, boss.Servants.Select(s => s.Motion).ToList(),
                WeaponStyle.Ranged, 15f, true, true));
            sim.Step(new ControlIntent(d.Dir < 0, d.Dir > 0, JumpPulse.Next(d.Jump, k.OnGround, k.VelY, ref lastJump), d.Drop, false, null));
            boss.Step(sim.X + MiniPhysics.W / 2f, sim.Y + MiniPhysics.H / 2f);
            Assert.True(sim.X >= 50 * 16f && sim.X + MiniPhysics.W <= 71 * 16f, $"left the arena at tick {t}: {sim}");
        }
        Assert.Null(sim.Hurt);
    }
}
