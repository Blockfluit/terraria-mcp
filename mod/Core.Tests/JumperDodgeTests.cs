using LlmPlayer.Core.Combat;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;

namespace LlmPlayer.Core.Tests;

public class BallisticTests
{
    // A King-Slime-sized body (7.6 x 7.2 tiles) resting on a floor at y=40.
    private static EnemyMotion Slime(float cx, float bottom, float vx = 0f, float vy = 0f) =>
        new(cx, bottom - 3.6f, 3.8f, 3.6f, vx, vy, 0.3f / 16f, 10f / 16f, 0.8f);

    [Fact]
    public void Grounded_OnlyAtRestOnATileEdge()
    {
        Assert.True(Ballistic.Grounded(Slime(50f, 40f)));
        Assert.False(Ballistic.Grounded(Slime(50f, 40f, vy: -8f / 16f)));
        Assert.False(Ballistic.Grounded(Slime(50f, 33.4f))); // top of its arc: no vertical speed, mid-tile
    }

    [Fact]
    public void Landing_FollowsTheArc()
    {
        // Small jump: -8 px/tick up, 4 px/tick sideways, gravity 0.3: ~53 ticks in the air, ~13 tiles on.
        var l = Ballistic.Landing(Slime(50f, 40f, -4f / 16f, -8f / 16f), 40f);
        Assert.NotNull(l);
        Assert.InRange(l!.Value.Ticks, 50, 56);
        Assert.InRange(l.Value.X, 50f - 14f, 50f - 12f);
        Assert.Null(Ballistic.Landing(Slime(50f, 40f), 30f)); // below that floor
        Assert.Null(Ballistic.Landing(new EnemyMotion(50f, 20f, 3f, 3f, 0f, 0.5f), 40f)); // a flyer
    }

    [Fact]
    public void Prediction_StopsAGroundedSlimeSliding()
    {
        var me = new PlayerKinematics(40f, 39f, 0f, 0f, true);
        var sliding = Slime(52f, 40f, -4f / 16f); // 4 px/tick toward the player, on the ground
        var keeps = sliding with { GroundFriction = 1f };
        Assert.True(ContactPredictor.Forecast(me, keeps, new Maneuver(0, false), 40f, PlayerPhysics.Vanilla, 60).Contact);
        Assert.False(ContactPredictor.Forecast(me, sliding, new Maneuver(0, false), 40f, PlayerPhysics.Vanilla, 60).Contact);
    }

    [Fact]
    public void Trace_StopsThePlayerAtTheArenaEnd()
    {
        var me = new PlayerKinematics(12f, 39f, -3f / 16f, 0f, true);
        var far = new EnemyMotion(80f, 30f, 1f, 1f, 0f, 0f);
        var tr = ContactPredictor.Trace(me, far, new Maneuver(-1, false), 40f, PlayerPhysics.Vanilla, 120, minLeft: 10f);
        Assert.Equal(10f + PlayerPhysics.HitboxWidth / 2f, tr.PlayerCX, 3);
        Assert.Equal(0f, tr.PlayerVX);
    }
}

public class HopPatternTests
{
    private static readonly Hop S = HopPattern.Small, M = HopPattern.Medium, B = HopPattern.Big;

    [Fact]
    public void AFreshFight_ExpectsKingSlimesCycle()
    {
        var p = new HopPattern();
        Assert.True(p.Next().Single().Like(S));
        p.Observe(S);
        Assert.True(p.Next().Single().Like(S));
        p.Observe(S);
        Assert.True(p.Next().Single().Like(M));
        Assert.True(p.Next(1).Single().Like(B));
        p.Observe(M);
        Assert.True(p.Next().Single().Like(B));
        p.Observe(B);
        Assert.True(p.Next().Single().Like(S));
    }

    [Fact]
    public void Slightly_Different_LaunchSpeeds_AreTheSameKind()
    {
        Assert.True(new Hop(4f / 16f, -7.7f / 16f).Like(S));
        Assert.False(M.Like(S));
    }

    [Fact]
    public void AnUnknownRhythm_IsLearnt_AndUnsureMeansEveryKind()
    {
        var x = new Hop(2f / 16f, -10f / 16f);
        var p = new HopPattern(new[] { x }); // a boss that only ever does x
        Assert.True(p.Next().Single().Like(x));
        p.Observe(S); // something new
        Assert.Equal(2, p.Next().Count);
        p.Observe(x);
        p.Observe(S);
        p.Observe(x);
        Assert.True(p.Next().Single().Like(S)); // x, S alternate now
    }
}

