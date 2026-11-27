namespace LlmPlayer.Core.Inventory;

/// <summary>What use_item saw before and after one use (taken by the mod from the real player).</summary>
/// <param name="Consumable">The item is used up on use (potions, Life Crystals, summon items…).</param>
/// <param name="BuffType">The buff the item gives (vanilla <c>Item.buffType</c>), 0 = none.</param>
/// <param name="Started">The game started the use animation (itemAnimation/itemTime went up from 0 after the press).</param>
public readonly record struct UseObservation(
    string Item,
    bool Consumable,
    int StackBefore,
    int StackAfter,
    int BuffType,
    bool BuffAfter,
    int BuffSecondsLeft,
    string? BuffName,
    int LifeBefore,
    int LifeAfter,
    bool Started);

/// <summary>One use_item outcome: did the use really happen, and what it did.</summary>
public sealed record UseResult(string Item, bool Ok, string Detail);

/// <summary>
/// Decides whether a use really happened. Seen in game: three potions used in a row before a boss, one buff never
/// appeared and that call never reported done. A started animation alone isn't proof (an earlier swing can still be
/// running), so: a consumable must have lost one from its stack; a non-consumable buff item must have its buff
/// active; anything else must have started its animation. Pure.
/// </summary>
public static class UseOutcome
{
    /// <summary>Ticks to keep checking after the animation ended before calling a use failed (consumption and buffs
    /// land within the use, this only covers a late frame).</summary>
    public const int GraceTicks = 10;

    public static bool Happened(in UseObservation o) =>
        o.Consumable ? o.StackAfter < o.StackBefore
        : o.BuffType > 0 ? o.BuffAfter
        : o.Started;

    public static UseResult Judge(in UseObservation o)
    {
        if (!Happened(o))
        {
            string why = o.Consumable ? $"the game didn't use {o.Item} (still {o.StackAfter} in the stack)"
                : o.BuffType > 0 ? $"the game didn't use {o.Item} (no {o.BuffName ?? "buff"} buff)"
                : $"the game didn't use {o.Item}";
            return new UseResult(o.Item, false, why);
        }
        var parts = new List<string>();
        if (o.BuffType > 0)
            parts.Add(o.BuffAfter
                ? $"buff {o.BuffName ?? "?"} {o.BuffSecondsLeft / 60}:{o.BuffSecondsLeft % 60:00}"
                : $"no {o.BuffName ?? "buff"} buff (immune or replaced?)");
        if (o.LifeAfter != o.LifeBefore) parts.Add($"life {o.LifeBefore} -> {o.LifeAfter}");
        if (o.Consumable) parts.Add($"{o.StackAfter} left");
        return new UseResult(o.Item, true, $"used {o.Item}" + (parts.Count > 0 ? $" ({string.Join(", ", parts)})" : ""));
    }

    /// <summary>The task's reason for a batch of queued uses: every use's detail, in order.</summary>
    public static string Summary(IReadOnlyList<UseResult> results) =>
        results.Count == 0 ? "nothing used" : string.Join("; ", results.Select(r => r.Ok ? r.Detail : $"FAILED {r.Detail}"));
}
