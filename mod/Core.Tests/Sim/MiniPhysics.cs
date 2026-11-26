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
/// <item>Water: gravity 0.2, max fall 5, jump 6.01 x30, displacement halved. No swimming without Flippers: a jump
/// still needs velocity.Y == 0 (the bottom). Leaving water cuts the remaining jump to 30/5 ticks.</item>
/// <item>Fall damage as vanilla: fallStart resets while jumping/wet/on ground; landing more than 25 tiles below it hurts.</item>
/// <item>Tools: reach is vanilla's tileRangeX/Y = 5/4 box with the pickaxe's tileBoost (-1 here, like early picks).
/// Pickaxe on a tile with enough power breaks it after <see cref="DigTicks"/> ticks of use; platforms are placed into
/// an empty tile next to any tile (3x3), every <see cref="PlatformUseTime"/> ticks, inside the hitbox allowed.
/// Using the wrong tool on a tile (e.g. a platform on a solid block, which vanilla would swap) counts as misuse.</item>
/// <item>Doors (Interact, within the 5/4 interaction box): a closed door's whole column of door tiles becomes air
/// (WorldGen.OpenDoor; the swing column isn't modelled: an open door is passable anyway); interacting with an open
/// one closes it again unless the hitbox overlaps its column (WorldGen.CloseDoor's Collision.EmptyTile check).</item>
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
    public int Ticks { get; private set; }
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

    public TilePoint StandingTile => NavMath.StandingTile(Kinematics);

    public void Step(ControlIntent c, SimTool tool = SimTool.None)
    {
        Ticks++;
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

        VY = Math.Min(VY + g, maxFall);

        if (VY >= g && !c.Down) StepUp(c.Up);

        bool fallThrough = c.Down;
        float vx = CollideX(VX);
        VX = vx;
        float vy = CollideY(VY, fallThrough, out bool up);
        VY = vy;
        if (_wet)
        {
            X += VX * 0.5f;
            Y += VY * 0.5f;
        }
        else
        {
            X += VX;
            Y += VY;
        }
        if (up)
        {
            VY = 0.01f;
            _jump = 0;
        }

        bool wetNow = Overlaps(X, Y, k => k is TileKind.Water or TileKind.Honey);
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
                    bool blocks = k == TileKind.Solid || (k == TileKind.Platform && !fallThrough);
                    if (blocks && Y + H <= ty * 16 + Eps) r = Math.Min(r, ty * 16 - (Y + H));
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
        if (!c.UseItem || c.CursorTile is not { } t || !InReach(t)) return;
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
    public bool InReach(TilePoint t) =>
        X / 16f - TileRangeX - TileBoost <= t.X
        && (X + W) / 16f + TileRangeX + TileBoost - 1f >= t.X
        && Y / 16f - TileRangeY - TileBoost <= t.Y
        && (Y + H) / 16f + TileRangeY + TileBoost - 2f >= t.Y;

    private bool HasAnchor(TilePoint t)
    {
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if ((dx != 0 || dy != 0) && _g.Get(t.X + dx, t.Y + dy).IsStandable) return true;
        return false;
    }

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
