namespace LlmPlayer.Core.Combat;

/// <summary>What the running task allows reflexes to do (see <see cref="TaskPhases"/> for which task is which).
/// Idle = no task, or a task that only waits; Defendable = a task auto-defend may pause (moving, exploring, walking
/// to an NPC, gathering, placing, picking up, waiting to switch items): it is not ticked while the defence runs and
/// resumes afterwards; Busy = a task that keeps the controls (attack, or a one-shot item use already pressed).</summary>
public enum TaskPhase { Idle, Defendable, Busy }

/// <param name="ThreatInReach">A visible attackable NPC is within the current weapon's reach (and, for ranged
/// weapons, in line of sight), or touched the player within the last second and is just outside reach.</param>
/// <param name="UsingItem">The player is mid-use (itemAnimation or itemTime &gt; 0). Ending a defence waits for the
/// use to finish (at most <see cref="ReflexArbiter.MaxReleaseWaitTicks"/>) so the held item isn't swapped back
/// mid-swing.</param>
public readonly record struct ReflexInputs(
    TaskPhase Task, bool AutoDefend, bool ThreatInReach, int Life, int LifeMax, float HealThreshold, bool PotionSick,
    bool HasHealingPotion, bool UsingItem = false);

/// <param name="QuickHeal">Call Player.QuickHeal this tick (uses no controls).</param>
/// <param name="Defend">Reflexes own the controls this tick: the task is not ticked (paused, not cancelled).</param>
public readonly record struct ReflexDecision(bool QuickHeal, bool Defend, bool DefendStarted, bool DefendEnded);

/// <summary>
/// Per-tick reflex decisions while the AI drives. Precedence, so the reflexes and the task never both write the
/// controls in one tick:
/// <list type="number">
/// <item>Quick-heal first, at any time (also during Busy tasks and while defending): below
/// <c>HealThreshold × LifeMax</c>, no Potion Sickness and a healing potion in the inventory. It is a direct game call
/// (the quick-heal hotkey's action), not a control, so it conflicts with nothing. Retried at most every
/// <see cref="HealRetryTicks"/>.</item>
/// <item>Auto-defend owns the controls when enabled, the task phase is Idle or Defendable (idle, moving, gathering,
/// placing, picking up, …), and a threat is in reach. It keeps them until no threat has been in reach for
/// <see cref="ReleaseGraceTicks"/> (so a slime bouncing in and out doesn't flip control every tick) and the current
/// item use has finished, then hands back and the paused task resumes where it left off.</item>
/// <item>A Busy task (attack, a pressed one-shot item use) always keeps the controls; switching to one ends a defence
/// immediately.</item>
/// <item>Otherwise the task (if any) writes the controls.</item>
/// </list>
/// </summary>
public sealed class ReflexArbiter
{
    public const int ReleaseGraceTicks = 20;
    public const int HealRetryTicks = 30;
    /// <summary>After the grace period, wait at most this long for the defender's swing to end before handing back.</summary>
    public const int MaxReleaseWaitTicks = 60;

    private int _noThreatTicks;
    private int _releaseWait;
    private int _healCooldown;

    public bool Defending { get; private set; }

    public ReflexDecision Tick(in ReflexInputs i)
    {
        bool heal = false;
        if (_healCooldown > 0) _healCooldown--;
        if (_healCooldown == 0 && i.Life > 0 && i.Life < i.HealThreshold * i.LifeMax && !i.PotionSick && i.HasHealingPotion)
        {
            heal = true;
            _healCooldown = HealRetryTicks;
        }

        bool started = false, ended = false;
        bool allowed = i.AutoDefend && i.Task != TaskPhase.Busy;
        if (!allowed)
        {
            ended = Defending;
            StopDefending();
        }
        else if (i.ThreatInReach)
        {
            started = !Defending;
            Defending = true;
            _noThreatTicks = 0;
            _releaseWait = 0;
        }
        else if (Defending)
        {
            if (_noThreatTicks < ReleaseGraceTicks) _noThreatTicks++;
            if (_noThreatTicks >= ReleaseGraceTicks)
            {
                if (i.UsingItem && _releaseWait < MaxReleaseWaitTicks) _releaseWait++;
                else
                {
                    StopDefending();
                    ended = true;
                }
            }
        }

        return new ReflexDecision(heal, Defending, started, ended);
    }

    /// <summary>Forget everything (driver change, world unload). No DefendEnded is reported.</summary>
    public void Reset()
    {
        StopDefending();
        _healCooldown = 0;
    }

    private void StopDefending()
    {
        Defending = false;
        _noThreatTicks = 0;
        _releaseWait = 0;
    }
}
