using LlmPlayer.Core.Combat;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;
using Xunit.Abstractions;

namespace LlmPlayer.Core.Tests.Sim;

/// <summary>
/// The attack's movement (CombatStance plan → Evasion → JumpPulse, as AttackTask wires it) driving the vanilla-physics
/// sim against scripted enemies, compared with the plan alone (the old stand-and-trade behaviour). Hits on the player
/// count with vanilla's 40-tick immunity.
/// </summary>
public class EvasionSimTests(ITestOutputHelper output)
{
    private const int Ground = 20, ImmunityTicks = 40;

    private static ArrayTileGrid Arena() =>
        new GridBuilder(100, 26).Fill(0, Ground, 99, 25, '#').Fill(0, 0, 0, 25, 'X').Fill(99, 0, 99, 25, 'X').Parse().Grid;

    private sealed class Enemy
    {
        public float X, Y, VX, VY; // centre, px
        public float HalfW, HalfH;
        public EnemyMotion Motion(float gravityPx) =>
            new(X / 16f, Y / 16f, HalfW / 16f, HalfH / 16f, VX / 16f, VY / 16f, gravityPx / 16f);
    }

    private sealed record Outcome(int HitsTaken, int Swings, int Ticks, bool Killed, int Contacts);

    /// <summary>One tick of the attack's movement. Returns the intent and whether it would fire.</summary>
    private static (ControlIntent Intent, bool Fire) Drive(MiniPhysics sim, ArrayTileGrid g, Enemy e, float gravityPx,
        WeaponStyle style, float reach, Evasion? ev, ref bool lastJump, ref int dodges)
    {
        var k = sim.Kinematics;
        float pcx = sim.X + MiniPhysics.W / 2f, pcy = sim.Y + MiniPhysics.H / 2f;
        float nx = Math.Clamp(pcx, e.X - e.HalfW, e.X + e.HalfW), ny = Math.Clamp(pcy, e.Y - e.HalfH, e.Y + e.HalfH);
        float dx = (nx - pcx) / 16f, dy = (ny - pcy) / 16f;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        var stance = CombatStance.Decide(style, dx, dy, reach, true);
        int plan = Evasion.PlanDir(stance, dx, 1);
        var stand = NavMath.StandingTile(k);
        bool safeL = StepSafety.IsSafe(g, stand, -1), safeR = StepSafety.IsSafe(g, stand, 1);
        int dir = plan;
        bool jumpWant = false;
        if (ev != null)
        {
            var d = ev.Tick(new EvasionInputs(k, e.Motion(gravityPx), style, plan, false, ContactPredictor.FloorBelow(g, k), safeL, safeR));
            if (d.Started) dodges++;
            dir = d.Dir;
            jumpWant = d.Jump;
        }
        bool blocked = k.OnGround && dir != 0 && Math.Abs(k.VelX) < 0.005f && sim.Ticks > 0;
        bool jump = JumpPulse.Next(jumpWant || blocked, k.OnGround, k.VelY, ref lastJump);
        var intent = new ControlIntent(dir < 0, dir > 0, jump, false, false, null);
        return (intent, CombatStance.ShouldFire(style, dist, reach, true));
    }

    private static bool Touching(MiniPhysics sim, Enemy e, float margin = 0f) =>
        sim.X - margin < e.X + e.HalfW && sim.X + MiniPhysics.W + margin > e.X - e.HalfW
        && sim.Y - margin < e.Y + e.HalfH && sim.Y + MiniPhysics.H + margin > e.Y - e.HalfH;

