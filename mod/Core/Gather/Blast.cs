using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;

namespace LlmPlayer.Core.Gather;

/// <summary>The thrown explosives <c>blast</c> supports. Bouncy variants, Scarab Bombs (directional blast), dirt bombs
/// (they place dirt) and grenades (they don't break tiles) are not supported.</summary>
public enum ExplosiveKind { StickyBomb, Bomb, StickyDynamite, Dynamite }

/// <summary>
/// One thrown explosive, in tiles and ticks (60 ticks = 1 s). Defaults are the vanilla values from the Terraria wiki
/// (Bomb / Sticky Bomb / Dynamite pages, desktop 1.4.4); the game glue passes the item's real <c>shootSpeed</c> and the
/// projectile's real <c>timeLeft</c> and hitbox.
/// </summary>
/// <param name="TileRadius">Tiles whose centre is closer than this to the explosion centre are destroyed (Bomb 4,
/// Dynamite 7).</param>
/// <param name="DamageHalf">Half the side of the square that damages entities (players too): Bomb 128 px = 4 tiles,
/// Dynamite 250 px = 7.8125 tiles.</param>
/// <param name="FuseTicks">Projectile timeLeft: Bomb 180 (3 s), Dynamite 300 (5 s). It explodes at timeLeft 3.</param>
/// <param name="SpeedPx">Throw speed in px/tick (item shootSpeed: Bomb 5, Dynamite 4).</param>
/// <param name="Sticky">Sticks to the first block it touches (Sticky Bomb / Sticky Dynamite).</param>
/// <param name="SizePx">Projectile hitbox side in px.</param>
public sealed record ExplosiveSpec(ExplosiveKind Kind, string Name, float TileRadius, float DamageHalf, int FuseTicks,
    float SpeedPx, bool Sticky, float SizePx)
{
    /// <summary>Dynamite rolls further (vanilla multiplies its ground friction by an extra 0.99 and its bounce by 0.8).</summary>
    public bool IsDynamite => Kind is ExplosiveKind.Dynamite or ExplosiveKind.StickyDynamite;

    /// <summary>A plain (non-sticky) explosive rolls and bounces: extra safety margin and a tighter miss limit.</summary>
    public float RollSlack => Sticky ? 0f : 1f;

    /// <summary>How far (tiles) the predicted explosion centre may be from the target's centre: one and a half tiles
    /// (sticky) / two tiles (rolling) inside the tile radius, so model error still destroys the target.</summary>
    public float MaxMiss => TileRadius - 1.5f - 0.5f * RollSlack;
}

public static class Explosives
{
    /// <summary>Default choice: the smallest blast first (least collateral), sticky before plain (it stays where it
    /// hits; a plain bomb rolls and bounces).</summary>
    public static readonly IReadOnlyList<ExplosiveKind> Preference =
        new[] { ExplosiveKind.StickyBomb, ExplosiveKind.Bomb, ExplosiveKind.StickyDynamite, ExplosiveKind.Dynamite };

    /// <summary>Vanilla spec; <paramref name="speedPx"/>, <paramref name="fuseTicks"/>, <paramref name="sizePx"/>
    /// override the defaults with the live item/projectile stats.</summary>
    public static ExplosiveSpec Spec(ExplosiveKind kind, float? speedPx = null, int? fuseTicks = null, float? sizePx = null)
    {
        var s = kind switch
        {
            ExplosiveKind.Bomb => new ExplosiveSpec(kind, "Bomb", 4f, 4f, 180, 5f, false, 22f),
            ExplosiveKind.StickyBomb => new ExplosiveSpec(kind, "Sticky Bomb", 4f, 4f, 180, 5f, true, 22f),
            ExplosiveKind.Dynamite => new ExplosiveSpec(kind, "Dynamite", 7f, 7.8125f, 300, 4f, false, 10f),
            _ => new ExplosiveSpec(kind, "Sticky Dynamite", 7f, 7.8125f, 300, 4f, true, 10f),
        };
        return s with
        {
            SpeedPx = speedPx is > 0 ? speedPx.Value : s.SpeedPx,
            FuseTicks = fuseTicks is > 3 ? fuseTicks.Value : s.FuseTicks,
            SizePx = sizePx is > 0 ? sizePx.Value : s.SizePx,
        };
    }

