using LlmPlayer.Core.Combat;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;

namespace LlmPlayer.Core.Tests.Sim;

/// <summary>A flying enemy in the boss sim: centre and velocity in px, half extents in px. Flies through tiles.</summary>
public sealed class SimFlyer
{
    public int Id;
    public float X, Y, VX, VY, HalfW, HalfH;
    public int Life, Contact;

    public EnemyMotion Motion => new(X / 16f, Y / 16f, HalfW / 16f, HalfH / 16f, VX / 16f, VY / 16f);

    public bool Touches(float left, float top, float w, float h) =>
        left < X + HalfW && left + w > X - HalfW && top < Y + HalfH && top + h > Y - HalfH;

    public bool Contains(float px, float py) => MathF.Abs(px - X) <= HalfW && MathF.Abs(py - Y) <= HalfH;
}

/// <summary>
/// An Eye-of-Cthulhu-like dasher for <see cref="MiniPhysics"/> (100x110 px, flies through blocks):
/// <list type="bullet">
/// <item>Phase 1: hover toward a point <see cref="HoverAbove"/> px above the player (accel 0.15, max 6 px/tick) for
/// <see cref="Hover1Ticks"/> ticks, spawning a Servant every 40 ticks (max 4 alive; 20x20 px, home in at up to
/// 3.5 px/tick); then 3 dashes: each re-aims at the player's centre at that moment and flies straight at
/// <see cref="Dash1Speed"/> px/tick for 45 ticks, then slows (×0.9/tick) for 15 ticks.</item>
/// <item>Phase 2 (from <see cref="Phase2At"/>): short hovers (45 ticks), then 5 dashes at <see cref="Dash2Speed"/>,
/// 35 ticks each with a 10-tick slow-down; contact 23. No more servants.</item>
/// </list>
/// Deterministic; the same script for every strategy.
/// </summary>
public sealed class EyeDasher
{
    public float Dash1Speed = 10f, Dash2Speed = 11f, HoverAbove = 12 * 16f;
    public int Hover1Ticks = 150, Phase2At = 1800;

    public readonly SimFlyer Eye = new() { Id = 0, HalfW = 50, HalfH = 55, Life = 2800, Contact = 15 };
    public readonly List<SimFlyer> Servants = new();
    public int DashesStarted { get; private set; }
    public int ServantsSpawned { get; private set; }

    private int _t, _stateT, _dashesLeft;
    private enum State { Hover, Dash, Slow }
    private State _state = State.Hover;
    private int _nextId = 1;

    public bool Phase2 => _t >= Phase2At;

    public EyeDasher(float x, float y)
    {
        Eye.X = x;
        Eye.Y = y;
    }

    public void Step(float playerCx, float playerCy)
    {
        _t++;
        _stateT++;
        var e = Eye;
        e.Contact = Phase2 ? 23 : 15;
        switch (_state)
        {
            case State.Hover:
            {
                float tx = playerCx, ty = playerCy - HoverAbove;
                float dx = tx - e.X, dy = ty - e.Y, l = MathF.Max(1f, MathF.Sqrt(dx * dx + dy * dy));
                e.VX = Math.Clamp(e.VX + dx / l * 0.15f, -6f, 6f);
                e.VY = Math.Clamp(e.VY + dy / l * 0.15f, -6f, 6f);
                if (!Phase2 && _stateT % 40 == 0 && Servants.Count < 4)
                {
                    Servants.Add(new SimFlyer { Id = _nextId++, X = e.X, Y = e.Y + 40, HalfW = 10, HalfH = 10, Life = 8, Contact = 12 });
                    ServantsSpawned++;
                }
                if (_stateT >= (Phase2 ? 45 : Hover1Ticks))
                {
                    _dashesLeft = Phase2 ? 5 : 3;
                    StartDash(playerCx, playerCy);
                }
                break;
            }
            case State.Dash:
                if (_stateT >= (Phase2 ? 35 : 45)) Go(State.Slow);
                break;
            case State.Slow:
                e.VX *= 0.9f;
                e.VY *= 0.9f;
                if (_stateT >= (Phase2 ? 10 : 15))
                {
                    if (_dashesLeft > 0) StartDash(playerCx, playerCy);
                    else Go(State.Hover);
                }
                break;
        }
        e.X += e.VX;
        e.Y += e.VY;

        foreach (var s in Servants)
        {
            float dx = playerCx - s.X, dy = playerCy - s.Y, l = MathF.Max(1f, MathF.Sqrt(dx * dx + dy * dy));
            s.VX += dx / l * 0.12f;
            s.VY += dy / l * 0.12f;
            float sp = MathF.Sqrt(s.VX * s.VX + s.VY * s.VY);
            if (sp > 3.5f)
            {
                s.VX *= 3.5f / sp;
                s.VY *= 3.5f / sp;
            }
            s.X += s.VX;
            s.Y += s.VY;
        }
    }

    private void StartDash(float pcx, float pcy)
    {
        var e = Eye;
        float dx = pcx - e.X, dy = pcy - e.Y, l = MathF.Max(1f, MathF.Sqrt(dx * dx + dy * dy));
        float speed = Phase2 ? Dash2Speed : Dash1Speed;
        e.VX = dx / l * speed;
        e.VY = dy / l * speed;
        _dashesLeft--;
        DashesStarted++;
        Go(State.Dash);
    }

    private void Go(State s)
    {
        _state = s;
        _stateT = 0;
    }
}

/// <summary>Straight arrows (no gravity): 10 px/tick, 90 ticks, 1 shot per <see cref="UseTime"/> ticks, 9 damage.</summary>
public sealed class SimBow
{
    public const float Speed = 10f;
    public const int UseTime = 26, Damage = 9, LifeTicks = 90;

    private readonly List<(float X, float Y, float VX, float VY, int Age)> _arrows = new();
    private int _cooldown;
    public int Shots { get; private set; }

    public void Fire(float fromX, float fromY, SimFlyer target)
    {
        if (_cooldown > 0) return;
        var (ax, ay) = AimLead.AimPoint(fromX, fromY, target.X, target.Y, target.VX, target.VY, Speed);
        float dx = ax - fromX, dy = ay - fromY, l = MathF.Max(1f, MathF.Sqrt(dx * dx + dy * dy));
        _arrows.Add((fromX, fromY, dx / l * Speed, dy / l * Speed, 0));
        _cooldown = UseTime;
        Shots++;
    }

    /// <summary>Move arrows; an arrow inside a flyer's box hits it (damage) and disappears. Returns hits per flyer id.</summary>
    public void Step(IEnumerable<SimFlyer> targets, Dictionary<int, int> hits)
    {
        if (_cooldown > 0) _cooldown--;
        var list = targets.ToList();
        for (int i = _arrows.Count - 1; i >= 0; i--)
        {
            var a = _arrows[i];
            a = (a.X + a.VX, a.Y + a.VY, a.VX, a.VY, a.Age + 1);
            var hit = list.FirstOrDefault(t => t.Life > 0 && t.Contains(a.X, a.Y));
            if (hit != null)
            {
                hit.Life -= Damage;
                hits[hit.Id] = hits.GetValueOrDefault(hit.Id) + 1;
                _arrows.RemoveAt(i);
            }
            else if (a.Age >= LifeTicks) _arrows.RemoveAt(i);
            else _arrows[i] = a;
        }
    }
}
