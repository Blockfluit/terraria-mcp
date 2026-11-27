using LlmPlayer.Control;
using LlmPlayer.Core.Combat;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using LlmPlayer.Core.Tasks;
using Terraria;

namespace LlmPlayer.Tasks;

/// <summary>
/// Use an item once, like one click: hold it (<see cref="HeldItemLease"/>: hotbar select, or swap a main-inventory
/// item into the selected hotbar slot), aim at the target tile every tick, press "use" until the game starts the
/// use, then release and wait for the animation to finish. The previously held item comes back afterwards.
/// <list type="bullet">
/// <item>Fails up front for potion sickness (a potion while <c>Player.potionDelay</c> &gt; 0).</item>
/// <item>Fails if the game doesn't start the use within <see cref="StartTimeoutTicks"/> of pressing (another
/// cooldown, an unmet condition, a target out of reach, …). Vanilla ItemCheck enforces reach, tool power and
/// cooldowns; nothing here bypasses it.</item>
/// <item>The aim (a tile) is applied every tick, during the press and the whole animation, so a swing never follows
/// the real mouse. Without an explicit target the aim is two tiles in front of the player.</item>
/// <item>Auto-defend may pause it only while it waits to switch items; from the first press on it is
/// <see cref="Uninterruptible"/> (pausing then could repeat the use, or move the aim mid-animation).</item>
/// </list>
/// </summary>
public sealed class UseItemTask : IGameTask<GameCtx>, IUninterruptibleTask
{
    public const int StartTimeoutTicks = 60, MaxAnimationTicks = 600, HoldTimeoutTicks = 300;

    private int _slot;
    private readonly Item _item;
    private readonly string _itemName;
    private readonly TilePoint? _aim;
    private readonly HeldItemLease _lease = new();
    private int _pressTicks, _animTicks, _holdTicks, _stackBefore, _lifeBefore;
    private bool _started;

    public string Name => "use_item";
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }

    /// <summary>"Use" was pressed: keep the controls until the use is over.</summary>
    public bool Uninterruptible => _pressTicks > 0 || _started;

    public UseItemTask(Player p, int slot, TilePoint? aim)
    {
        _slot = slot;
        _item = p.inventory[slot];
        _itemName = _item.Name;
        _aim = aim;
    }

    public GameTaskStatus Tick(GameCtx ctx)
    {
        var p = ctx.Player;
        var aim = _aim ?? InFront(p);

        if (_started)
        {
            // Release "use" (one use only) but keep aiming until the animation ends.
            ControlState.Pending = default(ControlIntent) with { CursorTile = aim };
            if ((p.itemAnimation == 0 && p.itemTime == 0) || ++_animTicks > MaxAnimationTicks)
                return Finish(ctx, GameTaskStatus.Succeeded, $"used {_itemName}{Effect(p)}");
            return GameTaskStatus.Running;
        }

        if (_pressTicks > 0 && UseBegan(p))
        {
            _started = true;
            Progress = "using";
            ControlState.Pending = default(ControlIntent) with { CursorTile = aim };
            return GameTaskStatus.Running;
        }

        if (!TaskItems.StillHas(p, _item))
            return Finish(ctx, GameTaskStatus.Failed, $"{_itemName} is no longer in the inventory");
        if (_item.potion && p.potionDelay > 0)
            return Finish(ctx, GameTaskStatus.Failed, $"potion sickness: can't drink {_itemName} for another {(p.potionDelay + 59) / 60}s");

        // Follow the item if something moved it since the command picked its slot (see SlotTracking).
        if (!_lease.IsHeld(p, _slot) && !ReferenceEquals(p.inventory[_slot], _item))
            _slot = TaskItems.Track(p, _slot, _item, -1);
        if (_slot < 0) return Finish(ctx, GameTaskStatus.Failed, $"{_itemName} is no longer in the inventory");
        if (!_lease.Hold(p, _slot))
        {
            ControlState.Pending = default(ControlIntent) with { CursorTile = aim };
            Progress = "waiting to switch items";
            if (++_holdTicks > HoldTimeoutTicks) return Finish(ctx, GameTaskStatus.Failed, "couldn't switch to the item (busy using another item)");
            return GameTaskStatus.Running;
        }

        if (_pressTicks == 0)
        {
            _stackBefore = _item.stack;
            _lifeBefore = p.statLife;
        }
        if (++_pressTicks > StartTimeoutTicks)
            return Finish(ctx, GameTaskStatus.Failed,
                $"the game didn't use {_itemName} (on cooldown, conditions not met, or target out of reach/invalid)");
        Progress = "pressing use";
        ControlState.Pending = default(ControlIntent) with { UseItem = true, CursorTile = aim };
        return GameTaskStatus.Running;
    }

    public void OnCancel(GameCtx ctx) => Cleanup(ctx);

    private bool UseBegan(Player p) =>
        p.itemAnimation > 0 || p.itemTime > 0 || _item.stack < _stackBefore || _item.IsAir;

    private string Effect(Player p) =>
        p.statLife != _lifeBefore ? $" (life {_lifeBefore} -> {p.statLife})" : "";

    private static TilePoint InFront(Player p) =>
        new((int)(p.Center.X / 16f) + 2 * p.direction, (int)(p.Center.Y / 16f));

    private GameTaskStatus Finish(GameCtx ctx, GameTaskStatus status, string reason)
    {
        Reason = reason;
        Cleanup(ctx);
        return status;
    }

    private void Cleanup(GameCtx ctx)
    {
        ControlState.Pending = default;
        _lease.Restore(ctx.Player);
    }
}

/// <summary>Small inventory helpers shared by item-using tasks.</summary>
public static class TaskItems
{
    /// <summary>The slot holding <paramref name="item"/> now (<see cref="Core.Inventory.SlotTracking"/>): the
    /// remembered slot, else wherever that Item object moved, else the first stack of the same type; -1 if gone.</summary>
    public static int Track(Player p, int slot, Item item, int type) =>
        Core.Inventory.SlotTracking.Resolve<Item>(p.inventory.Take(58).ToArray(), slot, item, it => it.IsAir, (a, _) => a.type == type);

    /// <summary>Is this exact Item object still a non-empty stack somewhere in inventory slots 0-57?</summary>
    public static bool StillHas(Player p, Item item)
    {
        if (item.IsAir) return false;
        for (int i = 0; i < 58; i++)
            if (ReferenceEquals(p.inventory[i], item)) return true;
        return false;
    }
}