    /// <summary>The first kind of <see cref="Preference"/> that is available, or null.</summary>
    public static ExplosiveKind? Choose(IEnumerable<ExplosiveKind> available)
    {
        var set = available.ToHashSet();
        foreach (var k in Preference)
            if (set.Contains(k)) return k;
        return null;
    }
}

/// <summary>Where a simulated throw ends up when it explodes.</summary>
/// <param name="X">Projectile centre (tiles) at detonation.</param>
/// <param name="Unpredictable">The flight touched an unknown tile or liquid: the result can't be trusted.</param>
/// <param name="Ticks">Ticks until the projectile came to rest (sticky: stuck) or detonated.</param>
public readonly record struct ThrowResult(float X, float Y, bool Unpredictable, bool Stuck, int Ticks);

/// <summary>
/// Flight of a thrown bomb (vanilla projectile aiStyle 16, approximated from the wiki and the documented AI): it
/// spawns at the player's centre with velocity = normalize(cursor − centre) × shootSpeed; flies straight for its
/// first <see cref="StraightTicks"/> ticks, then gains <see cref="GravityPx"/> px/tick² down (capped at
/// <see cref="MaxFallPx"/>); on the ground it slows by ×0.97 a tick (Dynamite ×0.99 more); hitting a wall reverses X
/// at ×0.4 (Dynamite ×0.32), landing faster than 0.7 px/tick bounces at ×0.4 (Dynamite ×0.32). Sticky ones stop
/// at the first block they touch. Platforms stop it from above. Everything in tiles/ticks.
/// </summary>
public static class ThrowModel
{
    public const int StraightTicks = 5;
    public const float GravityPx = 0.2f, MaxFallPx = 16f, BounceMinPx = 0.7f, Bounce = 0.4f, GroundFriction = 0.97f;
    private const float Px = 1f / 16f, Eps = 0.001f;

