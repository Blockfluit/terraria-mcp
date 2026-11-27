using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;

namespace LlmPlayer.Core.Tests.Sim;

/// <summary>What the player holds while UseItem is pressed (the task picks it from the follower's current step).</summary>
public enum SimTool { None, Pickaxe, Platform }

/// <summary>
/// A small deterministic stand-in for vanilla player movement, written independently of the follower and of
/// <see cref="JumpModel"/> from tModLoader 1.4.4 Player/Collision (decompiled):
/// <list type="bullet">
/// <item>20x42 px hitbox (the real one, not 32x48), positions in pixels, one Step = one tick.</item>
/// <item>Update order: HorizontalMovement → JumpMovement → gravity (+ clamp) → StepUp → TileCollision → move.</item>
/// <item>Run: +0.08 px/tick² up to 3 (at ≥ 3 the friction branch runs, so speed hovers just under 3); friction 0.2 on
/// the ground, 0.1 in the air; holding the opposite key brakes by 0.2 + 0.08.</item>
/// <item>Jump: only from velocity.Y == 0 with the key released since the last jump; vy = -5.01 held for up to 15
/// more ticks; hitting a ceiling sets vy = 0.01 and ends the jump.</item>
/// <item>Auto step-up (Collision.StepUp, full blocks): moving into a 1-tile step with room above lifts the player
/// ≤16 px when velocity.Y ≥ gravity and Down is not held. A platform step only while Up is held, and not when
/// another platform is right above it.</item>
/// <item>Platforms: solid from above only, ignored while Down is held (fallThrough = controlDown).</item>
/// <item>Water: gravity 0.2, max fall 5, jump 6.01 x30, displacement halved (a component the tile collision changed moves in full, as Player.WaterCollision). No swimming without Flippers: a jump
/// still needs velocity.Y == 0 (the bottom). Leaving water cuts the remaining jump to 30/5 ticks.</item>
/// <item>Fall damage as vanilla: fallStart resets while jumping/wet/on ground; landing more than 25 tiles below it hurts.</item>
/// <item>Tools: reach is vanilla's tileRangeX/Y = 5/4 box with the item's tileBoost (pickaxe -1 here, like early picks; platforms 0).
/// Pickaxe on a tile with enough power breaks it after <see cref="DigTicks"/> ticks of use; platforms are placed into
/// an empty tile orthogonally next to a tile (or with a background wall), every <see cref="PlatformUseTime"/> ticks, inside the hitbox allowed.
/// Using the wrong tool on a tile (e.g. a platform on a solid block, which vanilla would swap) counts as misuse.</item>
/// <item>Doors (Interact, within the 5/4 interaction box): a closed door's whole column of door tiles becomes air
/// (WorldGen.OpenDoor; the swing column isn't modelled: an open door is passable anyway); interacting with an open
/// one closes it again unless the hitbox overlaps its column (WorldGen.CloseDoor's Collision.EmptyTile check).</item>
/// <item>Shaped blocks (an approximation of Collision.SlopeCollision / StepUp / StepDown, not a port): a half block is
/// solid in its bottom half; a floor slope (DownRight = high at the right, DownLeft = high at the left) is solid below
/// its diagonal, its high side is a full-height wall, its low side no wall at all. Moving into a slope's diagonal (or
/// landing on it at an angle) lifts the hitbox out by up to 16 px; walking downhill keeps the feet on the surface
/// (snaps down by up to the step's horizontal distance + 1 px, 8.5 px near a half block) while the player was on the
/// ground and isn't jumping. The hitbox rests on the highest surface point under it, i.e. a corner on a slope.
/// StepUp onto a half block rises 8 px, onto a full block from a half block 8 px. Ceiling slopes are full blocks.</item>
/// <item>Water walking (<see cref="WaterWalk"/>, <see cref="LavaWalk"/>; Collision.WaterCollision): the top of a liquid
/// cell with no liquid above it stops a falling hitbox like a platform (the grid has no liquid levels: the cell top),
/// ignored while Down is held. <see cref="WaterWalkTicks"/> counts down; at 0 the buff is gone.</item>
/// </list>
/// </summary>
public sealed class MiniPhysics
{
    public const int W = PlayerPhysics.HitboxWidthPx, H = PlayerPhysics.HitboxHeightPx;
    private const float Gravity = 0.4f, MaxFall = 10f, JumpSpeed = 5.01f;
    private const int JumpHeight = 15;
    private const float WetGravity = 0.2f, WetMaxFall = 5f, WetJumpSpeed = 6.01f;
    private const int WetJumpHeight = 30;
    private const float MaxRun = 3f, RunAccel = 0.08f, RunSlowdown = 0.2f;
    private const int TileRangeX = 5, TileRangeY = 4;
    private const float Eps = 0.001f;

