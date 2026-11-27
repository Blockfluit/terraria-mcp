using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class ExplosiveSpecTests
{
    [Fact]
    public void VanillaDefaults_FromTheWiki()
    {
        var bomb = Explosives.Spec(ExplosiveKind.Bomb);
        Assert.Equal(4f, bomb.TileRadius);
        Assert.Equal(180, bomb.FuseTicks);
        Assert.Equal(5f, bomb.SpeedPx);
        Assert.False(bomb.Sticky);
        var dyn = Explosives.Spec(ExplosiveKind.Dynamite);
        Assert.Equal(7f, dyn.TileRadius);
        Assert.Equal(7.8125f, dyn.DamageHalf);
        Assert.Equal(300, dyn.FuseTicks);
        Assert.True(Explosives.Spec(ExplosiveKind.StickyBomb).Sticky);
    }

    [Fact]
    public void LiveStatsOverrideDefaults()
    {
        var s = Explosives.Spec(ExplosiveKind.Bomb, speedPx: 6f, fuseTicks: 200, sizePx: 20f);
        Assert.Equal((6f, 200, 20f), (s.SpeedPx, s.FuseTicks, s.SizePx));
        Assert.Equal(180, Explosives.Spec(ExplosiveKind.Bomb, fuseTicks: 0).FuseTicks);
    }

    [Fact]
    public void Choose_SmallestBlastFirst_StickyBeforePlain()
    {
        Assert.Equal(ExplosiveKind.StickyBomb, Explosives.Choose(new[] { ExplosiveKind.Dynamite, ExplosiveKind.Bomb, ExplosiveKind.StickyBomb }));
        Assert.Equal(ExplosiveKind.Bomb, Explosives.Choose(new[] { ExplosiveKind.Dynamite, ExplosiveKind.Bomb }));
        Assert.Equal(ExplosiveKind.StickyDynamite, Explosives.Choose(new[] { ExplosiveKind.Dynamite, ExplosiveKind.StickyDynamite }));
        Assert.Null(Explosives.Choose(Array.Empty<ExplosiveKind>()));
    }

    [Fact]
    public void MissLimit_LeavesSlackInsideTheRadius()
    {
        Assert.Equal(2.5f, Explosives.Spec(ExplosiveKind.StickyBomb).MaxMiss);
        Assert.Equal(2f, Explosives.Spec(ExplosiveKind.Bomb).MaxMiss);
    }
}

public class ThrowModelTests
{
    // 30 wide, floor at row 10 (rows 10-12 solid).
    private static ArrayTileGrid Flat(char fill = '.') =>
        ArrayTileGrid.Parse(Enumerable.Range(0, 13).Select(y => y >= 10 ? new string('#', 30) : new string(fill, 30)).ToArray()).Grid;

    [Fact]
    public void Velocity_HasTheThrowSpeed_TowardTheCursorCentre()
    {
        var (vx, vy) = ThrowModel.Velocity(5f, 5f, new TilePoint(8, 8), 5f);
        Assert.Equal(5f / 16f, MathF.Sqrt(vx * vx + vy * vy), 4);
        Assert.Equal(vx, vy, 4); // 45° down-right (cursor centre 8.5,8.5)
    }

    [Fact]
    public void StickyBomb_SticksToTheFirstBlockItTouches()
    {
        var spec = Explosives.Spec(ExplosiveKind.StickyBomb);
        var r = ThrowModel.Simulate(Flat(), 5f, 8.7f, new TilePoint(8, 12), spec);
        Assert.True(r.Stuck);
        Assert.False(r.Unpredictable);
        Assert.InRange(r.Y, 9.2f, 10f); // resting on the floor's top (row 10)
        Assert.InRange(r.X, 5.5f, 8.5f);
    }