    /// <summary>Initial velocity (tiles/tick) from the throw origin toward the centre of the cursor tile.</summary>
    public static (float Vx, float Vy) Velocity(float originX, float originY, TilePoint cursor, float speedPx)
    {
        float dx = cursor.X + 0.5f - originX, dy = cursor.Y + 0.5f - originY;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-4f) return (0f, 0f);
        float v = speedPx * Px / len;
        return (dx * v, dy * v);
    }

    public static ThrowResult Simulate(ITileGrid grid, float originX, float originY, TilePoint cursor, ExplosiveSpec spec)
    {
        var (vx, vy) = Velocity(originX, originY, cursor, spec.SpeedPx);
        return Simulate(grid, originX, originY, vx, vy, spec);
    }

    /// <summary>Simulate until detonation (timeLeft reaches 3) or until a sticky one sticks.</summary>
    public static ThrowResult Simulate(ITileGrid grid, float x, float y, float vx, float vy, ExplosiveSpec spec)
    {
        float half = spec.SizePx * Px / 2f;
        float bounce = spec.IsDynamite ? Bounce * 0.8f : Bounce;
        int detonate = spec.FuseTicks - 3;
        bool unknown = false, onGround = false;
        int restTicks = 0;
        if (Blocked(grid, x - half, y - half, x + half, y + half, false, 0f, ref unknown) || unknown)
            return new ThrowResult(x, y, true, false, 0);
        for (int t = 0; t < detonate; t++)
        {
            if (t >= StraightTicks)
            {
                if (onGround && vx != 0f)
                {
                    vx *= GroundFriction;
                    if (spec.IsDynamite) vx *= 0.99f;
                    if (MathF.Abs(vx) < 0.01f * Px) vx = 0f;
                }
                vy = MathF.Min(vy + GravityPx * Px, MaxFallPx * Px);
            }

            // X
            if (vx != 0f)
            {
                float nx = x + vx;
                if (Blocked(grid, nx - half, y - half, nx + half, y + half, false, 0f, ref unknown))
                {
                    if (unknown && _wet && SinksStraight(spec, vx, vy)) return Sink(grid, x, y, half, t);
                    if (unknown) return new ThrowResult(x, y, true, false, t);
                    x = vx > 0 ? MathF.Floor(nx + half) - half - Eps : MathF.Floor(nx - half) + 1f + half + Eps;
                    if (spec.Sticky) return new ThrowResult(x, y, false, true, t);
                    vx = -vx * bounce;
                }
                else x = nx;
            }

            // Y
            onGround = false;
            if (vy != 0f)
            {
                float ny = y + vy;
                if (Blocked(grid, x - half, ny - half, x + half, ny + half, vy > 0, y + half, ref unknown))
                {
                    if (unknown && _wet && SinksStraight(spec, vx, vy)) return Sink(grid, x, y, half, t);
                    if (unknown) return new ThrowResult(x, y, true, false, t);
                    if (vy > 0)
                    {
                        y = MathF.Floor(ny + half) - half - Eps;
                        onGround = true;
                    }
                    else y = MathF.Floor(ny - half) + 1f + half + Eps;
                    if (spec.Sticky) return new ThrowResult(x, y, false, true, t);
                    vy = vy > BounceMinPx * Px ? -vy * bounce : 0f;
                }
                else y = ny;
            }
            if (!onGround && vy >= 0f && Blocked(grid, x - half, y + half, x + half, y + half + 2 * Eps, true, y + half, ref unknown))
                onGround = !unknown;
            if (unknown) return new ThrowResult(x, y, true, false, t);

            if (onGround && vx == 0f && vy == 0f)
            {
                if (++restTicks > 2) return new ThrowResult(x, y, false, false, t);
            }
            else restTicks = 0;
        }
        return new ThrowResult(x, y, false, false, detonate);
    }

    /// <summary>A sticky explosive entering water/honey while falling mostly downward: water drag kills the sideways
    /// speed and it sinks onto the first block below, where it sticks (seen in game: a pool flowed into the blast
    /// crater above a Shadow Orb and every throw into it was refused as unpredictable).</summary>
    private static bool SinksStraight(ExplosiveSpec spec, float vx, float vy) => spec.Sticky && vy > 0f && vy >= MathF.Abs(vx);

    /// <summary>Sink straight down from (x, y) through liquid and air onto the highest block under the box's columns.
    /// Unknown tiles, lava or hazards on the way (or nothing within 30 rows) make it unpredictable.</summary>
    private static ThrowResult Sink(ITileGrid g, float x, float y, float half, int t)
    {
        int x0 = (int)MathF.Floor(x - half), x1 = (int)MathF.Floor(x + half - Eps);
        int startRow = (int)MathF.Floor(y + half - Eps);
        for (int ty = startRow; ty <= startRow + 30; ty++)
        {
            for (int tx = x0; tx <= x1; tx++)
            {
                var k = g.Get(tx, ty).Kind;
                if (k is TileKind.Unknown or TileKind.Lava or TileKind.Hazard) return new ThrowResult(x, y, true, false, t);
                if (k is TileKind.Solid or TileKind.Platform && ty > startRow)
                    return new ThrowResult(x, ty - half - Eps, false, true, t + 4 * (ty - startRow));
            }
        }
        return new ThrowResult(x, y, true, false, t);
    }

    /// <summary>Does the box overlap a block? Unknown tiles and liquid set <paramref name="unknown"/> (unpredictable
    /// flight). Platforms block only a downward move whose previous bottom was at or above their top.</summary>
    /// <summary>Set by <see cref="Blocked"/> when the "unknown" it reported was water or honey (not lava/unknown).</summary>
    [ThreadStatic] private static bool _wet;

    private static bool Blocked(ITileGrid g, float l, float t, float r, float b, bool down, float prevBottom, ref bool unknown)
    {
        _wet = false;
        int x0 = (int)MathF.Floor(l), x1 = (int)MathF.Floor(r - Eps), y0 = (int)MathF.Floor(t), y1 = (int)MathF.Floor(b - Eps);
        for (int ty = y0; ty <= y1; ty++)
            for (int tx = x0; tx <= x1; tx++)
            {
                var info = g.Get(tx, ty);
                switch (info.Kind)
                {
                    case TileKind.Water:
                    case TileKind.Honey:
                        _wet = true;
                        unknown = true;
                        return true;
                    case TileKind.Unknown:
                    case TileKind.Lava:
                        unknown = true;
                        return true;
                    case TileKind.Solid:
                        return true;
                    case TileKind.Platform when down && prevBottom <= ty + 2 * Eps:
                        return true;
                }
            }
        return false;
    }
}

