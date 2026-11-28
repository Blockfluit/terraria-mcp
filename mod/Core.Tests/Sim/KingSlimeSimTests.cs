using LlmPlayer.Core.Combat;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;
using Xunit.Abstractions;

namespace LlmPlayer.Core.Tests.Sim;

/// <summary>
/// The boss fight movement against a King-Slime-like hopper (<see cref="KingSlimeSim"/>) in the vanilla-physics sim,
/// as AttackTask wires it (BossMovement + MinionFocus, bow, kite with keep_distance 10), on flat ground with a
/// 55-wide arena like the live fight that killed the player. The player has 140 life and 17 defense (Classic: King
/// Slime hits for 40 - 8.5 ≈ 32, its Blue Slimes for 1); hits count with vanilla's 40-tick immunity; no healing.
/// Compared with the movement before the jumper dodge (and minions shot first).
/// </summary>
public class KingSlimeSimTests(ITestOutputHelper output)
{
    private const int Ground = 40, ImmunityTicks = 40, MaxLife = 140, Defense = 17;
    private const int ArenaX1 = 70, ArenaX2 = 124; // 55 columns

    private static ArrayTileGrid Flat() =>
        new GridBuilder(200, 46).Fill(0, Ground, 199, 45, '#').Fill(0, 0, 0, 45, 'X').Fill(199, 0, 199, 45, 'X').Parse().Grid;

    public enum Mode { New, Old, StandStill }

    public sealed record Outcome(int Hits, int Damage, int SurvivedTicks, bool Died, int BossHits, int MinionShots,
        int Jumps, int CorneredTicks, int CrossingTicks, bool LeftArena, int BossLifeLeft);

    private static int Hurt(int contact) => Math.Max(1, (int)MathF.Round(contact - Defense * 0.5f, MidpointRounding.AwayFromZero));

    private Outcome Fight(Mode mode, int startX = 97, int bossOffsetTiles = 18, int ticks = 2400, bool killMinions = false)
    {
        var g = Flat();
        var arena = new ArenaBounds(ArenaX1, ArenaX2);
        var sim = new MiniPhysics(g, new TilePoint(startX, Ground - 1));
        var ks = new KingSlimeSim((startX + 1 + bossOffsetTiles) * 16f, Ground * 16f);
        var bow = new SimBow();
        var move = new BossMovement(jumperAware: mode == Mode.New);
        var focus = new MinionFocus(adjacentOnly: mode == Mode.New && !killMinions);
        var hitsBy = new Dictionary<int, int>();
        bool lastJump = false, leftArena = false;
        int hits = 0, damage = 0, immune = 0, survived = ticks, minionShots = 0, cornered = 0, crossing = 0;

        for (int t = 0; t < ticks; t++)
        {
            var k = sim.Kinematics;
            float pcx = sim.X + MiniPhysics.W / 2f, pcy = sim.Y + MiniPhysics.H / 2f;
            ControlIntent intent = default;
            bool fireBoss = true;
            if (mode != Mode.StandStill)
            {
                var others = ks.Slimes.Select(s => KingSlimeSim.MotionOf(s.Body)).ToList();
                var d = move.Tick(new BossMoveInputs(k, g, arena, ks.Motion, others, WeaponStyle.Ranged,
                    CombatStance.RangedMaxTiles, CanSeeBoss: !ks.Hidden, LineOfSight: true, AttackStrategy.Kite, KeepTiles: 10f));
                if (d.Cornered) cornered++;
                if (d.Crossing) crossing++;
                bool blocked = k.OnGround && MathF.Abs(k.VelX) < 0.005f && StepSafety.WallAhead(g, NavMath.StandingTile(k), d.Dir);
                bool jump = JumpPulse.Next(d.Jump || blocked, k.OnGround, k.VelY, ref lastJump);
                if (d.CutJump && !k.OnGround) jump = lastJump = false;
                intent = new ControlIntent(d.Dir < 0, d.Dir > 0, jump, d.Drop, false, null);
                fireBoss = d.Fire;
            }
            int? minion = focus.Tick(ks.Slimes.Select(s => Contender(s.Body, pcx, pcy)));
            var target = minion is { } mid ? ks.Slimes.First(s => s.Body.Id == mid).Body : fireBoss && !ks.Hidden ? ks.Body : null;
            if (target != null)
            {
                int before = bow.Shots;
                bow.Fire(pcx, pcy, target);
                if (bow.Shots > before && target != ks.Body) minionShots++;
            }

            sim.Step(intent);
            pcx = sim.X + MiniPhysics.W / 2f;
            pcy = sim.Y + MiniPhysics.H / 2f;
            ks.Step(pcx, pcy, sim.Y + MiniPhysics.H);
            bow.Step(ks.Slimes.Select(s => s.Body).Append(ks.Body), hitsBy);
            ks.Slimes.RemoveAll(s => s.Body.Life <= 0);
            if (sim.X < ArenaX1 * 16f - 8f || sim.X + MiniPhysics.W > (ArenaX2 + 1) * 16f + 8f) leftArena = true;

            if (immune > 0) immune--;
            else
            {
                var touching = ks.Slimes.Select(s => s.Body).Prepend(ks.Body)
                    .FirstOrDefault(f => f.Touches(sim.X, sim.Y, MiniPhysics.W, MiniPhysics.H));
                if (touching != null)
                {
                    hits++;
                    damage += Hurt(touching.Contact);
                    immune = ImmunityTicks;
                    if (damage >= MaxLife)
                    {
                        survived = t;
                        break;
                    }
                }
            }
            if (ks.Body.Life <= 0) break;
        }
        return new Outcome(hits, damage, survived, damage >= MaxLife, hitsBy.GetValueOrDefault(0), minionShots, ks.Jumps,
            cornered, crossing, leftArena, ks.Body.Life);
    }