    private readonly ArrayTileGrid _g;
    private int _jump;
    private bool _releaseJump = true, _wet;
    private int _fallStart;
    private int _itemTime;
    private readonly Dictionary<TilePoint, int> _digProgress = new();

    /// <summary>Hitbox top-left (px) and velocity (px/tick).</summary>
    public float X, Y, VX, VY;
    public int PickPower = 35, DigTicks = 30, Platforms = 100, PlatformUseTime = 10, TileBoost = -1;
    /// <summary>Wood Platforms have no tileBoost (reach 3 rows below the feet); <see cref="TileBoost"/> is the pickaxe's.</summary>
    public int PlatformTileBoost = 0;
    public int Ticks { get; private set; }
    /// <summary>Ticks the hitbox overlapped water/honey, and ticks Down was held.</summary>
    public int WetTicks { get; private set; }
    public int DownTicks { get; private set; }
    /// <summary>Water Walking: water/honey surfaces hold the player (and lava with <see cref="LavaWalk"/>).</summary>
    public bool WaterWalk, LavaWalk;
    /// <summary>Ticks of water walking left (int.MaxValue: unlimited); water walking ends at 0.</summary>
    public int WaterWalkTicks = int.MaxValue;
    public readonly List<TilePoint> Mined = new(), Placed = new();
    public int Misuse { get; private set; }
    public string? Hurt { get; private set; }
    public int MaxFallTiles { get; private set; }
    public int DoorsOpened { get; private set; }
    public int DoorsClosed { get; private set; }
    /// <summary>Open doors: any tile of the door → all of its tiles.</summary>
    private readonly Dictionary<TilePoint, List<TilePoint>> _openDoors = new();
    public bool AnyDoorOpen => _openDoors.Count > 0;

    /// <summary>Place the player standing on `start` (feet-left tile), hitbox centred in the 2-wide body.</summary>
    public MiniPhysics(ArrayTileGrid grid, TilePoint start)
    {
        _g = grid;
        X = start.X * 16 + (32 - W) / 2f;
        Y = (start.Y + 1) * 16 - H;
        _fallStart = (int)(Y / 16f);
    }

    public PlayerKinematics Kinematics => NavMath.FromPixels(X, Y, VX, VY);

    /// <summary>The grid-aware standing tile (feet riding a slope / half block count as standing on it), as the game
    /// tasks compute it.</summary>
    public TilePoint StandingTile => NavMath.StandingTile(Kinematics, _g);

    public void Step(ControlIntent c, SimTool tool = SimTool.None)
    {
        Ticks++;
        if (WaterWalkTicks != int.MaxValue && WaterWalkTicks > 0 && --WaterWalkTicks == 0) WaterWalk = LavaWalk = false;
        bool grounded = VY == 0f;
        _wet = Overlaps(X, Y, k => k is TileKind.Water or TileKind.Honey);
        float g = _wet ? WetGravity : Gravity, maxFall = _wet ? WetMaxFall : MaxFall;
        float js = _wet ? WetJumpSpeed : JumpSpeed;
        int jh = _wet ? WetJumpHeight : JumpHeight;

        Horizontal(c);

        if (c.Jump)
        {
            if (_jump > 0)
            {
                if (VY == 0f) _jump = 0;
                else
                {
                    VY = -js;
                    _jump--;
                }
            }
            else if (VY == 0f && _releaseJump)
            {
                VY = -js;
                _jump = jh;
            }
            _releaseJump = false;
        }
        else
        {
            _jump = 0;
            _releaseJump = true;
        }

        bool jumping = VY < 0f;
        VY = Math.Min(VY + g, maxFall);

        if (VY >= g && !c.Down) StepUp(c.Up);

        bool fallThrough = c.Down;
        float vxIn = VX, vyIn = VY;
        float vx = CollideX(VX);
        VX = vx;
        float vy = CollideY(VY, fallThrough, out bool up);
        VY = vy;
        // Player.WaterCollision: half the velocity, except a component the tile collision changed moves in full (else a
        // landing in water never reaches velocity.Y == 0: each tick covers only half the gap to the floor).
        float dx = _wet && VX == vxIn ? VX * 0.5f : VX;
        if (_wet)
        {
            X += dx;
            Y += VY == vyIn ? VY * 0.5f : VY;
        }
        else
        {
            X += VX;
            Y += VY;
        }
        ResolveShapes(dx, grounded && !jumping && !_wet, fallThrough);
        if (up)
        {
            VY = 0.01f;
            _jump = 0;
        }

        bool wetNow = Overlaps(X, Y, k => k is TileKind.Water or TileKind.Honey);
        if (wetNow) WetTicks++;
        if (c.Down) DownTicks++;
        if (_wet && !wetNow && _jump > jh / 5) _jump = jh / 5;

        if (Overlaps(X, Y, k => k is TileKind.Lava or TileKind.Hazard)) Hurt ??= $"touched lava/hazard at tick {Ticks}";
        FallDamage(wetNow);
        UseItem(c, tool);
        Interact(c);
    }