/// <summary>A chosen throw: stand at <see cref="Stand"/> (feet-left), aim at <see cref="Cursor"/>, expect the
/// explosion at (<see cref="DetX"/>, <see cref="DetY"/>), <see cref="Miss"/> tiles from the target's centre.</summary>
public sealed record BlastPlan(TilePoint Stand, TilePoint Cursor, float DetX, float DetY, float Miss, int FlightTicks)
{
    /// <summary>The tile the explosion centre is in.</summary>
    public TilePoint DetTile => new((int)MathF.Floor(DetX), (int)MathF.Floor(DetY));
}

/// <summary>
/// Where to stand and where to aim for <c>blast</c>. The player must stay out of the explosion's damage square
/// (<see cref="ExplosiveSpec.DamageHalf"/>) plus a margin, measured against the real 1.25 × 2.625-tile hitbox, around
/// both the predicted explosion and the target itself. The throw must be predicted (<see cref="ThrowModel"/>, only
/// through known tiles) to explode within <see cref="ExplosiveSpec.MaxMiss"/> of the target.
/// </summary>
public static class BlastPlanner
{
    /// <summary>Tiles kept between the damage square and the hitbox (plus <see cref="ExplosiveSpec.RollSlack"/>).</summary>
    public const float PlayerMargin = 2f;
    public const int SearchRadius = 20, MaxCandidates = 80;
    /// <summary>Cursor rings (Chebyshev radius around the throw origin): their tiles are the aim directions tried.</summary>
    private static readonly int[] Rings = { 6, 10, 14 };
    private const float HitboxHeight = PlayerPhysics.HitboxHeightPx / 16f;

    /// <summary>The hitbox (tiles) of a player standing centred at feet-left <paramref name="s"/>.</summary>
    public static (float L, float T, float R, float B) Hitbox(TilePoint s)
    {
        float l = s.X + PlayerPhysics.CenterOffset, b = s.Y + 1f;
        return (l, b - HitboxHeight, l + PlayerPhysics.HitboxWidth, b);
    }

    /// <summary>Throw origin (= hitbox centre) of a player standing at <paramref name="s"/>.</summary>
    public static (float X, float Y) Origin(TilePoint s)
    {
        var h = Hitbox(s);
        return ((h.L + h.R) / 2f, (h.T + h.B) / 2f);
    }

    /// <summary>Danger reach (tiles) from the explosion centre: damage half-size + margin + roll slack.</summary>
    public static float DangerHalf(ExplosiveSpec spec, float margin = PlayerMargin) => spec.DamageHalf + margin + spec.RollSlack;

    /// <summary>Is a hitbox fully outside the danger square around (<paramref name="cx"/>, <paramref name="cy"/>)?</summary>
    public static bool HitboxSafe((float L, float T, float R, float B) h, float cx, float cy, ExplosiveSpec spec, float margin = PlayerMargin)
    {
        float d = DangerHalf(spec, margin);
        return h.R <= cx - d || h.L >= cx + d || h.B <= cy - d || h.T >= cy + d;
    }

    /// <summary>Which way to walk to get out of the danger square: away from the explosion horizontally (−1 left,
    /// +1 right).</summary>
    public static int RetreatDirection((float L, float T, float R, float B) h, float cx) => (h.L + h.R) / 2f >= cx ? 1 : -1;

