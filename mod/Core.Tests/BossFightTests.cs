using LlmPlayer.Core.Combat;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;

namespace LlmPlayer.Core.Tests;

public class ArenaTests
{
    [Fact]
    public void Bounds_RefuseStepsOut_WithSlack_AndAllowStepsBackIn()
    {
        var a = new ArenaBounds(10, 30);
        Assert.True(a.Contains(10));
        Assert.True(a.Contains(29));
        Assert.False(a.Contains(30)); // body x..x+1 would reach column 31
        Assert.True(a.Allows(20, -1));
        Assert.False(a.Allows(11, -1));
        Assert.True(a.Allows(12, -1));
        Assert.False(a.Allows(28, 1));
        Assert.True(a.Allows(27, 1));
        Assert.True(a.Allows(5, 1));  // outside left: back in only
        Assert.False(a.Allows(5, -1));
        Assert.True(a.Allows(40, -1));
        Assert.False(a.Allows(40, 1));
        Assert.Equal(new ArenaBounds(3, 9), ArenaBounds.Of(9, 3));
    }

    [Fact]
    public void DefaultScan_StopsAtLavaHolesAndWalls()
    {
        //            0         1         2         3
        //            0123456789012345678901234567890123
        var g = ArrayTileGrid.Parse(
            "..................................",
            "..................................",
            "......#...........................",
            "......#...........................",
            "......#...........................",
            "##################LL#....#########",
            "##################################").Grid;
        var a = ArenaScan.Default(g, new TilePoint(12, 4));
        Assert.Equal(7, a.X1);  // the wall at x=6
        Assert.Equal(17, a.X2); // lava at x=18
        var b = ArenaScan.Default(g, new TilePoint(30, 4));
        Assert.Equal(20, b.X1); // the 1-deep dip at x=21..24 is walkable; the lava at x=18..19 is not
    }

    [Fact]
    public void DefaultScan_FollowsOneTileSteps_AndPlatformRows_AndCapsTheSpan()
    {
        var g = ArrayTileGrid.Parse(
            "........................................",
            "........................................",
            "........................................",
            "....................####################",
            "########################################").Grid;
        var a = ArenaScan.Default(g, new TilePoint(10, 3), maxSpan: 50);
        Assert.Equal(0, a.X1);
        Assert.Equal(39, a.X2);
        var capped = ArenaScan.Default(g, new TilePoint(10, 3), maxSpan: 5);
        Assert.Equal(5, capped.X1);
        Assert.Equal(16, capped.X2);

        var p = ArrayTileGrid.Parse(
            "....................",
            "....................",
            "....................",
            "...==========.......",
            "....................",
            "....................",
            "....................",
            "....................",
            "####################").Grid;
        var row = ArenaScan.Default(p, new TilePoint(6, 2));
        Assert.Equal(3, row.X1);
        Assert.Equal(12, row.X2); // the row ends: a 5-tile drop isn't a safe step
    }
}

public class DropThroughTests
{
    private static PlayerKinematics Standing(int x, int feetY) => new(x + PlayerPhysics.CenterOffset, feetY, 0f, 0f, true);

    [Fact]
    public void OnAPlatform_LandsOnTheNextFloor()
    {
        var g = ArrayTileGrid.Parse(
            "..........",
            "..........",
            "..........",
            "..======..",
            "..........",
            "..........",
            "..........",
            "..========",
            "##########").Grid;
        Assert.Equal(7f, DropThrough.FloorBelow(g, Standing(3, 2)));
        Assert.Equal(8f, DropThrough.FloorBelow(g, Standing(3, 6)));
        Assert.True(float.IsPositiveInfinity(DropThrough.FloorBelow(g, Standing(3, 7)))); // solid ground
        Assert.True(float.IsPositiveInfinity(DropThrough.FloorBelow(g, Standing(3, 2) with { OnGround = false })));
    }

    [Fact]
    public void NeverIntoLavaWaterOrTheUnknown_OrTooFar()
    {
        var lava = ArrayTileGrid.Parse("......", "......", "..==..", "......", "..LL..", "######").Grid;
        Assert.True(float.IsPositiveInfinity(DropThrough.FloorBelow(lava, Standing(2, 1))));
        var unknown = ArrayTileGrid.Parse("......", "......", "..==..", "..??..", "......", "######").Grid;
        Assert.True(float.IsPositiveInfinity(DropThrough.FloorBelow(unknown, Standing(2, 1))));
        var rows = new List<string> { "......", "......", "..==.." };
        for (int i = 0; i < 20; i++) rows.Add("......");
        rows.Add("######");
        Assert.True(float.IsPositiveInfinity(DropThrough.FloorBelow(ArrayTileGrid.Parse(rows.ToArray()).Grid, Standing(2, 1))));
    }
}

