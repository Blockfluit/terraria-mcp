namespace LlmPlayer.Core.Nav;

/// <summary>
/// Derives the jump part of a <see cref="MovementProfile"/> from the player's physics by simulating jumps with
/// vanilla's update order (HorizontalMovement → JumpMovement → gravity → move). Everything is deliberately
/// pessimistic, so a planned jump is one the player can really make:
/// <list type="bullet">
/// <item>Standing start: no run-up speed is assumed.</item>
/// <item>Launch level h0: the lowest level at which the body can cross the whole span. Anything lower in between
/// (a wall, a pillar, the ledge itself) stops sideways motion until the feet clear it; the worst case is assumed:
/// no sideways progress until the feet are h0 tiles up, then accelerating from rest. On the way down the arc must
/// be fully over the target's columns before it sinks back to h0, or the obstacle catches it. h0 = 0 means
/// nothing in the way.</item>
/// <item>Ceiling c: with room for the body to rise only c tiles the jump is cut there (vanilla bonk: velocity.Y =
/// 0.01, jump = 0), which shortens the air time. Releasing early to stay under a platform gives a softer apex with
/// more air time, so the bonk model covers that too.</item>
/// <item>Takeoff with the hitbox left edge at <c>s.X + 0.75</c> (the follower jumps at the support edge or when
/// blocked); landing needs the hitbox left edge at <c>t.X - 0.125</c> or beyond (half a tile short of the
/// centred stance still overlaps t's support), minus a quarter-tile margin. A higher target additionally needs
/// <see cref="LedgeOverlapPx"/> of sideways travel before the feet sink below it.</item>
/// <item>Collisions are otherwise ignored; vanilla's auto step-up when falling against a ledge only helps.</item>
/// </list>
/// </summary>
public static class JumpModel
{
    private const float TakeoffX = 0.75f, LandX = -0.125f, MarginTiles = 0.25f;
    public const float LedgeOverlapPx = 4f;
    private const int MaxUpModelled = 29, MaxGapModelled = 32, MaxTicks = 900;

    /// <summary>Vanilla in water (not honey): jumpSpeed 6.01 held for jumpHeight 30 ticks, gravity 0.2, max fall 5,
    /// and the position moves by only half the velocity each tick (same constants as the MiniPhysics sim).</summary>
    public const float WaterJumpSpeed = 6.01f, WaterGravity = 0.2f, WaterMaxFall = 5f, WaterMoveFactor = 0.5f;
    public const int WaterJumpHeight = 30;

    /// <summary><see cref="WaterJumpUp"/> for vanilla (8: ~131 px).</summary>
    public const int VanillaWaterJumpUp = 8;

    /// <summary>Whole tiles the feet rise in one held jump off the bottom of water (no Flippers: the only way up),
    /// while the body is still in the water the whole time.</summary>
    public static int WaterJumpUp() => (int)MathF.Floor(WaterJumpRisePx() / PlayerPhysics.TilePx);

    /// <summary>Apex height in pixels of a held jump off the bottom of water (vanilla update order: jump, gravity,
    /// half-speed move).</summary>
    public static float WaterJumpRisePx()
    {
        float vy = -WaterJumpSpeed, rise = 0;
        int jump = WaterJumpHeight;
        for (int tick = 0; tick < MaxTicks; tick++)
        {
            if (tick > 0 && jump > 0)
            {
                vy = -WaterJumpSpeed;
                jump--;
            }
            vy = Math.Min(vy + WaterGravity, WaterMaxFall);
            if (vy >= 0) break;
            rise -= vy * WaterMoveFactor;
        }
        return rise;
    }