    /// <summary>The best aim from a throw origin: smallest miss, then shortest flight. Null if no aim lands within
    /// <see cref="ExplosiveSpec.MaxMiss"/>; <paramref name="closest"/> is the best predictable detonation anyway.
    /// With <paramref name="hitbox"/>, aims whose explosion would reach that hitbox are skipped
    /// (<paramref name="tooClose"/> counts the ones that would otherwise have qualified).</summary>
    public static (TilePoint Cursor, ThrowResult Result, float Miss)? BestThrow(ITileGrid grid, float ox, float oy, TilePoint target,
        ExplosiveSpec spec, out (ThrowResult Result, float Miss)? closest, out int tooClose,
        (float L, float T, float R, float B)? hitbox = null, float margin = PlayerMargin)
    {
        tooClose = 0;
        closest = null;
        (TilePoint, ThrowResult, float)? best = null;
        float tx = target.X + 0.5f, ty = target.Y + 0.5f;
        int cx = (int)MathF.Floor(ox), cy = (int)MathF.Floor(oy);
        foreach (int r in Rings)
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    var cursor = new TilePoint(cx + dx, cy + dy);
                    var res = ThrowModel.Simulate(grid, ox, oy, cursor, spec);
                    if (res.Unpredictable) continue;
                    float miss = MathF.Sqrt((res.X - tx) * (res.X - tx) + (res.Y - ty) * (res.Y - ty));
                    bool safe = hitbox is not { } hb || HitboxSafe(hb, res.X, res.Y, spec, margin);
                    if (safe && (closest is not { } c || miss < c.Miss)) closest = (res, miss);
                    if (miss > spec.MaxMiss) continue;
                    if (!safe)
                    {
                        tooClose++;
                        continue;
                    }
                    if (best is { } b && (miss > b.Item3 + 0.25f || (miss > b.Item3 - 0.25f && res.Ticks >= b.Item2.Ticks))) continue;
                    best = (cursor, res, miss);
                }
        return best;
    }

    /// <summary>
    /// Choose a stand and an aim. Candidates: standable spots (<see cref="Pathfinder.CanStand"/>, body known and dry)
    /// within <see cref="SearchRadius"/> of the target whose hitbox is safe from an explosion at the target, nearest
    /// to the player first (at most <see cref="MaxCandidates"/>). Each needs a throw (<see cref="BestThrow"/>) whose
    /// predicted explosion is also safe for that hitbox. Cost = walking distance (Manhattan) + 4 × miss.
    /// Null with <paramref name="why"/> when nothing works.
    /// </summary>
    public static BlastPlan? Plan(ITileGrid grid, TilePoint player, TilePoint target, ExplosiveSpec spec, out string? why,
        float margin = PlayerMargin)
    {
        why = null;
        float tx = target.X + 0.5f, ty = target.Y + 0.5f;
        var candidates = new List<TilePoint>();
        for (int y = target.Y - SearchRadius; y <= target.Y + SearchRadius; y++)
            for (int x = target.X - SearchRadius; x <= target.X + SearchRadius; x++)
            {
                var s = new TilePoint(x, y);
                if (!Pathfinder.CanStand(grid, s) || !BodyKnownAndDry(grid, s) || !FloorUnder(grid, s)) continue;
                if (!HitboxSafe(Hitbox(s), tx, ty, spec, margin)) continue;
                candidates.Add(s);
            }
        if (candidates.Count == 0)
        {
            why = $"no known dry spot to stand on within {SearchRadius} tiles of {target} that is more than " +
                  $"{DangerHalf(spec, margin):0.#} tiles from the blast (the {spec.Name}'s damage area plus a safety margin)";
            return null;
        }
        candidates.Sort((a, b) => Manhattan(a, player).CompareTo(Manhattan(b, player)));

        BlastPlan? best = null;
        float bestCost = float.MaxValue;
        (ThrowResult Result, float Miss)? nearest = null;
        int tooCloseTotal = 0;
        foreach (var s in candidates.Take(MaxCandidates))
        {
            var (ox, oy) = Origin(s);
            var t = BestThrow(grid, ox, oy, target, spec, out var closest, out int tooClose, Hitbox(s), margin);
            tooCloseTotal += tooClose;
            if (closest is { } c && (nearest is not { } n || c.Miss < n.Miss)) nearest = c;
            if (t is not { } th) continue;
            float cost = Manhattan(s, player) + 4f * th.Miss;
            if (cost >= bestCost) continue;
            if (!ThrowsFromWholeTile(grid, s, target, spec, margin)) continue;
            bestCost = cost;
            best = new BlastPlan(s, th.Cursor, th.Result.X, th.Result.Y, th.Miss, th.Result.Ticks);
        }
        if (best == null)
        {
            why = tooCloseTotal > 0 && (nearest is not { } n0 || n0.Miss > spec.MaxMiss)
                ? $"the {spec.Name} can't be thrown far enough: every throw that reaches {target} explodes within " +
                  $"{DangerHalf(spec, margin):0.#} tiles of where you'd stand (its damage area plus a safety margin). Use a smaller " +
                  "explosive (Sticky Bomb / Bomb) or blast from higher up"
                : nearest is { } nn
                ? $"no safe throw gets the {spec.Name} within {spec.MaxMiss:0.#} tiles of {target}: the closest predicted explosion is at " +
                  $"({(int)MathF.Floor(nn.Result.X)},{(int)MathF.Floor(nn.Result.Y)}), {nn.Miss:0.#} tiles away. Blast exposed rock nearer to you first " +
                  "(a tile next to open space you can see), then work inward"
                : $"no predictable throw path toward {target} (unknown tiles or liquid in the way): light/explore the area first";
        }
        return best;
    }

    /// <summary>Standing at tile s, the real hitbox can be up to half a tile left or right of <see cref="Hitbox"/>
    /// (StandingTile rounds); BlastTask lines it up, and the task re-aims from the real position, so a stand only
    /// qualifies if a good, safe throw exists within <see cref="WholeTileSlack"/> either way (seen in game: three stands in a row planned fine, then the re-aim
    /// from the real position found no throw: "no safe throw from here; gave up after 3 replans").</summary>
    /// <summary>How far (tiles) the real hitbox may still be off the modelled one: BlastTask lines the player up to
    /// within 2 px, this leaves room for that and for vanilla's sub-pixel drift.</summary>
    public const float WholeTileSlack = 0.2f;

    private static bool ThrowsFromWholeTile(ITileGrid grid, TilePoint s, TilePoint target, ExplosiveSpec spec, float margin)
    {
        var (ox, oy) = Origin(s);
        var h = Hitbox(s);
        foreach (float d in new[] { -WholeTileSlack, WholeTileSlack })
        {
            var hd = (h.L + d, h.T, h.R + d, h.B);
            if (BestThrow(grid, ox + d, oy, target, spec, out _, out _, hd, margin) == null) return false;
            if (!HitboxSafe(hd, target.X + 0.5f, target.Y + 0.5f, spec, margin)) return false;
        }
        return true;
    }

    /// <summary>A real floor (block or platform) under the body: standing "on" liquid sinks the player to the bottom
    /// (seen in game: the planned stand was on a flooded crater's surface, the player sank a row, and the re-aim from
    /// there found no throw, three replans in a row).</summary>
    /// Half blocks and slopes don't count either: resting only on one, the body sits up to half a tile lower than the
    /// throw model assumes (seen in game next to the same crater: a half block beside water, the same failure).
    private static bool FloorUnder(ITileGrid g, TilePoint s) => FullFloor(g.Get(s.X, s.Y + 1)) || FullFloor(g.Get(s.X + 1, s.Y + 1));

    private static bool FullFloor(TileInfo t) =>
        t.Kind == TileKind.Platform || (t.Kind == TileKind.Solid && !t.HalfBlock && t.Slope == TileSlope.None);

    private static bool BodyKnownAndDry(ITileGrid g, TilePoint s)
    {
        for (int dy = 0; dy < 3; dy++)
            for (int dx = 0; dx < 2; dx++)
                if (g.Get(s.X + dx, s.Y - dy).Kind is TileKind.Unknown or TileKind.Water or TileKind.Lava or TileKind.Honey or TileKind.Hazard)
                    return false;
        return true;
    }

    private static int Manhattan(TilePoint a, TilePoint b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
}