    private void Horizontal(ControlIntent c)
    {
        if (c.Left && VX > -MaxRun)
        {
            if (VX > RunSlowdown) VX -= RunSlowdown;
            VX -= RunAccel;
        }
        else if (c.Right && VX < MaxRun)
        {
            if (VX < -RunSlowdown) VX += RunSlowdown;
            VX += RunAccel;
        }
        else
        {
            float slow = VY == 0f ? RunSlowdown : RunSlowdown * 0.5f;
            if (VX > slow) VX -= slow;
            else if (VX < -slow) VX += slow;
            else VX = 0f;
        }
    }

    /// <summary>Collision.StepUp for full blocks and gravity down. Platforms count only while Up is held
    /// (holdsMatching) and not with another platform right above.</summary>
    private void StepUp(bool up)
    {
        int dir = Math.Sign(VX);
        if (dir == 0) return;
        float nx = X + VX;
        int col = (int)MathF.Floor((nx + W / 2 + (W / 2 + 1) * dir) / 16f);
        int feet = (int)MathF.Floor((Y + H - 1f) / 16f);
        const int bodyRows = (H + 15) / 16; // 3
        if (ShapeOf(col, feet) is not (Shape.None or Shape.Full) || ShapeOf(col, feet - 1) is not (Shape.None or Shape.Full))
        {
            ShapedStepUp(col, feet, dir, nx);
            return;
        }
        bool platformStep = up && Kind(col, feet) == TileKind.Platform && Kind(col, feet - 1) != TileKind.Platform;
        if (!Solid(col, feet) && !platformStep) return;
        if (Solid(col, feet - 1)) return;
        for (int j = 2; j <= bodyRows; j++)
            if (Solid(col, feet - j)) return;
        if (Solid(col - dir, feet - bodyRows)) return;
        if (!(col * 16 < nx + W && col * 16 + 16 > nx)) return;
        float top = feet * 16;
        if (!(top < Y + H)) return;
        float rise = Y + H - top;
        if (rise <= 16.1f) Y = top - H;
    }

    /// <summary>StepUp where the step involves a half block or slope: rise onto the highest surface of the step column
    /// (rows feet-1..feet) under the moved hitbox, by at most 16 px, if the lifted hitbox is free.</summary>
    private void ShapedStepUp(int col, int feet, int dir, float nx)
    {
        float top = float.MaxValue;
        for (int row = feet - 1; row <= feet; row++)
        {
            var sh = ShapeOf(col, row);
            if (sh == Shape.None) continue;
            top = Math.Min(top, FacingTop(col, row, sh, dir, nx, nx + W));
        }
        if (top == float.MaxValue) return;
        if (!(col * 16 < nx + W && col * 16 + 16 > nx)) return;
        if (!(top < Y + H)) return;
        if (Y + H - top > 16.1f) return;
        if (BoxBlocked(nx, top - H, nx + W, top)) return;
        Y = top - H;
    }

    /// <summary>Top of a block met while moving in direction dir over [x0, x1]: a slope's high side is a wall (the tile
    /// top), its diagonal is its surface there.</summary>
    private static float FacingTop(int tx, int ty, Shape sh, int dir, float x0, float x1) => sh switch
    {
        Shape.RisesRight when dir < 0 => ty * 16,
        Shape.RisesLeft when dir > 0 => ty * 16,
        _ => TopOver(tx, ty, sh, x0, x1),
    };

    private enum Shape { None, Full, Half, RisesRight, RisesLeft }

