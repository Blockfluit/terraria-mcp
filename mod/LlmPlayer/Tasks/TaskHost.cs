using LlmPlayer.Bridge;
using LlmPlayer.Control;
using LlmPlayer.Core.Control;
using LlmPlayer.Core.Tasks;
using LlmPlayer.Observe;
using Terraria;
using Terraria.ModLoader;

namespace LlmPlayer.Tasks;

/// <summary>What a task sees each tick. Rebuilt every tick (the grid snapshots the screen rect for fair vision).</summary>
public sealed class GameCtx
{
    public required Player Player { get; init; }
    public required GameTileGrid Grid { get; init; }

    public static GameCtx ForLocalPlayer()
    {
        var p = Main.LocalPlayer;
        return new GameCtx { Player = p, Grid = new GameTileGrid(p) };
    }
}

/// <summary>
/// Runs at most one task (Core <see cref="TaskRunner{TCtx}"/>) on the main thread:
/// <list type="bullet">
/// <item>Ticks once per game update (BridgeSystem.Tick) and only while the AI drives.</item>
/// <item>Resets <see cref="ControlState.Pending"/> whenever no task runs, so a finished task never leaves keys held.</item>
/// <item>Cancels the task when the driver changes (reason = the change's reason, e.g. "user_input",
/// "player_died", "left_world") and on world unload.</item>
/// <item>Emits <c>task_finished</c> with the report; the indicator shows "&lt;name&gt; (&lt;progress&gt;)".</item>
/// </list>
/// Tasks write <see cref="ControlState.Pending"/> themselves; a task can run another as a sub-task by calling its
/// Tick/OnCancel directly (see <see cref="MoveToTask"/>).
/// </summary>
public sealed class TaskHost : ModSystem
{
    public static TaskRunner<GameCtx> Runner { get; private set; } = new();

    /// <summary>Start a task, cancelling the running one with reason "replaced". Main thread only. Any other task than
    /// open_chest closes an open chest first (vanilla would close it anyway once the player walks out of reach).</summary>
    public static void Start(IGameTask<GameCtx> task)
    {
        if (task is not OpenChestTask && Main.LocalPlayer is { } p) Interact.ChestAccess.Close(p);
        Runner.Start(task, GameCtx.ForLocalPlayer());
    }

    /// <summary>Cancel the running task (no-op when idle). Returns the cancelled task's name, or null.</summary>
    public static string? Cancel(string reason)
    {
        var name = Runner.Current?.Name;
        if (name != null) Runner.Cancel(reason, GameCtx.ForLocalPlayer());
        ControlState.Pending = default;
        return name;
    }

    public override void Load()
    {
        Runner = new TaskRunner<GameCtx>();
        Runner.Finished += OnFinished;
        ControlState.Changed += OnDriverChanged;
        ControlState.TaskLabel = Label;
        BridgeSystem.Tick += OnTick;
    }

    public override void Unload()
    {
        Runner = new TaskRunner<GameCtx>();
        PathOverlayState.Clear();
    }

    public override void OnWorldUnload()
    {
        if (!Runner.IsIdle && Main.LocalPlayer != null) Cancel("left_world");
        Reflexes.Reset(null);
        PathOverlayState.Clear();
    }

    /// <summary>
    /// One writer of <see cref="ControlState.Pending"/> per tick: <see cref="Reflexes"/> run first (quick-heal never
    /// touches controls); if auto-defend owns the controls the task is not ticked this tick (paused, not cancelled),
    /// otherwise the task ticks; with neither, keys are released.
    /// </summary>
    private static void OnTick()
    {
        if (Main.gameMenu || Main.LocalPlayer is not { active: true } p) return;
        if (ControlState.Driver != Driver.Ai) return;
        bool reflexOwnsControls = Reflexes.Tick(p, Runner.CurrentTask);
        if (!reflexOwnsControls && !Runner.IsIdle) Runner.Tick(GameCtx.ForLocalPlayer());
        if (Runner.IsIdle && !reflexOwnsControls)
        {
            ControlState.Pending = default;
            PathOverlayState.Clear();
        }
    }

    private static void OnDriverChanged(Driver d, string reason)
    {
        if (d != Driver.User) return;
        Cancel(reason);
        if (Main.LocalPlayer is { active: true } pl) Interact.ChestAccess.Close(pl); // hand back with no chest left open
        Reflexes.Reset(Main.LocalPlayer is { active: true } p ? p : null);
    }

    private static void OnFinished(TaskReport report) => BridgeSystem.Emit("task_finished", report);

    private static string? Label() =>
        Runner.Current is { } c ? (c.Progress is { } p ? $"{c.Name} ({p})" : c.Name) : null;
}

/// <summary>The path the overlay draws (main thread only; set by the movement task that owns the follower).</summary>
public static class PathOverlayState
{
    public static Core.Nav.PathFollower? Follower { get; private set; }

    public static void Set(Core.Nav.PathFollower? follower) => Follower = follower;

    public static void Clear() => Follower = null;
}