/// <summary>What the safety check knows about one tile near a blast (filled from the real world by the glue).</summary>
public enum BlastTileKind
{
    /// <summary>Nothing an explosion breaks (air, liquid, a non-solid decoration).</summary>
    Empty,
    /// <summary>A block the explosion destroys (dirt, stone, Ebonstone…).</summary>
    Block,
    /// <summary>Explosion-proof and harmless (dungeon brick, Lihzahrd brick, pre-Hardmode Hellstone / Meteorite…).</summary>
    Immune,
    /// <summary>Demon / Crimson Altar: explosion-proof (wiki: Explosion-proof items), so fine.</summary>
    Altar,
    /// <summary>Furniture, chests, doors, crafting stations, beds… (the AI's protection rules): never blasted.</summary>
    Protected,
    /// <summary>Shadow Orb / Crimson Heart: only with allow_orb.</summary>
    Orb,
    /// <summary>Life Crystal, Life Fruit, the sword in stone: collect them instead.</summary>
    Collectible,
    /// <summary>A pot: the explosion breaks it and its loot drops (picked up afterwards). Not worth protecting, and a
    /// pot inside rock can't be collected first (seen in game: an orb couldn't be blasted for a pot 4 tiles away).</summary>
    Breakable,
}

/// <param name="ProtectedArea">Within the spawn radius or a housed NPC's room.</param>
/// <param name="Visible">The player can see it (fair vision); unseen problems are reported without coordinates.</param>
/// <param name="Origin">Top-left tile of the object (orbs: to count each orb once).</param>
public readonly record struct BlastTile(BlastTileKind Kind, string? Name = null, bool Visible = true, bool ProtectedArea = false,
    TilePoint? Origin = null, bool Wall = false);