    private Shape ShapeOf(int x, int y)
    {
        var t = _g.Get(x, y);
        if (t.Kind != TileKind.Solid) return Shape.None;
        if (t.HalfBlock) return Shape.Half;
        return t.Slope switch
        {
            TileSlope.DownRight => Shape.RisesRight,
            TileSlope.DownLeft => Shape.RisesLeft,
            _ => Shape.Full, // ceiling slopes count as full blocks
        };
    }

    /// <summary>Highest point (smallest y, px) of the block's solid top over [x0, x1].</summary>
    private static float TopOver(int tx, int ty, Shape sh, float x0, float x1) => sh switch
    {
        Shape.Half => ty * 16 + 8,
        Shape.RisesRight => ty * 16 + 16 - Math.Clamp(x1 - tx * 16, 0f, 16f),
        Shape.RisesLeft => ty * 16 + Math.Clamp(x0 - tx * 16, 0f, 16f),
        _ => ty * 16,
    };

    /// <summary>Does the box overlap the block's solid part? (The box already overlaps the tile's cell.)</summary>
    private static bool ShapeOverlaps(int tx, int ty, Shape sh, float x0, float x1, float y1) => sh switch
    {
        Shape.None => false,
        Shape.Full => true,
        _ => y1 > TopOver(tx, ty, sh, x0, x1) + Eps,
    };

    private bool BoxBlocked(float x0, float y0, float x1, float y1)
    {
        int c0 = (int)MathF.Floor((x0 + Eps) / 16f), c1 = (int)MathF.Floor((x1 - Eps) / 16f);
        int r0 = (int)MathF.Floor((y0 + Eps) / 16f), r1 = (int)MathF.Floor((y1 - Eps) / 16f);
        for (int ty = r0; ty <= r1; ty++)
            for (int tx = c0; tx <= c1; tx++)
                if (ShapeOverlaps(tx, ty, ShapeOf(tx, ty), x0, x1, y1)) return true;
        return false;
    }

    /// <summary>
    /// After the move: pushed into a half block / slope diagonal (walking uphill, landing at an angle) → lift out by up
    /// to 16 px, or undo the horizontal move if that doesn't fit. Otherwise, walking downhill on the ground → keep the
    /// feet on the surface.
    /// </summary>
    private void ResolveShapes(float dx, bool mayStick, bool fallThrough)
    {
        float need = 0f;
        int c0 = (int)MathF.Floor((X + Eps) / 16f), c1 = (int)MathF.Floor((X + W - Eps) / 16f);
        int r0 = (int)MathF.Floor((Y + Eps) / 16f), r1 = (int)MathF.Floor((Y + H - Eps) / 16f);
        for (int ty = r0; ty <= r1; ty++)
            for (int tx = c0; tx <= c1; tx++)
            {
                var sh = ShapeOf(tx, ty);
                if (sh is Shape.None or Shape.Full || !ShapeOverlaps(tx, ty, sh, X, X + W, Y + H)) continue;
                need = Math.Max(need, Y + H - TopOver(tx, ty, sh, X, X + W));
            }
        if (need > 0f)
        {
            if (need <= 16.1f && !BoxBlocked(X, Y - need, X + W, Y - need + H))
            {
                Y -= need;
                if (VY > 0f) VY = 0f;
            }
            else
            {
                X -= dx;
                VX = 0f;
            }
            return;
        }
        if (!mayStick) return;

        // Walking downhill: the surface under the hitbox fell away by about the horizontal step.
        bool lowTop = LowTopNear(X - dx, X - dx + W, Y + H) || LowTopNear(X, X + W, Y + H);
        if (!lowTop) return;
        float limit = Math.Max(Math.Abs(dx) + 1f, 8.5f);
        float best = float.MaxValue;
        int rb0 = (int)MathF.Floor((Y + H - Eps) / 16f), rb1 = (int)MathF.Floor((Y + H + limit) / 16f);
        for (int ty = rb0; ty <= rb1; ty++)
            for (int tx = c0; tx <= c1; tx++)
            {
                var k = _g.Get(tx, ty).Kind;
                float top;
                if (k == TileKind.Solid) top = TopOver(tx, ty, ShapeOf(tx, ty), X, X + W);
                else if (k == TileKind.Platform && !fallThrough) top = ty * 16;
                else continue;
                if (top >= Y + H - Eps) best = Math.Min(best, top);
            }
        float gap = best - (Y + H);
        if (gap > Eps && gap <= limit)
        {
            Y += gap;
            VY = 0f;
        }
    }