public class StepWallTests
{
    [Fact]
    public void WallAhead_OnlyForASolidTileInBodyHeight()
    {
        var g = ArrayTileGrid.Parse(
            "..........",
            "......#...",
            "..........",
            "..........",
            "##########").Grid;
        Assert.True(StepSafety.WallAhead(g, new TilePoint(4, 3), 1));   // the block at (6,1) is at head height
        Assert.False(StepSafety.WallAhead(g, new TilePoint(4, 3), -1));
        Assert.False(StepSafety.WallAhead(g, new TilePoint(4, 3), 0));
    }
}

public class MinionFocusAdjacentTests
{
    [Fact]
    public void BossFirst_OnlyAMinionRightNextToThePlayerIsShot()
    {
        var f = new MinionFocus(adjacentOnly: true);
        Assert.Null(f.Tick(new[] { new Contender(1, 6f, 0f, -0.3f, 0f) }));  // closing fast but 6 tiles away
        Assert.Equal(2, f.Tick(new[] { new Contender(2, 2f, 0.5f, 0f, 0f) }));
        Assert.Null(f.Tick(new[] { new Contender(2, 5f, 0f, 0f, 0f) }));     // moved off: back to the boss
        var all = new MinionFocus();
        Assert.Equal(1, all.Tick(new[] { new Contender(1, 6f, 0f, -0.3f, 0f) }));
    }
}

public class CorneredTests
{
    [Fact]
    public void Cornered_LittleRoomBehind_AndTheBossNear()
    {
        var a = new ArenaBounds(70, 124);
        var boss = new EnemyMotion(85f, 36f, 3.8f, 3.6f, 0f, 0f);
        Assert.True(BossMovement.Cornered(a, 73f, boss));                  // 2.4 tiles behind, boss 7 tiles away
        Assert.False(BossMovement.Cornered(a, 73f, boss with { CX = 110f })); // boss far
        Assert.False(BossMovement.Cornered(a, 97f, boss));                 // room behind
        Assert.True(BossMovement.Cornered(a, 122f, boss with { CX = 112f })); // the other end
        Assert.False(BossMovement.Cornered(null, 73f, boss));
    }
}

public class JumperDodgeTests
{
    private static ArrayTileGrid Flat() => new GridBuilder(200, 46).Fill(0, 40, 199, 45, '#').Parse().Grid;

    private static PlayerKinematics Standing(float cx, float vx = 0f) =>
        new(cx - PlayerPhysics.HitboxWidth / 2f, 39f, vx, 0f, true);

    [Fact]
    public void Cornered_AgainstAWall_ABigJump_IsPassedUnder()
    {
        var g = Flat();
        var arena = new ArenaBounds(70, 124);
        var m = new BossMovement();
        // Player 3 tiles from the left end; King Slime just took off from 6 tiles to the right on its big jump.
        var me = Standing(74f);
        var boss = new EnemyMotion(84f, 40f - 3.6f - 0.8f, 3.8f, 3.6f, -3.5f / 16f, -12.7f / 16f, 0.3f / 16f, 10f / 16f, 0.8f);
        var d = m.Tick(new BossMoveInputs(me, g, arena, boss, null, WeaponStyle.Ranged, 15f, true, true, AttackStrategy.Kite, 10f));
        Assert.Equal(1, d.Dir);
        Assert.True(d.Crossing, $"{d}");
        Assert.True(d.Cornered);
        Assert.True(d.Fire);
    }

    [Fact]
    public void InTheOpen_AMediumJump_IsOutrun()
    {
        var g = Flat();
        var arena = new ArenaBounds(40, 160);
        var m = new BossMovement();
        var me = Standing(100f);
        // Medium jump from 8 tiles to the right: too low to pass under.
        var boss = new EnemyMotion(100f + 0.625f + 8f + 3.8f, 40f - 3.6f - 0.35f, 3.8f, 3.6f, -4.5f / 16f, -5.7f / 16f,
            0.3f / 16f, 10f / 16f, 0.8f);
        var d = m.Tick(new BossMoveInputs(me, g, arena, boss, null, WeaponStyle.Ranged, 15f, true, true, AttackStrategy.Kite, 10f));
        Assert.Equal(-1, d.Dir);
        Assert.False(d.Crossing);
    }

    [Fact]
    public void AFlyingBoss_IsNotAJumper()
    {
        Assert.False(JumperDodge.IsJumper(new EnemyMotion(10f, 10f, 3f, 3f, 0f, 0f)));
        Assert.True(JumperDodge.IsJumper(new EnemyMotion(10f, 10f, 3f, 3f, 0f, 0f, 0.3f / 16f)));
    }
}
