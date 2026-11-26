using LlmPlayer.Core.Combat;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class ContactPredictorTests
{
    // Player standing on a floor whose top edge is row 10: feet row 9, hitbox left at x + 0.375.
    private static PlayerKinematics Standing(float x, float velX = 0f) => new(x + 0.375f, 9f, velX, 0f, true);
    private const float Floor = 10f;
    private static float PlayerCX(float x) => x + 1f;

    /// <summary>A 1.25 x 2.5 tile walker on the floor.</summary>
    private static EnemyMotion Walker(float cx, float vx) => new(cx, Floor - 1.25f, 0.6f, 1.25f, vx, 0f, Gravity: 0.3f / 16f);

    [Fact]
    public void ApproachingWalker_PredictsContact_WhenStandingStill()
    {
        var f = ContactPredictor.Forecast(Standing(10), Walker(PlayerCX(10) + 3f, -2f / 16f), new Maneuver(0, false), Floor,
            PlayerPhysics.Vanilla, 36);
        Assert.True(f.Contact);
        Assert.InRange(f.FirstContactTick!.Value, 10, 25);
    }

    [Fact]
    public void ApproachingWalker_RunningAwayAtFullSpeed_AvoidsContact()
    {
        var f = ContactPredictor.Forecast(Standing(10, velX: -3f / 16f), Walker(PlayerCX(10) + 3f, -1.5f / 16f),
            new Maneuver(-1, false), Floor, PlayerPhysics.Vanilla, 36);
        Assert.False(f.Contact);
    }

    [Fact]
    public void RecedingEnemy_NoContact()
    {
        var f = ContactPredictor.Forecast(Standing(10), Walker(PlayerCX(10) + 3f, 2f / 16f), new Maneuver(0, false), Floor,
            PlayerPhysics.Vanilla, 36);
        Assert.False(f.Contact);
        Assert.True(f.MinGap > 1f);
    }

    [Fact]
    public void AlreadyOverlapping_IsContactAtTickZero()
    {
        var f = ContactPredictor.Forecast(Standing(10), Walker(PlayerCX(10), 0f), new Maneuver(0, false), Floor, PlayerPhysics.Vanilla, 36);
        Assert.Equal(0, f.FirstContactTick);
    }

    [Fact]
    public void LowFlyerSkimmingTheFloor_IsJumpedOver()
    {
        // A small flyer gliding along at knee height straight at the player: standing gets hit, a jump clears it.
        var flyer = new EnemyMotion(PlayerCX(10) + 4f, Floor - 0.5f, 0.5f, 0.5f, -5f / 16f, 0f);
        var stand = ContactPredictor.Forecast(Standing(10), flyer, new Maneuver(0, false), Floor, PlayerPhysics.Vanilla, 36);
        var jump = ContactPredictor.Forecast(Standing(10), flyer, new Maneuver(0, true), Floor, PlayerPhysics.Vanilla, 36);
        Assert.True(stand.Contact);
        Assert.False(jump.Contact);
    }

    [Fact]
    public void FallingEnemy_DoesNotSinkThroughTheFloor()
    {
        // A slime dropping onto the floor 4 tiles beside the player must not be predicted to slide under it.
        var slime = new EnemyMotion(PlayerCX(10) + 4f, Floor - 6f, 1f, 0.75f, 0f, 2f / 16f, Gravity: 0.3f / 16f);
        var f = ContactPredictor.Forecast(Standing(10), slime, new Maneuver(0, false), Floor, PlayerPhysics.Vanilla, 60);
        Assert.False(f.Contact);
    }

    [Fact]
    public void FloorBelow_FindsPlatformOrGround()
    {
        var (g, _) = new GridBuilder(10, 20).Fill(0, 15, 9, 15, '#').Fill(0, 8, 9, 8, '=').Parse();
        Assert.Equal(8f, ContactPredictor.FloorBelow(g, new PlayerKinematics(3.375f, 5f, 0, 0.1f, false)));
        Assert.Equal(15f, ContactPredictor.FloorBelow(g, new PlayerKinematics(3.375f, 10f, 0, 0.1f, false)));
        Assert.Equal(float.PositiveInfinity, ContactPredictor.FloorBelow(g, new PlayerKinematics(3.375f, 16f, 0, 0.1f, false), 2));
    }

    [Theory]
    [InlineData(0f, 1, 0.005f)]
    [InlineData(0f, -1, -0.005f)]
    public void Run_AcceleratesTowardDir(float vx, int dir, float expected) =>
        Assert.Equal(expected, ContactPredictor.Run(vx, dir, true, 0.005f, 0.1875f, 0.0125f), 4);

    [Fact]
    public void Run_NoKeySlowsToStop()
    {
        Assert.Equal(0f, ContactPredictor.Run(0.01f, 0, true, 0.005f, 0.1875f, 0.0125f));
        Assert.Equal(0.1f - 0.0125f, ContactPredictor.Run(0.1f, 0, true, 0.005f, 0.1875f, 0.0125f), 4);
    }
}