    /// <summary>A half block or floor slope right under (or in the bottom row of) the hitbox spanning [x0, x1].</summary>
    private bool LowTopNear(float x0, float x1, float bottom)
    {
        int c0 = (int)MathF.Floor((x0 + Eps) / 16f), c1 = (int)MathF.Floor((x1 - Eps) / 16f);
        int r0 = (int)MathF.Floor((bottom - Eps) / 16f), r1 = (int)MathF.Floor((bottom + 16f) / 16f);
        for (int ty = r0; ty <= r1; ty++)
            for (int tx = c0; tx <= c1; tx++)
                if (_g.Get(tx, ty).IsLowTop) return true;
        return false;
    }

    private float CollideX(float vx)
    {
        if (vx == 0f) return 0f;
        float nx = X + vx;
        int x0 = (int)MathF.Floor(nx / 16f), x1 = (int)MathF.Floor((nx + W - Eps) / 16f);
        int y0 = (int)MathF.Floor((Y + Eps) / 16f), y1 = (int)MathF.Floor((Y + H - Eps) / 16f);
        float r = vx;
        for (int ty = y0; ty <= y1; ty++)
            for (int tx = x0; tx <= x1; tx++)
            {
                if (!Solid(tx, ty)) continue;
                // Shaped blocks: a half block only where the hitbox reaches its solid half; a slope only on its high
                // (wall) side, its diagonal lifts the hitbox instead (ResolveShapes).
                var sh = ShapeOf(tx, ty);
                if (sh == Shape.Half && !(Y + H > ty * 16 + 8 + Eps)) continue;
                if (sh == Shape.RisesRight && vx > 0) continue;
                if (sh == Shape.RisesLeft && vx < 0) continue;
                if (vx > 0 && X + W <= tx * 16 + Eps) r = Math.Min(r, tx * 16 - (X + W));
                else if (vx < 0 && X >= tx * 16 + 16 - Eps) r = Math.Max(r, tx * 16 + 16 - X);
            }
        return r;
    }

    private float CollideY(float vy, bool fallThrough, out bool up)
    {
        up = false;
        if (vy == 0f) return 0f;
        float ny = Y + vy;
        int x0 = (int)MathF.Floor((X + Eps) / 16f), x1 = (int)MathF.Floor((X + W - Eps) / 16f);
        int y0 = (int)MathF.Floor(ny / 16f), y1 = (int)MathF.Floor((ny + H - Eps) / 16f);
        float r = vy;
        for (int ty = y0; ty <= y1; ty++)
            for (int tx = x0; tx <= x1; tx++)
            {
                var k = _g.Get(tx, ty).Kind;
                if (vy > 0)
                {
                    bool blocks = k == TileKind.Solid || (k == TileKind.Platform && !fallThrough) || (!fallThrough && WalkSurface(tx, ty));
                    float top = k == TileKind.Solid ? TopOver(tx, ty, ShapeOf(tx, ty), X, X + W) : ty * 16;
                    // (Never negative: resting a hair inside a slope's surface must still read as velocity.Y == 0.)
                    if (blocks && Y + H <= top + Eps) r = Math.Min(r, Math.Max(0f, top - (Y + H)));
                }
                else if (k == TileKind.Solid && Y >= ty * 16 + 16 - Eps)
                {
                    float d = ty * 16 + 16 - Y;
                    if (d > r)
                    {
                        r = d;
                        up = true;
                    }
                }
            }
        return r;
    }

    private void FallDamage(bool wet)
    {
        int row = (int)(Y / 16f);
        if (VY == 0f)
        {
            int dist = row - _fallStart;
            MaxFallTiles = Math.Max(MaxFallTiles, dist);
            if (dist > 25) Hurt ??= $"fall damage: {dist} tiles at tick {Ticks}";
            _fallStart = row;
        }
        if (_jump > 0 || wet) _fallStart = row;
    }

    private void UseItem(ControlIntent c, SimTool tool)
    {
        if (_itemTime > 0) _itemTime--;
        if (!c.UseItem || c.CursorTile is not { } t || !InReach(t, tool == SimTool.Platform ? PlatformTileBoost : TileBoost)) return;
        var info = _g.Get(t);
        switch (tool)
        {
            case SimTool.Pickaxe:
                if (info.Kind is not (TileKind.Solid or TileKind.Platform)) return; // a swing at air
                if (info.PickPowerRequired > PickPower) return;
                int p = _digProgress.GetValueOrDefault(t) + 1;
                _digProgress[t] = p;
                if (p < DigTicks) return;
                _g.Set(t.X, t.Y, TileInfo.Air);
                _digProgress.Remove(t);
                Mined.Add(t);
                return;
            case SimTool.Platform:
                if (info.Kind is TileKind.Solid or TileKind.Platform)
                {
                    Misuse++;
                    return;
                }
                if (_itemTime > 0 || Platforms <= 0 || info.Kind is TileKind.Lava || !HasAnchor(t)) return;
                _g.Set(t.X, t.Y, new TileInfo(TileKind.Platform));
                Platforms--;
                Placed.Add(t);
                _itemTime = PlatformUseTime;
                return;
            default:
                Misuse++;
                return;
        }
    }