    [Fact]
    public void PlainBomb_LandsRollsAndStopsOnFlatGround()
    {
        var spec = Explosives.Spec(ExplosiveKind.Bomb);
        var r = ThrowModel.Simulate(Flat(), 5f, 8.7f, new TilePoint(10, 8), spec);
        Assert.False(r.Stuck);
        Assert.False(r.Unpredictable);
        Assert.InRange(r.Y, 9.2f, 10f);
        Assert.True(r.X > 5f && r.X < 25f, $"x {r.X}");
    }

    [Fact]
    public void Gravity_BendsTheFlight_DownwardAfterFiveTicks()
    {
        var spec = Explosives.Spec(ExplosiveKind.StickyBomb);
        // Thrown level from x=2 in a long tunnel: it must come down onto the floor, not hit the far wall level.
        var r = ThrowModel.Simulate(Flat(), 2f, 8.7f, new TilePoint(29, 8), spec);
        Assert.True(r.Stuck);
        Assert.InRange(r.Y, 9.2f, 10f);
    }

    [Fact]
    public void UnknownTiles_MakeTheThrowUnpredictable()
    {
        var spec = Explosives.Spec(ExplosiveKind.StickyBomb);
        var r = ThrowModel.Simulate(Flat('?'), 5f, 8.7f, new TilePoint(8, 12), spec);
        Assert.True(r.Unpredictable);
    }

    [Fact]
    public void Liquid_MakesTheThrowUnpredictable()
    {
        var rows = Enumerable.Range(0, 13).Select(y => y >= 10 ? new string('#', 30) : y == 9 ? new string('~', 30) : new string('.', 30)).ToArray();
        var r = ThrowModel.Simulate(ArrayTileGrid.Parse(rows).Grid, 5f, 7.7f, new TilePoint(8, 12), Explosives.Spec(ExplosiveKind.Bomb));
        Assert.True(r.Unpredictable);
    }

    /// <summary>A water-filled crater (seen in game above a Shadow Orb): a sticky bomb thrown down into it sinks and
    /// sticks to the crater floor instead of being refused.</summary>
    [Fact]
    public void StickyBomb_SinksThroughWaterOntoTheFloor()
    {
        string[] rows =
        {
            "..............................",
            "..............................",
            "..............................",
            "..............................",
            "..............................",
            "..............................",
            "..............................",
            "..............................",
            "..............................",
            "##########~~~~################",
            "##########~~~~################",
            "##########~~~~################",
            "##############################",
        };
        var g = ArrayTileGrid.Parse(rows).Grid;
        var r = ThrowModel.Simulate(g, 11.5f, 5f, 0f, 0.3f, Explosives.Spec(ExplosiveKind.StickyBomb));
        Assert.False(r.Unpredictable);
        Assert.True(r.Stuck);
        Assert.InRange(r.Y, 11f, 12f); // resting on the floor (row 12 top), centre half a bomb above it
        Assert.InRange(r.X, 11f, 12f);
        // A plain bomb rolls about under water: still refused.
        Assert.True(ThrowModel.Simulate(g, 11.5f, 5f, 0f, 0.3f, Explosives.Spec(ExplosiveKind.Bomb)).Unpredictable);
    }
}

public class BlastPlannerTests
{
    /// <summary>A cavern 60 wide: air rows 0-19, floor from row 20 (solid to row 34).</summary>
    private static ArrayTileGrid Cavern(int width = 60) =>
        ArrayTileGrid.Parse(Enumerable.Range(0, 35).Select(y => y >= 20 ? new string('#', width) : new string('.', width)).ToArray()).Grid;

    [Fact]
    public void Hitbox_IsTheCentred20x42Box()
    {
        var h = BlastPlanner.Hitbox(new TilePoint(10, 10));
        Assert.Equal(10.375f, h.L, 3);
        Assert.Equal(11.625f, h.R, 3);
        Assert.Equal(11f, h.B, 3);
        Assert.Equal(11f - 42f / 16f, h.T, 3);
    }