/// <summary>Outcome of <see cref="BlastSafety.Check"/>.</summary>
/// <param name="Refusal">Why the blast must not happen, or null.</param>
/// <param name="Destroys">Visible tiles expected to be destroyed (explosion-proof ones excluded; pots included).</param>
/// <param name="DestroysByName">Count per tile name (visible ones).</param>
/// <param name="UnseenInRadius">Unseen tiles within the tile radius (whatever they are, they may go too).</param>
/// <param name="Orbs">Distinct orbs within the checked zone (radius + margin, with allow_orb): counted for the boss rule.</param>
/// <param name="Walls">Visible tiles with a background wall within the radius (vanilla only removes walls whose
/// edge is in the radius, so this is an upper bound).</param>
public sealed record BlastReport(string? Refusal, IReadOnlyList<TilePoint> Destroys, IReadOnlyDictionary<string, int> DestroysByName,
    int UnseenInRadius, int Orbs, int Walls);

/// <summary>
/// The safety rules for <c>blast</c>, checked before every throw against the real tiles around it:
/// <list type="bullet">
/// <item>Zone: every tile whose centre is within the tile radius + <see cref="ProtectMargin"/> of the predicted
/// explosion or of the target (in case the throw lands short or long).</item>
/// <item>Refused: anything in the spawn area or an NPC house; protected objects (furniture, chests, doors, stations,
/// beds); collectibles (Life Crystal, Life Fruit, sword in stone: collect them first); Shadow Orbs / Crimson
/// Hearts unless allow_orb, and with allow_orb when breaking them would make the orb count reach 3 (the boss
/// summon) unless allow_boss_summon — the same rule as collect.</item>
/// <item>Fine: altars and other explosion-proof tiles; pots (they break and drop their loot).</item>
/// </list>
/// Expected losses: blocks (and orbs, pots) whose centre is within the tile radius of the predicted explosion.
/// </summary>
public static class BlastSafety
{
    public const float ProtectMargin = 2f;

