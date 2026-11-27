using LlmPlayer.Control;
using LlmPlayer.Core.Combat;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Inventory;
using LlmPlayer.Core.Nav;
using LlmPlayer.Core.Tasks;
using Terraria;

namespace LlmPlayer.Tasks;

/// <summary>
/// Use an item once, like one click: hold it (<see cref="HeldItemLease"/>: hotbar select, or swap a main-inventory
/// item into the selected hotbar slot), aim at the target tile every tick, wait for any swing/use still running to
/// end, press "use" until the game starts the use, then release and wait for the animation to finish. The previously
/// held item comes back afterwards.
/// <list type="bullet">
/// <item>Verified (<see cref="UseOutcome"/>): a consumable must have lost one from its stack, a buff item must have
/// its buff; otherwise the use fails with a reason instead of reporting success.</item>
/// <item>Queue: use_item while another use_item runs is appended (<see cref="TryEnqueue"/>) instead of cancelling it.
/// Seen in game: three potions used in a row before a boss, the first call was replaced by the second before it
/// pressed and its buff never came. The report lists every use; the task fails if any of them failed.</item>
/// <item>Fails a use for potion sickness (a potion while <c>Player.potionDelay</c> &gt; 0).</item>
/// <item>Fails a use if the game doesn't start it within <see cref="StartTimeoutTicks"/> of pressing (another
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
    public const int StartTimeoutTicks = 60, MaxAnimationTicks = 600, HoldTimeoutTicks = 300, MaxQueued = 10;

    private sealed class Use(int slot, Item item, TilePoint? aim)
    {
        public int Slot = slot;
        public readonly Item Item = item;
        public readonly int Type = item.type;
        public readonly string Name = item.Name;
        public readonly bool Consumable = item.consumable;
        public readonly bool Potion = item.potion;
        public readonly int BuffType = item.buffType;
        public readonly TilePoint? Aim = aim;
        public int PressTicks, AnimTicks, HoldTicks, GraceTicks, StackBefore, LifeBefore;
        public bool Started, Ended;
    }

    private readonly Queue<Use> _queue = new();
    private Use? _cur;
    private readonly List<UseResult> _results = new();
    private readonly HeldItemLease _lease = new();

    public string Name => "use_item";
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }
    public object? Result => _results.Count > 0 ? new { Used = _results } : null;

    /// <summary>"Use" was pressed: keep the controls until the use is over.</summary>
    public bool Uninterruptible => _cur is { } u && (u.PressTicks > 0 || u.Started);

    public UseItemTask(Player p, int slot, TilePoint? aim) => _cur = new Use(slot, p.inventory[slot], aim);

    /// <summary>Queue another use behind the current one(s); false when the queue is full.</summary>
    public bool TryEnqueue(Player p, int slot, TilePoint? aim)
    {
        if (_queue.Count >= MaxQueued) return false;
        _queue.Enqueue(new Use(slot, p.inventory[slot], aim));
        return true;
    }

    public int Queued => _queue.Count + (_cur != null ? 1 : 0);

    public GameTaskStatus Tick(GameCtx ctx)
    {
        if (_cur == null && !_queue.TryDequeue(out _cur)) return Done(ctx);
        var u = _cur!;
        var r = TickUse(ctx.Player, u);
        if (r == null) return GameTaskStatus.Running;
        _results.Add(r);
        _cur = null;
        ControlState.Pending = default;
        if (_queue.Count == 0) return Done(ctx);
        return GameTaskStatus.Running;
    }

    /// <summary>One tick of the current use; its result once it is over.</summary>
    private UseResult? TickUse(Player p, Use u)
    {
        var aim = u.Aim ?? InFront(p);
        string tag = _queue.Count > 0 || _results.Count > 0 ? $"{u.Name}: " : "";

        if (u.Ended)
        {
            // Consumption / buff land within the use; a few more ticks cover a late frame before calling it failed.
            ControlState.Pending = default(ControlIntent) with { CursorTile = aim };
            var o = Observe(p, u);
            if (UseOutcome.Happened(o) || ++u.GraceTicks > UseOutcome.GraceTicks) return UseOutcome.Judge(o);
            return null;
        }

        if (u.Started)
        {
            // Release "use" (one use only) but keep aiming until the animation ends.
            ControlState.Pending = default(ControlIntent) with { CursorTile = aim };
            if ((p.itemAnimation == 0 && p.itemTime == 0) || ++u.AnimTicks > MaxAnimationTicks) u.Ended = true;
            return null;
        }

        if (u.PressTicks > 0 && UseBegan(p, u))
        {
            u.Started = true;
            Progress = tag + "using";
            ControlState.Pending = default(ControlIntent) with { CursorTile = aim };
            return null;
        }

        if (u.PressTicks == 0)
        {
            if (!TaskItems.StillHas(p, u.Item))
            {
                // Merged into another stack or replaced: follow the same item type.
                int slot = TaskItems.Track(p, u.Slot, u.Item, u.Type);
                if (slot < 0) return new UseResult(u.Name, false, $"{u.Name} is no longer in the inventory");
                _cur = new Use(slot, p.inventory[slot], u.Aim) { HoldTicks = u.HoldTicks };
                return null;
            }
            if (u.Potion && p.potionDelay > 0)
                return new UseResult(u.Name, false, $"potion sickness: can't drink {u.Name} for another {(p.potionDelay + 59) / 60}s");

            // Follow the item if something moved it since the command picked its slot (see SlotTracking).
            if (!_lease.IsHeld(p, u.Slot) && !ReferenceEquals(p.inventory[u.Slot], u.Item))
                u.Slot = TaskItems.Track(p, u.Slot, u.Item, u.Type);
            if (u.Slot < 0) return new UseResult(u.Name, false, $"{u.Name} is no longer in the inventory");
            // Hold it, and let any swing or use still under way finish first: pressing during it would be ignored (or
            // count the old animation as this use).
            if (!_lease.Hold(p, u.Slot) || p.itemAnimation > 0 || p.itemTime > 0 || p.channel)
            {
                ControlState.Pending = default(ControlIntent) with { CursorTile = aim };
                Progress = tag + "waiting to switch items";
                if (++u.HoldTicks > HoldTimeoutTicks)
                    return new UseResult(u.Name, false, $"couldn't switch to {u.Name} (busy using another item)");
                return null;
            }
            u.StackBefore = u.Item.stack;
            u.LifeBefore = p.statLife;
        }

        if (++u.PressTicks > StartTimeoutTicks)
            return new UseResult(u.Name, false,
                $"the game didn't use {u.Name} (on cooldown, conditions not met, or target out of reach/invalid)");
        Progress = tag + "pressing use";
        ControlState.Pending = default(ControlIntent) with { UseItem = true, CursorTile = aim };
        return null;
    }

    public void OnCancel(GameCtx ctx) => Cleanup(ctx);

    private static bool UseBegan(Player p, Use u) =>
        p.itemAnimation > 0 || p.itemTime > 0 || u.Item.stack < u.StackBefore || u.Item.IsAir;

    private static UseObservation Observe(Player p, Use u)
    {
        int idx = u.BuffType > 0 ? p.FindBuffIndex(u.BuffType) : -1;
        int stackAfter = u.Item.IsAir ? 0 : u.Item.stack; // the same Item object: vanilla decrements it (air at 0)
        return new UseObservation(u.Name, u.Consumable, u.StackBefore, stackAfter, u.BuffType, idx >= 0,
            idx >= 0 ? p.buffTime[idx] / 60 : 0, u.BuffType > 0 ? Lang.GetBuffName(u.BuffType) : null,
            u.LifeBefore, p.statLife, u.Started);
    }

    private static TilePoint InFront(Player p) =>
        new((int)(p.Center.X / 16f) + 2 * p.direction, (int)(p.Center.Y / 16f));

    private GameTaskStatus Done(GameCtx ctx)
    {
        Reason = _results.Count == 1 ? _results[0].Detail : UseOutcome.Summary(_results);
        Cleanup(ctx);
        return _results.All(r => r.Ok) ? GameTaskStatus.Succeeded : GameTaskStatus.Failed;
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