    [Fact]
    public void HitboxSafe_KeepsDamageSquarePlusMarginClear()
    {
        var bomb = Explosives.Spec(ExplosiveKind.StickyBomb); // danger half = 4 + 2
        var stand = BlastPlanner.Hitbox(new TilePoint(10, 20));
        Assert.True(BlastPlanner.HitboxSafe(stand, 10.375f - 6.01f, 20f, bomb));
        Assert.False(BlastPlanner.HitboxSafe(stand, 10.375f - 5.9f, 20f, bomb));
        Assert.True(BlastPlanner.HitboxSafe(stand, 11f, 21f + 6.01f, bomb)); // far enough below the feet
        Assert.False(BlastPlanner.HitboxSafe(stand, 11f, 21f + 5f, bomb));
        // A plain bomb rolls: one more tile.
        Assert.False(BlastPlanner.HitboxSafe(stand, 10.375f - 6.5f, 20f, Explosives.Spec(ExplosiveKind.Bomb)));
    }

    [Fact]
    public void RetreatDirection_AwayFromTheBlast()
    {
        var h = BlastPlanner.Hitbox(new TilePoint(10, 20));
        Assert.Equal(1, BlastPlanner.RetreatDirection(h, 8f));
        Assert.Equal(-1, BlastPlanner.RetreatDirection(h, 14f));
    }

    [Theory]
    [InlineData(ExplosiveKind.StickyBomb)]
    [InlineData(ExplosiveKind.Bomb)]
    public void FloorTarget_StandIsOutsideTheBlast_AndTheThrowLandsOnTarget(ExplosiveKind kind)
    {
        var grid = Cavern();
        var spec = Explosives.Spec(kind);
        var target = new TilePoint(30, 20); // floor surface tile
        var plan = BlastPlanner.Plan(grid, new TilePoint(29, 19), target, spec, out var why);
        Assert.True(plan != null, why);
        Assert.True(plan!.Miss <= spec.MaxMiss);
        Assert.True(BlastPlanner.HitboxSafe(BlastPlanner.Hitbox(plan.Stand), plan.DetX, plan.DetY, spec));
        Assert.True(BlastPlanner.HitboxSafe(BlastPlanner.Hitbox(plan.Stand), target.X + 0.5f, target.Y + 0.5f, spec));
        // Re-simulating the chosen aim from the stand reproduces the prediction.
        var (ox, oy) = BlastPlanner.Origin(plan.Stand);
        var r = ThrowModel.Simulate(grid, ox, oy, plan.Cursor, spec);
        Assert.Equal(plan.DetX, r.X, 3);
        Assert.Equal(plan.DetY, r.Y, 3);
        Assert.Equal(19, plan.Stand.Y); // on the cavern floor
    }

    [Fact]
    public void Dynamite_CantBeThrownClearOfItsOwnBlast_OnOpenGround()
    {
        var plan = BlastPlanner.Plan(Cavern(), new TilePoint(29, 19), new TilePoint(30, 20), Explosives.Spec(ExplosiveKind.StickyDynamite), out var why);
        Assert.Null(plan);
        Assert.Contains("can't be thrown far enough", why);
    }

    [Fact]
    public void Dynamite_FromALedgeHighAbove_IsPossible()
    {
        // A ledge 16 rows above a pit floor: dropping Sticky Dynamite down to the floor keeps the player clear.
        var rows = Enumerable.Range(0, 40).Select(y =>
            y < 14 ? new string('.', 60)
            : y == 14 ? new string('#', 24) + new string('.', 36)
            : y < 30 ? new string('#', 10) + new string('.', 50)
            : new string('#', 60)).ToArray();
        var g = ArrayTileGrid.Parse(rows).Grid;
        var spec = Explosives.Spec(ExplosiveKind.StickyDynamite);
        var plan = BlastPlanner.Plan(g, new TilePoint(20, 13), new TilePoint(30, 30), spec, out var why);
        Assert.True(plan != null, why);
        Assert.True(BlastPlanner.HitboxSafe(BlastPlanner.Hitbox(plan!.Stand), plan.DetX, plan.DetY, spec));
    }