    public static BlastReport Check(Func<int, int, BlastTile> world, TilePoint target, float detX, float detY, ExplosiveSpec spec,
        int orbsBroken, bool allowOrb, bool allowBossSummon, float margin = ProtectMargin)
    {
        float zone = spec.TileRadius + margin + spec.RollSlack;
        float tx = target.X + 0.5f, ty = target.Y + 0.5f;
        int x0 = (int)MathF.Floor(MathF.Min(detX, tx) - zone), x1 = (int)MathF.Ceiling(MathF.Max(detX, tx) + zone);
        int y0 = (int)MathF.Floor(MathF.Min(detY, ty) - zone), y1 = (int)MathF.Ceiling(MathF.Max(detY, ty) + zone);
        var destroys = new List<TilePoint>();
        var byName = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var orbs = new HashSet<TilePoint>();
        int unseen = 0, walls = 0;
        string? refusal = null, unseenRefusal = null;

        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float cx = x + 0.5f, cy = y + 0.5f;
                float dDet = Dist(cx, cy, detX, detY), dTarget = Dist(cx, cy, tx, ty);
                if (dDet >= zone && dTarget >= zone) continue;
                var t = world(x, y);
                var at = new TilePoint(x, y);
                string name = t.Name ?? t.Kind.ToString().ToLowerInvariant();
                string? problem = null;
                if (t.ProtectedArea) problem = "is in an NPC house or the spawn area";
                else switch (t.Kind)
                {
                    case BlastTileKind.Protected:
                        problem = $"({name}: furniture, chests, doors, beds and crafting stations are never blasted) is too close";
                        break;
                    case BlastTileKind.Collectible:
                        problem = $"({name}) is too close: collect it first";
                        break;
                    case BlastTileKind.Orb when !allowOrb:
                        problem = $"({name}) would break: pass allow_orb to break it on purpose (the first drops a Musket; the third summons the Eater of Worlds / Brain of Cthulhu)";
                        break;
                    case BlastTileKind.Orb:
                        orbs.Add(t.Origin ?? at);
                        break;
                }
                if (problem != null)
                {
                    if (t.Visible) refusal ??= $"{at} {problem} (within {zone:0.#} tiles of the blast)";
                    else unseenRefusal ??= $"something protected in unseen tiles within {zone:0.#} tiles of the blast";
                    continue;
                }

                if (dDet >= spec.TileRadius) continue;
                if (!t.Visible)
                {
                    unseen++;
                    continue;
                }
                if (t.Wall) walls++;
                if (t.Kind is BlastTileKind.Block or BlastTileKind.Orb or BlastTileKind.Breakable)
                {
                    destroys.Add(at);
                    byName[name] = byName.TryGetValue(name, out var n) ? n + 1 : 1;
                }
            }

        refusal ??= unseenRefusal;
        int orbCount = DistinctOrbs(orbs);
        if (refusal == null && orbCount > 0 && orbsBroken + orbCount >= 3 && !allowBossSummon)
            refusal = $"{orbsBroken} orbs were already broken: breaking {orbCount} more summons the Eater of Worlds / Brain of Cthulhu " +
                      "(pass allow_boss_summon to do it on purpose)";
        return new BlastReport(refusal, destroys, byName, unseen, orbCount, walls);
    }

    /// <summary>
    /// How many orbs the given origins stand for. An orb is a 2x2 object, so origins of real, distinct orbs are at least 2
    /// tiles apart on some axis; points closer than that (an orb tile reported without its object origin, or a frame-
    /// based origin off by one sub-tile) belong to an orb already counted. Seen in game: one Shadow Orb in the zone with
    /// one already broken was refused as "breaking 2 more summons the Eater of Worlds".
    /// </summary>
    public static int DistinctOrbs(IEnumerable<TilePoint> origins)
    {
        var reps = new List<TilePoint>();
        foreach (var p in origins.OrderBy(p => p.Y).ThenBy(p => p.X))
            if (!reps.Any(r => Math.Abs(r.X - p.X) <= 1 && Math.Abs(r.Y - p.Y) <= 1))
                reps.Add(p);
        return reps.Count;
    }

    private static float Dist(float ax, float ay, float bx, float by) => MathF.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
}
