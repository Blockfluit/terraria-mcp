using LlmPlayer.Core.Tasks;

namespace LlmPlayer.Core.Combat;

/// <summary>
/// A task that is sometimes in the middle of something auto-defend must not interrupt, e.g. a one-shot item use that
/// has already pressed "use" (pausing it could repeat the use or move its aim mid-animation).
/// </summary>
public interface IUninterruptibleTask
{
    /// <summary>True while the task must keep the controls (it then counts as <see cref="TaskPhase.Busy"/>).</summary>
    bool Uninterruptible { get; }
}

/// <summary>
/// Which running task auto-defend may pause (<see cref="ReflexArbiter"/>). Keyed by the task's name, which is the
/// MCP command that started it.
/// <list type="bullet">
/// <item>Idle: no task, <c>wait</c>.</item>
/// <item>Defendable: <c>move_to</c>, <c>explore</c>, <c>talk_to_npc</c> (walking to the NPC; opening the chat is
/// instant), <c>chop_tree</c>, <c>mine</c>, <c>mine_area</c>, <c>pick_up_items</c>, <c>place</c>,
/// <c>use_item</c>. A paused task is simply not ticked; all their timers count their own ticks, so a defence doesn't
/// use up a swing timeout, pickup deadline or stuck counter. A swing in progress finishes before the defender
/// switches to its weapon (HeldItemLease waits for the use to end).</item>
/// <item>Busy: <c>attack</c> (it fights already), any task reporting <see cref="IUninterruptibleTask.Uninterruptible"/>
/// (use_item once it pressed "use"), and any unknown task (safe default).</item>
/// </list>
/// </summary>
public static class TaskPhases
{
    private static readonly HashSet<string> Defendable = new(StringComparer.Ordinal)
    {
        "move_to", "explore", "talk_to_npc", "chop_tree", "mine", "mine_area", "pick_up_items", "place", "use_item",
    };

    public static TaskPhase Of(string? taskName) => taskName switch
    {
        null or "wait" => TaskPhase.Idle,
        _ when Defendable.Contains(taskName) => TaskPhase.Defendable,
        _ => TaskPhase.Busy,
    };

    public static TaskPhase Of<TCtx>(IGameTask<TCtx>? task) =>
        task is IUninterruptibleTask { Uninterruptible: true } ? TaskPhase.Busy : Of(task?.Name);
}
