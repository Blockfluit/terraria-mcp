using LlmPlayer.Bridge;
using LlmPlayer.Core.Combat;
using LlmPlayer.Observe;
using Terraria;

namespace LlmPlayer.Tasks;

/// <summary>
/// The weapon and ammo an attack (or auto-defend) fights with. The LLM decides: with a chosen weapon it is used
/// exactly; only when it becomes unusable (gone, out of ammo or mana) does the loadout fall back to the best
/// alternative (<see cref="WeaponFallback"/>, defense-aware, preferring the strategy's style), and switches back once
/// it is usable again. With fallback off it stops instead (<see cref="StopReason"/>). Without a chosen weapon it picks
/// the best one (re-chosen every second, the old auto behaviour). A chosen ammo is put first in vanilla's ammo order
/// (<see cref="AmmoOrder"/>) by swapping stacks; <see cref="Restore"/> swaps them back. Every switch is reported
/// (<see cref="Switches"/>, a <c>weapon_switch</c> event and a recent event). Main thread only.
/// </summary>
public sealed class WeaponLoadout
{
    public const int RecheckTicks = 60;

    private readonly Item? _chosen;
    private readonly string? _chosenName;
    private readonly int _ammoType, _ammoClass;
    private readonly string? _ammoName;
    private readonly string _who;
    private readonly SwapJournal<Item> _ammoSwaps = new();
    private readonly List<string> _switches = new();

    private Weapon? _current;
    private int _age;
    private WeaponIssue _fallbackIssue; // None = not in fallback
    private bool _ammoOut;

    public bool Fallback { get; }
    public AttackStrategy Strategy { get; }

    /// <summary>Set when the task must stop (fallback off, or nothing usable is left after a fallback).</summary>
    public string? StopReason { get; private set; }

    public IReadOnlyList<string> Switches => _switches;
    public bool Auto => _chosen == null;

    /// <param name="weapon">The chosen weapon (an inventory item), or null = pick the best (auto).</param>
    /// <param name="ammo">The chosen ammo stack (its item type is what counts), or null = vanilla's order.</param>
    /// <param name="who">"attack" or "auto-defend", for the messages.</param>
    public WeaponLoadout(Item? weapon, Item? ammo, bool fallback, AttackStrategy strategy, string who = "attack")
    {
        _chosen = weapon;
        _chosenName = weapon?.Name;
        _ammoType = ammo?.type ?? 0;
        _ammoClass = ammo?.ammo ?? 0;
        _ammoName = ammo?.Name;
        Fallback = fallback;
        Strategy = strategy;
        _who = who;
    }

    /// <summary>The weapon to use this tick, or null (then <see cref="StopReason"/> says why, or nothing is usable).</summary>
    /// <param name="applyAmmo">Also put the chosen ammo first now (auto-defend only does that once it fights:
    /// <see cref="ApplyAmmo"/>).</param>
    public Weapon? Tick(Player p, int targetDefense, bool applyAmmo = true)
    {
        StopReason = null;
        var w = _chosen == null ? TickAuto(p, targetDefense) : TickChosen(p, targetDefense);
        if (w != null && applyAmmo && !ApplyAmmo(p)) return null;
        // Ammo order changes the shot's damage/speed (aim lead): refresh the stats.
        if (w != null) _current = w = CombatKit.Candidate(p, w.Slot) is { } c ? w with { Stats = c } : w;
        return w;
    }

    /// <summary>Name of the ammo the current weapon fires now (vanilla's choice after our ordering), or null.</summary>
    public string? AmmoInUse(Player p) =>
        _current is { } w && w.Item.useAmmo > 0 ? p.ChooseAmmo(w.Item)?.Name : null;

    /// <summary>"Wooden Bow + Flaming Arrow" style label for progress strings.</summary>
    public string Label(Player p)
    {
        if (_current is not { } w) return "no weapon";
        string s = w.Item.Name + (AmmoInUse(p) is { } a ? $" + {a}" : "");
        if (_fallbackIssue != WeaponIssue.None) s += $" (fallback: {_chosenName} {WeaponFallback.Describe(_fallbackIssue)})";
        else if (_chosen == null) s += " (auto)";
        return s;
    }

    /// <summary>Put moved ammo stacks back where they were (only if both are still where we left them).</summary>
    public void Restore(Player p) => _ammoSwaps.Restore(p.inventory);

    private Weapon? TickAuto(Player p, int defense)
    {
        bool unusable = _current != null && !CombatKit.StillUsable(p, _current);
        if (_current != null && !unusable && --_age > 0) return Relocate(p, _current);
        var old = _current;
        _current = CombatKit.BestWeapon(p, defense, Strategies.RequiredStyle(Strategy), _ammoClass);
        _age = RecheckTicks;
        if (unusable && old != null && _current != null && !ReferenceEquals(old.Item, _current.Item))
            Switch(WeaponFallback.SwitchMessage(old.Item.Name, Issue(p, old.Item), _current.Item.Name, _who), old.Item.Name, _current.Item.Name);
        return _current;
    }

