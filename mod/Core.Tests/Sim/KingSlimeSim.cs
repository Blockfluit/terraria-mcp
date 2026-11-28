using LlmPlayer.Core.Combat;

namespace LlmPlayer.Core.Tests.Sim;

/// <summary>
/// A King-Slime-like hopper for the boss sim (vanilla aiStyle 15 as described on the wiki, Classic mode), on a flat
/// floor at <see cref="FloorPx"/> (px, the floor's top edge); it may leave the player's arena:
/// <list type="bullet">
/// <item>Hitbox 98x92 px × scale, scale 0.75 + 0.5 × life fraction (it shrinks as it loses life); bottom stays on the
/// floor while resting.</item>
/// <item>On the ground: horizontal speed × 0.8 per tick; a timer counts up by 2 per tick (+1 below 80% life, +1 below
/// 60%, +2 below 40%, +3 below 20%) from -120 (after a small jump) or -200 (after the big one); at 0 it faces the
/// player and jumps: a cycle of small (vy -8, vx +4), small, medium (vy -6, vx +4.5), big (vy -13, vx +3.5).</item>
/// <item>In the air: gravity 0.3 px/tick² (max fall 10); it keeps the direction it jumped in, nudging vx toward 3
/// px/tick only when slower. Contact 40.</item>
/// <item>Far from the player (more than <see cref="TeleportTiles"/>) for <see cref="TeleportAfter"/> ticks: it vanishes
/// and reappears <see cref="TeleportDelay"/> ticks later on the floor where the player stood when it left.</item>
/// <item>Every 4% of life lost it releases 2 Blue Slimes (24x18 px, 25 life, contact 7) that hop at the player.</item>
/// </list>
/// </summary>
public sealed class KingSlimeSim
{
    public const int LifeMax = 2000, Contact = 40, SlimeContact = 7, TeleportAfter = 300, TeleportDelay = 60;
    public const float Gravity = 0.3f, MaxFall = 10f, TeleportTiles = 40f, Friction = 0.8f;

    public readonly float FloorPx;
    public readonly SimFlyer Body = new() { Id = 0, Life = LifeMax, Contact = Contact };
    public readonly List<Hopper> Slimes = new();
    public int Jumps { get; private set; }
    public int Teleports { get; private set; }
    public int SlimesSpawned { get; private set; }
    public bool Grounded => _grounded;
    public bool Hidden => _teleportIn > 0;

    private float _timer = -60f;
    private int _cycle, _dir = 1, _far, _teleportIn, _nextId = 1, _lastSpawnLife = LifeMax;
    private bool _grounded = true;
    private float _teleX;

    public sealed class Hopper
    {
        public readonly SimFlyer Body;
        public int Timer, Dir = 1;
        public bool Grounded = true;
        public Hopper(SimFlyer body) => Body = body;
    }

    public KingSlimeSim(float centreX, float floorPx)
    {
        FloorPx = floorPx;
        Body.X = centreX;
        Resize();
        Body.Y = FloorPx - Body.HalfH;
    }

    /// <summary>Hitbox and motion for the AI, tiles: gravity 0.3 px/tick² (AttackTask's NpcGravityPx), friction 0.8.</summary>
    public EnemyMotion Motion => MotionOf(Body);

    public static EnemyMotion MotionOf(SimFlyer b) =>
        new(b.X / 16f, b.Y / 16f, b.HalfW / 16f, b.HalfH / 16f, b.VX / 16f, b.VY / 16f, Gravity / 16f, MaxFall / 16f, Friction);

    private void Resize()
    {
        float scale = 0.75f + 0.5f * Math.Max(0, Body.Life) / LifeMax;
        float bottom = Body.Y + Body.HalfH;
        Body.HalfW = 98f * scale / 2f;
        Body.HalfH = 92f * scale / 2f;
        if (_grounded) Body.Y = bottom - Body.HalfH;
    }