public class DashDetectorTests
{
    private static readonly PlayerKinematics Me = new(10f, 20f, 0f, 0f, true); // centre ≈ (10.6, 19.7)

    [Fact]
    public void FastAndHeadedAtThePlayer_IsADash()
    {
        Assert.True(DashDetector.IsDash(Me, new EnemyMotion(10.6f, 8f, 3f, 3f, 0f, 0.6f)));      // straight down
        Assert.True(DashDetector.IsDash(Me, new EnemyMotion(0f, 19.7f, 3f, 3f, 0.6f, 0.05f)));   // from the side
        Assert.False(DashDetector.IsDash(Me, new EnemyMotion(10.6f, 8f, 3f, 3f, 0f, 0.1f)));     // slow hover
        Assert.False(DashDetector.IsDash(Me, new EnemyMotion(10.6f, 8f, 3f, 3f, 0.6f, 0f)));     // fast but sideways
        Assert.False(DashDetector.IsDash(Me, new EnemyMotion(10.6f, 8f, 3f, 3f, 0f, -0.6f)));    // flying away
    }
}

public class KiterTests
{
    [Fact]
    public void RunsAwayFromTheBoss_TurnsNearTheEnds()
    {
        var k = new Kiter();
        var a = new ArenaBounds(0, 60);
        Assert.Equal(-1, k.Plan(30, a, bossDx: 5f, true, true)); // boss to the right → run left
        Assert.Equal(-1, k.Plan(10, a, bossDx: -5f, true, true)); // keeps going even when the boss passes
        Assert.Equal(1, k.Plan(3, a, bossDx: -5f, true, true));   // within the edge margin → turn
        Assert.Equal(1, k.Plan(40, a, bossDx: 5f, true, true));
        Assert.Equal(-1, k.Plan(56, a, bossDx: 5f, true, true));
    }

    [Fact]
    public void TurnsAtUnsafeSteps_HeadsBackIntoTheArena_AndHoldsWhenBoxedIn()
    {
        var k = new Kiter();
        Assert.Equal(-1, k.Plan(30, null, 5f, true, true));
        Assert.Equal(1, k.Plan(30, null, 5f, canLeft: false, canRight: true));
        Assert.Equal(0, k.Plan(30, null, 5f, false, false));
        var a = new ArenaBounds(20, 40);
        Assert.Equal(1, new Kiter().Plan(10, a, 5f, true, true));
        Assert.Equal(-1, new Kiter().Plan(45, a, -5f, true, true));
        // Narrow arena: both directions inside the margin → keep going while the step is allowed.
        var narrow = new ArenaBounds(20, 26);
        var n = new Kiter();
        Assert.Equal(-1, n.Plan(22, narrow, 5f, true, true));
    }
}

public class MinionFocusTests
{
    [Fact]
    public void PicksTheMinionThatArrivesFirst_OnlyWhenItThreatens()
    {
        var f = new MinionFocus();
        Assert.Null(f.Tick(new[] { new Contender(1, 8f, 0f, 0.1f, 0f) }));        // 8 tiles, flying away
        Assert.Null(f.Tick(new[] { new Contender(1, 20f, 0f, -0.3f, 0f) }));      // beyond range
        Assert.Equal(2, f.Tick(new[]
        {
            new Contender(1, 10f, 0f, -0.1f, 0f), // eta 100
            new Contender(2, 6f, 0f, -0.2f, 0f),  // eta 30
        }));
    }

    [Fact]
    public void CloseMinions_CountEvenWhenNotClosing_AndFocusSticksUntilGone()
    {
        var f = new MinionFocus();
        Assert.Equal(3, f.Tick(new[] { new Contender(3, 2f, 1f, 0f, 0f) }));
        // A faster one appears, but the focus stays on 3 while it's within KeepRange.
        Assert.Equal(3, f.Tick(new[] { new Contender(3, 14f, 0f, 0f, 0f), new Contender(4, 3f, 0f, -0.5f, 0f) }));
        Assert.Equal(4, f.Tick(new[] { new Contender(4, 3f, 0f, -0.5f, 0f) })); // 3 died
        Assert.Null(f.Tick(Array.Empty<Contender>()));
    }
}