    /// <summary>Copy of <paramref name="baseProfile"/> with MaxJumpUp, MaxJumpGap and JumpReach from physics.</summary>
    public static MovementProfile Apply(MovementProfile baseProfile, PlayerPhysics phys)
    {
        var free = Vertical(phys, float.MaxValue);
        int maxUp = 0;
        for (int h = 1; h <= MaxUpModelled; h++)
        {
            if (Reach(free, phys, dy: -h, launch: h) < 1) break;
            maxUp = h;
        }

        int width = 2 * maxUp + 1, levels = maxUp + 1;
        var reach = new int[levels * (levels + 1) * width];
        for (int c = 1; c <= levels; c++)
        {
            var v = c == levels ? free : Vertical(phys, c * 16f);
            for (int h0 = 0; h0 <= levels; h0++)
                for (int dy = -maxUp; dy <= maxUp; dy++)
                {
                    int k = h0 > c || -dy >= c ? 0 : Reach(v, phys, dy, h0);
                    reach[MovementProfile.ReachIndex(maxUp, c, h0, dy)] = Math.Clamp(k, 0, MaxGapModelled);
                }
        }
        return baseProfile with
        {
            MaxJumpUp = maxUp, MaxJumpGap = reach.Max(), JumpReach = reach, MaxWaterJumpUp = WaterJumpUp(),
        };
    }

    private readonly record struct Sample(float Rise, float Vy);

    /// <summary>Largest |dx| for landing dy rows lower when sideways motion starts once the feet are `launch` up.</summary>
    private static int Reach(List<Sample> v, PlayerPhysics p, int dy, int launch)
    {
        int start = 0; // ticks before this one make no sideways progress
        if (launch > 0)
        {
            start = -1;
            for (int i = 1; i < v.Count; i++)
                if (v[i].Rise >= launch * 16f) { start = i; break; }
            if (start < 0) return 0;
        }
        // The span can only be crossed at or above the launch level, so the arc must already be over the target's
        // columns (fully clear of whatever is in between) when it comes back down to that level.
        bool obstacle = launch > -dy;
        float landRise = Math.Max(-dy, launch) * 16f;
        float landX = obstacle ? 0f : LandX;
        float x = 0, vx = 0;
        for (int i = 1; i < v.Count; i++)
        {
            int tick = i - 1;
            if (tick >= start)
            {
                vx = RunRight(vx, onGround: tick == 0, p);
                x += vx;
            }
            if (v[i].Vy > 0 && v[i].Rise <= landRise && i > start)
            {
                int k = (int)MathF.Floor(x / 16f + TakeoffX - landX - MarginTiles);
                if (dy < 0) return x >= LedgeOverlapPx ? Math.Max(k, 1) : 0;
                return k;
            }
        }
        return 0;
    }

    /// <summary>Per tick after the move: feet height above takeoff and vertical velocity. A ceiling caps the rise
    /// like a head bonk does.</summary>
    private static List<Sample> Vertical(PlayerPhysics p, float ceilingPx)
    {
        var list = new List<Sample> { new(0, 0) };
        float rise = 0, vy = 0;
        int jump = 0;
        for (int tick = 0; tick < MaxTicks; tick++)
        {
            if (tick == 0)
            {
                vy = -p.JumpSpeed;
                jump = p.JumpHeight;
            }
            else if (jump > 0)
            {
                vy = -p.JumpSpeed;
                jump--;
            }
            vy = Math.Min(vy + p.Gravity, p.MaxFallSpeed);
            rise -= vy;
            if (rise > ceilingPx)
            {
                rise = ceilingPx;
                vy = 0.01f;
                jump = 0;
            }
            list.Add(new Sample(rise, vy));
            if (rise < -(MaxUpModelled + 2) * 16f) break;
        }
        return list;
    }

    /// <summary>Vanilla HorizontalMovement with the right key held (no run boots: accRunSpeed == maxRunSpeed).</summary>
    internal static float RunRight(float vx, bool onGround, PlayerPhysics p)
    {
        if (vx < p.MaxRunSpeed)
        {
            if (vx < -p.RunSlowdown) vx += p.RunSlowdown;
            return vx + p.RunAcceleration;
        }
        float slow = onGround ? p.RunSlowdown : p.RunSlowdown * 0.5f;
        return vx > slow ? vx - slow : 0f;
    }
}