public class EvasionTests
{
    private const float Floor = 10f;
    private static PlayerKinematics Standing(float x, float velX = 0f) => new(x + 0.375f, 9f, velX, 0f, true);
    private static EnemyMotion Walker(float cx, float vx) => new(cx, Floor - 1.25f, 0.6f, 1.25f, vx, 0f, Gravity: 0.3f / 16f);

    private static EvasionInputs In(PlayerKinematics me, EnemyMotion e, WeaponStyle style = WeaponStyle.Ranged, int planDir = 0,
        bool safeLeft = true, bool safeRight = true) =>
        new(me, e, style, planDir, false, Floor, safeLeft, safeRight);

    [Fact]
    public void NoThreat_FollowsPlan()
    {
        var d = new Evasion().Tick(In(Standing(10), Walker(40, 0f), planDir: 1));
        Assert.False(d.Evading);
        Assert.Equal(1, d.Dir);
        Assert.False(d.Started);
    }

    [Fact]
    public void ChargingEnemy_BacksAway()
    {
        // Enemy 3 tiles to the right charging left: run left.
        var d = new Evasion().Tick(In(Standing(10), Walker(14, -3f / 16f), planDir: 1));
        Assert.True(d.Evading);
        Assert.True(d.Started);
        Assert.Equal(-1, d.Dir);
    }

    [Fact]
    public void ChargingEnemy_UnsafeBehind_JumpsOverInstead()
    {
        // A hole to the left: can't back off, so jump (over a low flyer) rather than stand and take it.
        var flyer = new EnemyMotion(15f, Floor - 0.5f, 0.5f, 0.5f, -5f / 16f, 0f);
        var d = new Evasion().Tick(In(Standing(10), flyer, safeLeft: false));
        Assert.True(d.Evading);
        Assert.NotEqual(-1, d.Dir);
        Assert.True(d.Jump);
    }

    [Fact]
    public void NeverPicksAnUnsafeDirection()
    {
        var d = new Evasion().Tick(In(Standing(10), Walker(14, -3f / 16f), safeLeft: false, safeRight: false));
        Assert.Equal(0, d.Dir);
    }

    [Fact]
    public void UnsafePlanDirection_IsDroppedEvenWithoutThreat()
    {
        var d = new Evasion().Tick(In(Standing(10), Walker(40, 0f), planDir: 1, safeRight: false));
        Assert.Equal(0, d.Dir);
    }

    [Fact]
    public void Melee_StillClosesInWhenEnemyIsNotComing()
    {
        // Idle enemy 4 tiles away: melee plan (step toward) isn't overridden.
        var d = new Evasion().Tick(In(Standing(10), Walker(15, 0f), WeaponStyle.Melee, planDir: 1));
        Assert.False(d.Evading);
        Assert.Equal(1, d.Dir);
    }

    [Fact]
    public void Melee_LooksLessFarAheadThanRanged()
    {
        // A slow walker 3 tiles off: ranged already backs off, melee keeps its ground to swing.
        var e = Walker(14.5f, -1.2f / 16f);
        Assert.True(new Evasion().Tick(In(Standing(10), e, WeaponStyle.Ranged)).Evading);
        Assert.False(new Evasion().Tick(In(Standing(10), e, WeaponStyle.Melee)).Evading);
    }