public class BossMovementTests
{
    private static ArrayTileGrid Flat(int w = 80) =>
        new GridBuilder(w, 30).Fill(0, 25, w - 1, 29, '#').Parse().Grid;

    private static PlayerKinematics Standing(int x, int feetY, float vx = 0f) =>
        new(x + PlayerPhysics.CenterOffset, feetY, vx, 0f, true);

    [Fact]
    public void Ranged_KitesAwayFromAHoveringBoss_AndFiresWhenItCanSeeIt()
    {
        var g = Flat();
        var m = new BossMovement();
        var boss = new EnemyMotion(44f, 12f, 3f, 3.4f, 0f, 0f); // hovering up and to the right
        var d = m.Tick(new BossMoveInputs(Standing(40, 24), g, new ArenaBounds(5, 75), boss, null, WeaponStyle.Ranged, 15f, true, true));
        Assert.Equal(-1, d.Dir);
        Assert.False(d.Evading);
        Assert.True(d.Fire);
        var hidden = m.Tick(new BossMoveInputs(Standing(40, 24), g, new ArenaBounds(5, 75), boss, null, WeaponStyle.Ranged, 15f, false, true));
        Assert.False(hidden.Fire);
        var far = m.Tick(new BossMoveInputs(Standing(40, 24), g, null, boss with { CY = -20f }, null, WeaponStyle.Ranged, 15f, true, true));
        Assert.False(far.Fire); // beyond FireTiles
    }

    [Fact]
    public void ADashOverhead_IsDodged_ByDroppingThroughThePlatform()
    {
        var g = new GridBuilder(80, 30).Fill(0, 25, 79, 29, '#').Fill(10, 20, 70, 20, '=').Parse().Grid;
        var m = new BossMovement();
        // Standing on the platform row (feet y=19), boss right above diving at 10 px/tick.
        var boss = new EnemyMotion(41f, 9f, 3f, 3.4f, 0f, 10f / 16f);
        var d = m.Tick(new BossMoveInputs(Standing(40, 19), g, null, boss, null, WeaponStyle.Ranged, 15f, true, true));
        Assert.True(d.Evading);
        Assert.True(d.DodgeStarted);
        Assert.True(d.Drop || d.Dir != 0, $"{d}");
    }

    [Fact]
    public void Brakes_BeforeSlidingPastAnArenaEdge()
    {
        var a = new ArenaBounds(10, 40);
        Assert.Equal(1, BossMovement.BrakeFor(a, Standing(11, 24, vx: -3f / 16f)));
        Assert.Equal(0, BossMovement.BrakeFor(a, Standing(25, 24, vx: -3f / 16f)));
        Assert.Equal(-1, BossMovement.BrakeFor(a, Standing(38, 24, vx: 3f / 16f)));
        Assert.Equal(0, BossMovement.BrakeFor(a, Standing(38, 24, vx: -3f / 16f)));
    }

    [Fact]
    public void ClimbsOntoAPlatformRowAbove_WhenNothingThreatens()
    {
        var g = new GridBuilder(80, 30).Fill(0, 25, 79, 29, '#').Fill(10, 20, 70, 20, '=').Parse().Grid;
        Assert.True(BossMovement.PlatformAbove(g, Standing(40, 24)));
        Assert.False(BossMovement.PlatformAbove(g, Standing(5, 24)));
        var low = new GridBuilder(80, 30).Fill(0, 25, 79, 29, '#').Fill(10, 23, 70, 23, '=').Parse().Grid;
        Assert.False(BossMovement.PlatformAbove(low, Standing(40, 24))); // 1 row up: not a row to fight on
        var m = new BossMovement();
        var boss = new EnemyMotion(60f, 5f, 3f, 3.4f, 0f, 0f);
        var d = m.Tick(new BossMoveInputs(Standing(40, 24), g, null, boss, null, WeaponStyle.Ranged, 15f, true, true));
        Assert.True(d.Jump);
    }

    [Fact]
    public void Melee_StepsTowardTheBoss_WithinTheArena()
    {
        var g = Flat();
        var m = new BossMovement();
        var boss = new EnemyMotion(30f, 20f, 3f, 3.4f, 0f, 0f);
        var d = m.Tick(new BossMoveInputs(Standing(40, 24), g, new ArenaBounds(5, 75), boss, null, WeaponStyle.Melee, 3f, true, true));
        Assert.Equal(-1, d.Dir);
        var edge = m.Tick(new BossMoveInputs(Standing(40, 24), g, new ArenaBounds(39, 75), boss, null, WeaponStyle.Melee, 3f, true, true));
        Assert.Equal(0, edge.Dir);
    }
}