    private Weapon? TickChosen(Player p, int defense)
    {
        var item = _chosen!;
        int slot = CombatKit.Locate(p, item);
        var issue = slot >= 0 ? Issue(p, p.inventory[slot]) : WeaponIssue.Gone;

        if (_fallbackIssue != WeaponIssue.None)
        {
            bool back = issue == WeaponIssue.None
                        && WeaponFallback.MayReturn(_fallbackIssue, p.statMana, p.statManaMax2, p.inventory[slot].mana);
            if (back)
            {
                string from = _current?.Item.Name ?? "nothing";
                _fallbackIssue = WeaponIssue.None;
                Switch(WeaponFallback.BackMessage(_chosenName!, _who), from, _chosenName!);
            }
            else
            {
                // Keep the fallback weapon; re-pick only when it stops working or every second.
                if (_current != null && CombatKit.StillUsable(p, _current) && --_age > 0) return Relocate(p, _current);
                return PickFallback(p, defense, issue == WeaponIssue.None ? _fallbackIssue : issue, slot);
            }
        }

        if (issue == WeaponIssue.None)
            return _current = new Weapon(slot, p.inventory[slot], CombatKit.Candidate(p, slot)!.Value);
        return PickFallback(p, defense, issue, slot);
    }

    private Weapon? PickFallback(Player p, int defense, WeaponIssue issue, int chosenSlot)
    {
        var d = WeaponFallback.Decide(_chosenName!, chosenSlot, issue, Fallback,
            CombatKit.Candidates(p, chosenSlot >= 0 ? p.inventory[chosenSlot] : null), defense, Strategies.RequiredStyle(Strategy));
        _age = RecheckTicks;
        if (d.Kind == WeaponDecisionKind.Stop)
        {
            StopReason = d.Reason;
            _current = null;
            return null;
        }
        var next = new Weapon(d.Slot, p.inventory[d.Slot], CombatKit.Candidate(p, d.Slot)!.Value);
        bool changed = _fallbackIssue == WeaponIssue.None || _current == null || !ReferenceEquals(_current.Item, next.Item);
        _fallbackIssue = issue;
        if (changed)
            Switch(WeaponFallback.SwitchMessage(_chosenName!, issue, next.Item.Name, _who), _current?.Item.Name ?? _chosenName!, next.Item.Name);
        return _current = next;
    }

    /// <summary>Make the chosen ammo the stack the current weapon fires next (no-op without a chosen ammo, or when the
    /// weapon uses another ammo class). False = stop (it ran out and fallback is off).</summary>
    public bool ApplyAmmo(Player p)
    {
        if (_ammoType == 0 || _current is not { } w || w.Item.useAmmo != _ammoClass) return true;
        var slots = CombatKit.AmmoSlots(p);
        var plan = AmmoOrder.Plan(slots, _ammoClass, _ammoType);
        switch (plan.Kind)
        {
            case AmmoPlanKind.Swap:
                _ammoOut = false;
                if (plan.A == p.selectedItem || plan.B == p.selectedItem) return true; // never move the held item
                _ammoSwaps.Swap(p.inventory, plan.A, plan.B);
                return true;
            case AmmoPlanKind.Ready:
                _ammoOut = false;
                return true;
            default: // Missing (WrongAmmo can't happen: the command checked the class)
                if (!Fallback)
                {
                    StopReason = $"{_ammoName} ran out, fallback is off";
                    return false;
                }
                if (!_ammoOut)
                {
                    _ammoOut = true;
                    int alt = AmmoOrder.Alternative(slots, _ammoClass, _ammoType);
                    if (alt >= 0)
                        Switch(WeaponFallback.AmmoSwitchMessage(_ammoName!, p.inventory[alt].Name, _who), _ammoName!, p.inventory[alt].Name);
                }
                return true;
        }
    }

    private static Weapon? Relocate(Player p, Weapon w)
    {
        int s = CombatKit.Locate(p, w.Item);
        return s < 0 || s == w.Slot ? w : w with { Slot = s };
    }

    private static WeaponIssue Issue(Player p, Item it)
    {
        int s = CombatKit.Locate(p, it);
        if (s < 0) return WeaponIssue.Gone;
        var c = CombatKit.Candidate(p, s);
        return WeaponFallback.Diagnose(true, it.useAmmo > 0, it.useAmmo <= 0 || p.HasAmmo(it),
            it.mana > 0, it.mana <= 0 || p.CheckMana(it, -1, false, true), c is { Usable: true });
    }

    private void Switch(string message, string from, string to)
    {
        _switches.Add(message);
        RecentEvents.Add(message);
        BridgeSystem.Emit(EventNames.WeaponSwitch, new { By = _who, From = from, To = to, Message = message });
    }
}