    [Fact]
    public void Dodge_StaysOnForReleaseTicks_ThenReturnsToPlan()
    {
        var ev = new Evasion();
        Assert.True(ev.Tick(In(Standing(10), Walker(14, -3f / 16f))).Started);
        var calm = In(Standing(10), Walker(40, 0f), planDir: 1);
        for (int t = 1; t < Evasion.ReleaseTicks; t++)
        {
            var d = ev.Tick(calm);
            Assert.True(d.Evading);
            Assert.False(d.Started);
        }
        Assert.False(ev.Tick(calm).Evading);
        Assert.True(ev.Tick(In(Standing(10), Walker(14, -3f / 16f))).Started); // a new dodge counts again
    }
}

public class JumpPulseTests
{
    [Fact]
    public void PressesOnGround_HoldsWhileRising_ReleasesBeforeNextJump()
    {
        bool last = false;
        Assert.True(JumpPulse.Next(true, onGround: true, 0f, ref last));
        Assert.True(JumpPulse.Next(true, onGround: false, -0.3f, ref last));  // rising: hold
        Assert.False(JumpPulse.Next(true, onGround: false, 0.2f, ref last));  // falling: release
        Assert.False(JumpPulse.Next(true, onGround: false, 0.2f, ref last));
        Assert.True(JumpPulse.Next(true, onGround: true, 0f, ref last));      // landed: jump again
        Assert.False(JumpPulse.Next(true, onGround: true, 0f, ref last));     // still on ground while held: release first
    }

    [Fact]
    public void NotWanted_NeverPresses()
    {
        bool last = true;
        Assert.False(JumpPulse.Next(false, true, 0f, ref last));
        Assert.False(last);
    }
}

public class RetreatTests
{
    [Theory]
    [InlineData(25, 100, 0.3f, false, true)]
    [InlineData(25, 100, 0.3f, true, false)]   // can heal: keep fighting (quick-heal handles it)
    [InlineData(35, 100, 0.3f, false, false)]  // above threshold
    [InlineData(0, 100, 0.3f, false, false)]   // dead: nothing to do
    [InlineData(10, 100, 0f, false, false)]    // disabled
    public void ShouldRetreat(int life, int max, float threshold, bool canHeal, bool expected) =>
        Assert.Equal(expected, Retreat.ShouldRetreat(life, max, threshold, canHeal));

    [Fact]
    public void Direction_AwayFromNearerThreats()
    {
        Assert.Equal(-1, Retreat.Direction(new[] { 3f }, true, true));
        Assert.Equal(1, Retreat.Direction(new[] { -3f }, true, true));
        // A close one on the right outweighs a far one on the left.
        Assert.Equal(-1, Retreat.Direction(new[] { 2f, -20f }, true, true));
    }

    [Fact]
    public void Direction_RespectsSafety()
    {
        Assert.Equal(1, Retreat.Direction(new[] { 3f }, safeLeft: false, safeRight: true));
        Assert.Equal(0, Retreat.Direction(new[] { 3f }, false, false));
        Assert.Equal(1, Retreat.Direction(Array.Empty<float>(), true, true));
    }
}

public class AttackStatsTests
{
    [Fact]
    public void CountsOnlyLifeLost_AndKills()
    {
        var s = new AttackStats(startTick: 100);
        s.ObserveLife(100);
        s.ObserveLife(90);
        s.ObserveLife(120); // healed
        s.ObserveLife(100);
        s.AddKill("Zombie");
        s.AddKill("Demon Eye");
        s.AddDodge();
        var r = s.Result(nowTick: 100 + 150);
        Assert.Equal(30, r.DamageTaken);
        Assert.Equal(2, r.Killed);
        Assert.Equal(new[] { "Zombie", "Demon Eye" }, r.Kills);
        Assert.Equal(2.5f, r.DurationS);
        Assert.Equal(1, r.Dodges);
        Assert.False(r.Retreated);
    }
}