public class EvasionOthersAndDropTests
{
    [Fact]
    public void AMinionOnCollisionCourse_TriggersADodge_EvenWhenTheTargetIsFar()
    {
        var g = new GridBuilder(60, 30).Fill(0, 25, 59, 29, '#').Parse().Grid;
        var me = new PlayerKinematics(30 + PlayerPhysics.CenterOffset, 24, 0f, 0f, true);
        var far = new EnemyMotion(30f, 0f, 3f, 3f, 0f, 0f);
        var minion = new EnemyMotion(36f, 23.5f, 0.6f, 0.6f, -0.25f, 0f);
        var floor = ContactPredictor.FloorBelow(g, me);
        var alone = new Evasion().Tick(new EvasionInputs(me, far, WeaponStyle.Ranged, 0, false, floor, true, true));
        Assert.False(alone.Evading);
        var withMinion = new Evasion().Tick(new EvasionInputs(me, far, WeaponStyle.Ranged, 0, false, floor, true, true,
            Others: new[] { minion }));
        Assert.True(withMinion.Evading);
    }

    [Fact]
    public void LeftOkRightOk_BlockMovesEvenInTheAir()
    {
        var me = new PlayerKinematics(30f, 20f, 0f, 0.2f, false);
        var enemy = new EnemyMotion(28f, 19.5f, 1f, 1f, 0.3f, 0f); // closing from the left
        var d = new Evasion().Tick(new EvasionInputs(me, enemy, WeaponStyle.Ranged, 0, false, 25f, true, true, RightOk: false));
        Assert.True(d.Evading);
        Assert.NotEqual(1, d.Dir);
    }
}

public class BossStatsTests
{
    [Fact]
    public void BossFields_OnlyForBossFights()
    {
        var plain = new AttackStats(0).Result(60);
        Assert.Null(plain.Boss);
        Assert.Null(plain.BossKilled);
        Assert.Null(plain.BossLifeLeftPct);

        var s = new AttackStats(0) { PotionsUsed = 2, AmmoLeft = 140 };
        s.ObserveBoss("Eye of Cthulhu", 1401, 2800);
        var r = s.Result(600);
        Assert.Equal("Eye of Cthulhu", r.Boss);
        Assert.False(r.BossKilled);
        Assert.Equal(51, r.BossLifeLeftPct);
        Assert.Equal(2, r.PotionsUsed);
        Assert.Equal(140, r.AmmoLeft);

        s.ObserveBoss("Eye of Cthulhu", 1, 2800);
        Assert.Equal(1, s.Result(600).BossLifeLeftPct); // a sliver never reads 0
        s.BossKilled = true;
        Assert.True(s.Result(600).BossKilled);
        Assert.Null(s.Result(600).BossLifeLeftPct);
    }

    [Fact]
    public void BossResult_SerializesWithoutNullFields()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new AttackStats(0).Result(60), LlmPlayer.Core.Bridge.WireProtocol.Json);
        Assert.DoesNotContain("boss", json);
        Assert.Contains("\"potionsUsed\":0", json);
    }
}

public class BossFactsTests
{
    [Theory]
    [InlineData("eye_of_cthulhu", "Eye of Cthulhu")]
    [InlineData("Eye of Cthulhu", "Eye of Cthulhu")]
    [InlineData("EoC", "Eye of Cthulhu")]
    [InlineData("king slime", "King Slime")]
    [InlineData("KING_SLIME", "King Slime")]
    public void Find(string q, string name) => Assert.Equal(name, BossFacts.Find(q)?.Name);

    [Fact]
    public void UnknownBoss_IsNull() => Assert.Null(BossFacts.Find("Skeletron"));

    [Fact]
    public void EyeFacts_NameTheSummonAndTheDawnFlee()
    {
        var e = BossFacts.EyeOfCthulhu;
        Assert.Contains("6 Lens", e.Recipe);
        Assert.Contains("Demon Altar", e.Recipe);
        Assert.Contains("night", e.SummonWhen);
        Assert.Contains("dawn", e.Leaves);
        Assert.Contains("200", e.NaturalSpawn);
    }
}