    [Fact]
    public void PrefersTheNearestSafeStand()
    {
        var spec = Explosives.Spec(ExplosiveKind.StickyBomb);
        var plan = BlastPlanner.Plan(Cavern(), new TilePoint(45, 19), new TilePoint(30, 20), spec, out _)!;
        Assert.True(plan.Stand.X > 30, $"stand {plan.Stand}: should stay on the player's side");
    }

    [Fact]
    public void BuriedTarget_RefusedWithTheClosestReachableDetonation()
    {
        // The in-game case: an orb 17 tiles under the cavern floor, inside rock.
        var spec = Explosives.Spec(ExplosiveKind.StickyBomb);
        var plan = BlastPlanner.Plan(Cavern(), new TilePoint(28, 19), new TilePoint(30, 33), spec, out var why);
        Assert.Null(plan);
        Assert.Contains("closest predicted explosion", why);
        Assert.Contains("exposed rock", why);
    }

    [Fact]
    public void EncasedOrb_ReachedByBlastingDownLayerByLayer()
    {
        // The in-game case in miniature: cavern floor at row 20, a 2x2 orb at (30,36)-(31,37) 16 rows into the rock.
        var grid = Cavern(80);
        var spec = Explosives.Spec(ExplosiveKind.StickyBomb);
        var orb = new TilePoint(30, 36);
        var player = new TilePoint(28, 19);
        for (int blast = 0; blast < 10; blast++)
        {
            var direct = BlastPlanner.Plan(grid, player, orb, spec, out _);
            if (direct != null)
            {
                Assert.True(direct.Miss <= spec.MaxMiss);
                Assert.True(blast >= 3, $"reached after {blast} blasts: the orb starts buried");
                return;
            }
            // Next layer: the topmost rock in the orb's column (exposed to the crater above it).
            int y = 0;
            while (!grid.Get(orb.X, y).IsSolid) y++;
            var plan = BlastPlanner.Plan(grid, player, new TilePoint(orb.X, y), spec, out var why);
            Assert.True(plan != null, $"blast {blast} at ({orb.X},{y}): {why}");
            for (int ty = (int)plan!.DetY - 5; ty <= plan.DetY + 5; ty++)
                for (int tx = (int)plan.DetX - 5; tx <= plan.DetX + 5; tx++)
                    if (MathF.Sqrt(MathF.Pow(tx + 0.5f - plan.DetX, 2) + MathF.Pow(ty + 0.5f - plan.DetY, 2)) < spec.TileRadius)
                        grid.Set(tx, ty, TileInfo.Air);
            player = plan.Stand;
        }
        Assert.Fail("orb still out of reach after 10 blasts");
    }

    [Fact]
    public void CrampedCave_NoSafeStand()
    {
        // A 7-wide pocket: nowhere 6+ tiles from the blast.
        var rows = new[]
        {
            "###########",
            "##.......##",
            "##.......##",
            "##.......##",
            "###########",
            "###########",
        };
        var g = ArrayTileGrid.Parse(rows).Grid;
        var plan = BlastPlanner.Plan(g, new TilePoint(3, 3), new TilePoint(5, 4), Explosives.Spec(ExplosiveKind.StickyBomb), out var why);
        Assert.Null(plan);
        Assert.Contains("no known dry spot", why);
    }

    [Fact]
    public void ThrowsOnlyThroughKnownTiles()
    {
        // Floor known, but the air between the player and the target is unknown.
        var rows = Enumerable.Range(0, 35).Select(y => y >= 20 ? new string('#', 60)
            : y >= 15 ? new string('.', 22) + new string('?', 16) + new string('.', 22) : new string('?', 60)).ToArray();
        var plan = BlastPlanner.Plan(ArrayTileGrid.Parse(rows).Grid, new TilePoint(10, 19), new TilePoint(30, 20),
            Explosives.Spec(ExplosiveKind.StickyBomb), out var why);
        Assert.Null(plan);
        Assert.NotNull(why);
    }