    /// <summary>A zombie-like walker (18x40 px, up to 1.5 px/tick toward the player, lunging at 2.5 when close) vs a short sword (reach 2.5).
    /// Every swing that connects barely knocks it back and 8 hits kill it.</summary>
    private Outcome MeleeVsWalker(bool evade, int startOffsetTiles)
    {
        var g = Arena();
        var sim = new MiniPhysics(g, new TilePoint(50, Ground - 1));
        var e = new Enemy { X = (51 + startOffsetTiles) * 16f, Y = Ground * 16f - 20f, HalfW = 9f, HalfH = 20f };
        var ev = evade ? new Evasion() : null;
        bool lastJump = false;
        int dodges = 0, hits = 0, swings = 0, immune = 0, swingCd = 0, enemyHits = 0;
        for (int t = 0; t < 1200; t++)
        {
            var (intent, fire) = Drive(sim, g, e, 0.3f, WeaponStyle.Melee, 2.5f, ev, ref lastJump, ref dodges);
            sim.Step(intent);

            float pcx = sim.X + MiniPhysics.W / 2f;
            int toward = Math.Sign(pcx - e.X);
            // Walker: accelerate toward the player, capped.
            float cap = Math.Abs(pcx - e.X) < 4 * 16f ? 2.5f : 1.5f; // lunges when close
            e.VX = Math.Clamp(e.VX + 0.1f * toward, -cap, cap);
            e.X = Math.Clamp(e.X + e.VX, 2 * 16f, 97 * 16f);

            if (swingCd > 0) swingCd--;
            float gapX = Math.Abs(pcx - e.X) - e.HalfW;
            if (fire && swingCd == 0 && gapX / 16f <= 2.5f)
            {
                swingCd = 25;
                swings++;
                if (++enemyHits >= 8) return new Outcome(hits, swings, t, true, 0);
                e.VX = -toward * 0.5f; // knockback-resistant
            }
            if (immune > 0) immune--;
            else if (Touching(sim, e))
            {
                hits++;
                immune = ImmunityTicks;
            }
        }
        return new Outcome(hits, swings, 1200, false, 0);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(-10)]
    [InlineData(4)]
    public void Melee_HitAndRun_TakesFewerHitsThanTrading(int offset)
    {
        var naive = MeleeVsWalker(evade: false, offset);
        var smart = MeleeVsWalker(evade: true, offset);
        output.WriteLine($"naive {naive}\nsmart {smart}");
        Assert.True(smart.Killed, "evading melee still kills the walker");
        Assert.True(smart.HitsTaken < naive.HitsTaken || smart.HitsTaken == 0, "evasion takes fewer hits than trading");
        Assert.True(smart.HitsTaken <= 1, $"took {smart.HitsTaken} hits");
    }

    /// <summary>A demon-eye-like flyer (30x32 px): hovers 8 tiles up and to one side, then dashes at the player at
    /// 6 px/tick, passing through tiles. The player has a gun (keeps 8–15 tiles). Counts dashes that touch the player.</summary>
    private Outcome RangedVsDasher(bool evade, int seed)
    {
        var rng = new Random(seed);
        var g = Arena();
        var sim = new MiniPhysics(g, new TilePoint(50, Ground - 1));
        var e = new Enemy { X = 60 * 16f, Y = (Ground - 9) * 16f, HalfW = 15f, HalfH = 16f };
        var ev = evade ? new Evasion() : null;
        bool lastJump = false;
        int dodges = 0, contacts = 0, dashes = 0, ticks = 0;
        const int cycles = 10;
        for (int c = 0; c < cycles; c++)
        {
            int side = rng.Next(2) == 0 ? -1 : 1;
            // Hover toward a point beside and above the player.
            for (int t = 0; t < 70; t++, ticks++)
            {
                var (intent, _) = Drive(sim, g, e, 0f, WeaponStyle.Ranged, 15f, ev, ref lastJump, ref dodges);
                sim.Step(intent);
                float tx = sim.X + side * 9 * 16f, ty = sim.Y - 7 * 16f;
                float hx = tx - e.X, hy = ty - e.Y, hl = MathF.Max(1f, MathF.Sqrt(hx * hx + hy * hy));
                e.VX = hx / hl * MathF.Min(3f, hl);
                e.VY = hy / hl * MathF.Min(3f, hl);
                e.X += e.VX;
                e.Y += e.VY;
            }
            // Dash at the player's current centre.
            dashes++;
            float px = sim.X + MiniPhysics.W / 2f, py = sim.Y + MiniPhysics.H / 2f;
            float dx = px - e.X, dy = py - e.Y, l = MathF.Sqrt(dx * dx + dy * dy);
            e.VX = dx / l * 6f;
            e.VY = dy / l * 6f;
            bool hit = false;
            for (int t = 0; t < 50; t++, ticks++)
            {
                var (intent, _) = Drive(sim, g, e, 0f, WeaponStyle.Ranged, 15f, ev, ref lastJump, ref dodges);
                sim.Step(intent);
                e.X += e.VX;
                e.Y += e.VY; // flies through tiles, like the Eye of Cthulhu
                if (Touching(sim, e)) hit = true;
            }
            if (hit) contacts++;
        }
        Assert.Null(sim.Hurt);
        return new Outcome(contacts, 0, ticks, false, dashes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Ranged_DodgesMostDashes(int seed)
    {
        var naive = RangedVsDasher(evade: false, seed);
        var smart = RangedVsDasher(evade: true, seed);
        output.WriteLine($"naive {naive}\nsmart {smart}");
        Assert.True(smart.HitsTaken <= 3, $"{smart.HitsTaken}/{smart.Contacts} dashes connected");
        Assert.True(smart.HitsTaken < naive.HitsTaken, "evasion beats standing in the band");
    }
}