    private void Interact(ControlIntent c)
    {
        if (c.Interact is not { } t || !InInteractionRange(t)) return;
        if (_g.Get(t).IsClosedDoor)
        {
            var tiles = new List<TilePoint>();
            int top = t.Y, bottom = t.Y;
            while (_g.Get(t.X, top - 1).IsClosedDoor) top--;
            while (_g.Get(t.X, bottom + 1).IsClosedDoor) bottom++;
            for (int y = top; y <= bottom; y++) tiles.Add(new TilePoint(t.X, y));
            foreach (var p in tiles)
            {
                _g.Set(p.X, p.Y, new TileInfo(TileKind.Air, Protected: true, Glyph: 'F', Name: "Open Door"));
                _openDoors[p] = tiles;
            }
            DoorsOpened++;
        }
        else if (_openDoors.TryGetValue(t, out var door))
        {
            int x0 = (int)MathF.Floor((X + Eps) / 16f), x1 = (int)MathF.Floor((X + W - Eps) / 16f);
            if (t.X >= x0 && t.X <= x1) return; // someone in the doorway
            foreach (var p in door)
            {
                _g.Set(p.X, p.Y, ArrayTileGrid.ClosedDoor);
                _openDoors.Remove(p);
            }
            DoorsClosed++;
        }
    }

    /// <summary>Player.IsInTileInteractionRange with TileReachCheckSettings.Simple.</summary>
    public bool InInteractionRange(TilePoint t) =>
        X / 16f - TileRangeX <= t.X
        && (X + W) / 16f + TileRangeX - 1f >= t.X
        && Y / 16f - TileRangeY <= t.Y
        && (Y + H) / 16f + TileRangeY - 2f >= t.Y;

    /// <summary>Vanilla PlaceThing_Tiles / mining reach box (blockRange 0).</summary>
    public bool InReach(TilePoint t) => InReach(t, TileBoost);

    public bool InReach(TilePoint t, int boost) =>
        X / 16f - TileRangeX - boost <= t.X
        && (X + W) / 16f + TileRangeX + boost - 1f >= t.X
        && Y / 16f - TileRangeY - boost <= t.Y
        && (Y + H) / 16f + TileRangeY + boost - 2f >= t.Y;

    /// <summary>Vanilla: a tile on one of the 4 sides, or a background wall behind it (diagonals don't count).</summary>
    private bool HasAnchor(TilePoint t) =>
        _g.Get(t).Wall
        || _g.Get(t.X - 1, t.Y).IsStandable || _g.Get(t.X + 1, t.Y).IsStandable
        || _g.Get(t.X, t.Y - 1).IsStandable || _g.Get(t.X, t.Y + 1).IsStandable;

    private bool WalkSurface(int x, int y) =>
        (WaterWalk || LavaWalk) && LiquidWalk.IsSurface(_g, x, y, WaterWalk, LavaWalk);

    private bool Solid(int x, int y) => _g.Get(x, y).Kind == TileKind.Solid;
    private TileKind Kind(int x, int y) => _g.Get(x, y).Kind;

    private bool Overlaps(float x, float y, Func<TileKind, bool> pred)
    {
        int x0 = (int)MathF.Floor((x + Eps) / 16f), x1 = (int)MathF.Floor((x + W - Eps) / 16f);
        int y0 = (int)MathF.Floor((y + Eps) / 16f), y1 = (int)MathF.Floor((y + H - Eps) / 16f);
        for (int ty = y0; ty <= y1; ty++)
            for (int tx = x0; tx <= x1; tx++)
                if (pred(_g.Get(tx, ty).Kind)) return true;
        return false;
    }

    public override string ToString() =>
        $"t={Ticks} px=({X:F1},{Y:F1}) v=({VX:F2},{VY:F2}) tile={StandingTile} jump={_jump} wet={_wet}";
}