    [Fact]
    public void Crater_BlastingDeeper_FromTheRim()
    {
        // After one blast: a bowl 7 wide, 4 deep in the floor; the next target is its bottom.
        var rows = Enumerable.Range(0, 35).Select(y =>
            y < 20 ? new string('.', 60)
            : y < 24 ? new string('#', 27) + new string('.', 7) + new string('#', 26)
            : new string('#', 60)).ToArray();
        var g = ArrayTileGrid.Parse(rows).Grid;
        var spec = Explosives.Spec(ExplosiveKind.StickyBomb);
        var plan = BlastPlanner.Plan(g, new TilePoint(20, 19), new TilePoint(30, 24), spec, out var why);
        Assert.True(plan != null, why);
        Assert.True(BlastPlanner.HitboxSafe(BlastPlanner.Hitbox(plan!.Stand), plan.DetX, plan.DetY, spec));
    }
}

public class BlastSafetyTests
{
    private static readonly ExplosiveSpec Sticky = Explosives.Spec(ExplosiveKind.StickyBomb);
    private static readonly TilePoint Target = new(50, 50);

    /// <summary>Stone everywhere below row 50, air above, plus overrides.</summary>
    private static Func<int, int, BlastTile> World(Dictionary<TilePoint, BlastTile>? over = null) => (x, y) =>
        over != null && over.TryGetValue(new TilePoint(x, y), out var t) ? t
        : y >= 50 ? new BlastTile(BlastTileKind.Block, "Ebonstone Block", Wall: false) : new BlastTile(BlastTileKind.Empty);

    private static BlastReport Check(Dictionary<TilePoint, BlastTile>? over = null, bool allowOrb = false, int orbsBroken = 0,
        bool allowBoss = false) =>
        BlastSafety.Check(World(over), Target, 50.5f, 49.6f, Sticky, orbsBroken, allowOrb, allowBoss);

    [Fact]
    public void PlainRock_IsFine_AndReportsTheTilesInTheRadius()
    {
        var r = Check();
        Assert.Null(r.Refusal);
        Assert.Contains(Target, r.Destroys);
        Assert.All(r.Destroys, t => Assert.True(MathF.Sqrt(MathF.Pow(t.X + 0.5f - 50.5f, 2) + MathF.Pow(t.Y + 0.5f - 49.6f, 2)) < 4f));
        Assert.Equal(r.Destroys.Count, r.DestroysByName["Ebonstone Block"]);
        Assert.DoesNotContain(new TilePoint(50, 54), r.Destroys); // 4.9 tiles down
    }

    [Fact]
    public void Furniture_InTheMarginZone_IsRefused()
    {
        // 5 tiles away: outside the 4-tile radius but inside radius + 2 margin.
        var r = Check(new() { [new TilePoint(55, 49)] = new BlastTile(BlastTileKind.Protected, "Work Bench") });
        Assert.NotNull(r.Refusal);
        Assert.Contains("Work Bench", r.Refusal);
        Assert.Contains("(55,49)", r.Refusal);
    }

    [Fact]
    public void FarFurniture_IsFine() =>
        Assert.Null(Check(new() { [new TilePoint(58, 49)] = new BlastTile(BlastTileKind.Protected, "Chest") }).Refusal);

    [Fact]
    public void Altar_IsExplosionProof_AndNotDestroyed()
    {
        var r = Check(new() { [new TilePoint(51, 49)] = new BlastTile(BlastTileKind.Altar, "Demon Altar") });
        Assert.Null(r.Refusal);
        Assert.DoesNotContain(new TilePoint(51, 49), r.Destroys);
    }