    public void Step(float playerCx, float playerCy, float playerBottom)
    {
        var b = Body;
        Resize();
        SpawnSlimes();
        if (_teleportIn > 0)
        {
            if (--_teleportIn == 0)
            {
                b.X = _teleX;
                b.Y = FloorPx - b.HalfH;
                b.VX = b.VY = 0f;
                _grounded = true;
                _timer = -60f;
            }
            StepSlimes(playerCx);
            return;
        }
        if (MathF.Abs(playerCx - b.X) / 16f > TeleportTiles) _far++;
        else _far = 0;
        if (_far >= TeleportAfter)
        {
            _far = 0;
            _teleportIn = TeleportDelay;
            _teleX = playerCx;
            Teleports++;
            b.X = -10000f; // gone
            StepSlimes(playerCx);
            return;
        }

        if (_grounded)
        {
            b.VX *= Friction;
            float f = (float)b.Life / LifeMax;
            _timer += 2f + (f < 0.8f ? 1 : 0) + (f < 0.6f ? 1 : 0) + (f < 0.4f ? 2 : 0) + (f < 0.2f ? 3 : 0);
            if (_timer >= 0f)
            {
                _dir = playerCx >= b.X ? 1 : -1;
                switch (_cycle)
                {
                    case 3:
                        b.VY = -13f; b.VX += 3.5f * _dir; _timer = -200f; _cycle = 0;
                        break;
                    case 2:
                        b.VY = -6f; b.VX += 4.5f * _dir; _timer = -120f; _cycle++;
                        break;
                    default:
                        b.VY = -8f; b.VX += 4f * _dir; _timer = -120f; _cycle++;
                        break;
                }
                _grounded = false;
                Jumps++;
            }
        }
        else if ((_dir == 1 && b.VX < 3f) || (_dir == -1 && b.VX > -3f))
        {
            if ((_dir == -1 && b.VX < 0.1f) || (_dir == 1 && b.VX > -0.1f)) b.VX += 0.2f * _dir;
            else b.VX *= 0.93f;
        }
        if (!_grounded) b.VY = Math.Min(b.VY + Gravity, MaxFall);
        b.X += b.VX;
        b.Y += b.VY;
        if (b.Y + b.HalfH >= FloorPx && b.VY >= 0f)
        {
            b.Y = FloorPx - b.HalfH;
            b.VY = 0f;
            _grounded = true;
        }
        StepSlimes(playerCx);
    }

    private void SpawnSlimes()
    {
        while (Body.Life <= _lastSpawnLife - LifeMax / 25 && Body.Life > 0)
        {
            _lastSpawnLife -= LifeMax / 25;
            for (int i = 0; i < 2; i++)
            {
                var s = new SimFlyer
                {
                    Id = _nextId++, X = Body.X + (i == 0 ? -20f : 20f), HalfW = 12f, HalfH = 9f, Life = 25, Contact = SlimeContact,
                };
                s.Y = Body.Hidden() ? FloorPx - s.HalfH : Body.Y;
                Slimes.Add(new Hopper(s) { Grounded = false, Timer = -30 - 20 * i });
                SlimesSpawned++;
            }
        }
    }

    private void StepSlimes(float playerCx)
    {
        foreach (var h in Slimes)
        {
            var s = h.Body;
            if (h.Grounded)
            {
                s.VX *= Friction;
                if (++h.Timer >= 0)
                {
                    h.Dir = playerCx >= s.X ? 1 : -1;
                    s.VY = -6f;
                    s.VX += 2f * h.Dir;
                    h.Timer = -60;
                    h.Grounded = false;
                }
            }
            if (!h.Grounded) s.VY = Math.Min(s.VY + Gravity, MaxFall);
            s.X += s.VX;
            s.Y += s.VY;
            if (s.Y + s.HalfH >= FloorPx && s.VY >= 0f)
            {
                s.Y = FloorPx - s.HalfH;
                s.VY = 0f;
                h.Grounded = true;
            }
        }
    }
}

internal static class SimFlyerExt
{
    public static bool Hidden(this SimFlyer f) => f.X < -5000f;
}