    private static Contender Contender(SimFlyer s, float pcx, float pcy)
    {
        float nx = Math.Clamp(pcx, s.X - s.HalfW, s.X + s.HalfW), ny = Math.Clamp(pcy, s.Y - s.HalfH, s.Y + s.HalfH);
        return new Contender(s.Id, (nx - pcx) / 16f, (ny - pcy) / 16f, s.VX / 16f, s.VY / 16f);
    }

    [Theory]
    [InlineData(97, 18)]   // middle, boss to the right
    [InlineData(97, -18)]  // middle, boss to the left
    [InlineData(76, 14)]   // near the left end, boss coming from the open side: cornered at once
    [InlineData(115, -10)] // near the right end
    [InlineData(84, 30)]
    public void JumperDodge_SurvivesFortySeconds_InA55WideArena(int startX, int bossOffset)
    {
        var still = Fight(Mode.StandStill, startX, bossOffset);
        var old = Fight(Mode.Old, startX, bossOffset);
        var now = Fight(Mode.New, startX, bossOffset);
        output.WriteLine($"still {still}\nold   {old}\nnew   {now}");
        Assert.False(now.Died, $"survives 40 s: {now}");
        Assert.True(now.Damage <= 3 * Hurt(KingSlimeSim.Contact) + 10, $"at most 3 King Slime hits: {now.Damage}");
        Assert.False(now.LeftArena, "stays in the arena");
        Assert.True(now.Damage < old.Damage, $"new {now.Damage} vs old {old.Damage}");
        Assert.True(now.BossHits >= old.BossHits, $"keeps shooting the boss: {now.BossHits} arrows hit (old {old.BossHits})");
    }

    /// <summary>Many starts (player 72..120, boss 10 or 20 tiles either side), a minute each without healing (the
    /// boss jumps faster as it loses life): far fewer deaths than the old movement (2 of 33 vs 24 when written), and
    /// less than half its damage on average (52 vs 137).</summary>
    [Fact]
    public void Sweep_NeverDies_AndTakesFarLessDamage()
    {
        int deaths = 0, oldDeaths = 0, dmg = 0, oldDmg = 0, n = 0, worst = 0;
        for (int x = 72; x <= 120; x += 6)
            foreach (int off in new[] { -20, -10, 10, 20 })
            {
                if (x + off < 60 || x + off > 135) continue;
                var now = Fight(Mode.New, x, off, ticks: 3600);
                var old = Fight(Mode.Old, x, off, ticks: 3600);
                n++;
                if (now.Died) deaths++;
                if (old.Died) oldDeaths++;
                dmg += now.Damage;
                oldDmg += old.Damage;
                worst = Math.Max(worst, now.Damage);
                if (now.Died) output.WriteLine($"died: x={x} off={off} {now}");
            }
        output.WriteLine($"{n} fights: new deaths {deaths}, mean damage {dmg / n}, worst {worst}; old deaths {oldDeaths}, mean damage {oldDmg / n}");
        Assert.True(deaths * 8 <= oldDeaths, $"deaths {deaths} vs old {oldDeaths}");
        Assert.True(dmg * 2 <= oldDmg, $"mean damage {dmg / n} vs old {oldDmg / n}");
    }

    [Fact]
    public void OldMovement_DiesToTheJumps_LikeTheLiveFight()
    {
        var old = Fight(Mode.Old);
        output.WriteLine($"old {old}");
        Assert.True(old.Died, $"the movement before the jumper dodge dies within 40 s: {old}");
    }

    [Fact]
    public void Cornered_CrossesUnderTheBoss_AndReportsIt()
    {
        var now = Fight(Mode.New, startX: 76, bossOffsetTiles: 14);
        output.WriteLine($"new {now}");
        Assert.True(now.CorneredTicks > 0, "the progress can say cornered");
        Assert.True(now.CrossingTicks > 0, "ran under it to the open side");
    }

    [Fact]
    public void BossFirst_MinionsOnlyWhenAdjacent()
    {
        var bossFirst = Fight(Mode.New);
        var killAll = Fight(Mode.New, killMinions: true);
        output.WriteLine($"boss first {bossFirst}\nkill all   {killAll}");
        Assert.True(bossFirst.BossHits > killAll.BossHits, "more arrows into the boss when minions wait");
        Assert.True(bossFirst.MinionShots < killAll.MinionShots);
    }
}