    [Fact]
    public void ImmuneBlocks_AreNotReported()
    {
        var r = Check(new() { [new TilePoint(50, 51)] = new BlastTile(BlastTileKind.Immune, "Blue Brick") });
        Assert.Null(r.Refusal);
        Assert.DoesNotContain(new TilePoint(50, 51), r.Destroys);
    }

    [Fact]
    public void Collectible_IsRefused_CollectItFirst()
    {
        var r = Check(new() { [new TilePoint(48, 49)] = new BlastTile(BlastTileKind.Collectible, "Life Crystal") });
        Assert.Contains("Life Crystal", r.Refusal);
        Assert.Contains("collect it first", r.Refusal);
    }

    [Fact]
    public void SpawnOrHouse_IsRefused() =>
        Assert.Contains("NPC house or the spawn area",
            Check(new() { [new TilePoint(52, 52)] = new BlastTile(BlastTileKind.Block, ProtectedArea: true) }).Refusal);

    private static Dictionary<TilePoint, BlastTile> Orb(int ox = 50, int oy = 52)
    {
        var d = new Dictionary<TilePoint, BlastTile>();
        for (int dx = 0; dx < 2; dx++)
            for (int dy = 0; dy < 2; dy++)
                d[new TilePoint(ox + dx, oy + dy)] = new BlastTile(BlastTileKind.Orb, "Shadow Orb", Origin: new TilePoint(ox, oy));
        return d;
    }

    [Fact]
    public void Orb_RefusedWithoutAllowOrb() =>
        Assert.Contains("allow_orb", Check(Orb()).Refusal);

    [Fact]
    public void Orb_WithAllowOrb_IsDestroyed_CountedOnce()
    {
        var r = Check(Orb(), allowOrb: true);
        Assert.Null(r.Refusal);
        Assert.Equal(1, r.Orbs);
        Assert.Contains(new TilePoint(50, 52), r.Destroys);
    }

    [Fact]
    public void ThirdOrb_NeedsAllowBossSummon()
    {
        Assert.Contains("allow_boss_summon", Check(Orb(), allowOrb: true, orbsBroken: 2).Refusal);
        Assert.Null(Check(Orb(), allowOrb: true, orbsBroken: 2, allowBoss: true).Refusal);
        Assert.Null(Check(Orb(), allowOrb: true, orbsBroken: 1).Refusal);
    }

    [Fact]
    public void TwoOrbsAtOnce_CanAlsoReachTheBossCount()
    {
        var over = Orb(48, 51);
        foreach (var kv in Orb(52, 51)) over[kv.Key] = kv.Value;
        Assert.Contains("allow_boss_summon", Check(over, allowOrb: true, orbsBroken: 1).Refusal);
    }

    [Fact]
    public void UnseenProtectedTile_RefusedWithoutCoordinates()
    {
        var r = Check(new() { [new TilePoint(53, 52)] = new BlastTile(BlastTileKind.Protected, "Chest", Visible: false) });
        Assert.NotNull(r.Refusal);
        Assert.Contains("unseen", r.Refusal);
        Assert.DoesNotContain("53", r.Refusal);
        Assert.DoesNotContain("Chest", r.Refusal);
    }

    [Fact]
    public void UnseenTiles_CountedNotNamed()
    {
        var r = Check(new() { [new TilePoint(50, 52)] = new BlastTile(BlastTileKind.Block, "Gold Ore", Visible: false) });
        Assert.Null(r.Refusal);
        Assert.Equal(1, r.UnseenInRadius);
        Assert.False(r.DestroysByName.ContainsKey("Gold Ore"));
    }

    [Fact]
    public void ZoneAlsoCoversTheTarget_WhenThePredictionIsOff()
    {
        // Predicted far to the left; a bench near the target itself still refuses.
        var r = BlastSafety.Check(World(new() { [new TilePoint(54, 49)] = new BlastTile(BlastTileKind.Protected, "Work Bench") }),
            Target, 46.5f, 49.6f, Sticky, 0, false, false);
        Assert.NotNull(r.Refusal);
    }
}
