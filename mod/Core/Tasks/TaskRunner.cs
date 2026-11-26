using System.Text.Json.Serialization;

namespace LlmPlayer.Core.Tasks;

public enum GameTaskStatus { Running, Succeeded, Failed, Cancelled }

/// <param name="Result">Optional structured outcome of the task (e.g. talk_to_npc's chat and shop), serialized as
/// <c>result</c> only when set. Reaches the LLM via get_task / wait_for_task and the <c>task_finished</c> event.</param>
public sealed record TaskReport(string Name, GameTaskStatus Status, string? Reason, string? Progress,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Result = null);

public interface IGameTask<TCtx>
{
    string Name { get; }
    GameTaskStatus Tick(TCtx ctx);
    string? Reason { get; }
    string? Progress { get; }
    void OnCancel(TCtx ctx);

    /// <summary>Structured outcome for the report (null for most tasks). Read after every tick and on finish.</summary>
    object? Result => null;
}

/// <summary>Runs at most one task. <c>Finished</c> fires exactly once per started task.</summary>
public sealed class TaskRunner<TCtx>
{
    private IGameTask<TCtx>? _task;

    public TaskReport? LastFinished { get; private set; }
    public bool IsIdle => _task == null;
    public event Action<TaskReport>? Finished;

    public TaskReport? Current => _task == null
        ? null
        : new TaskReport(_task.Name, GameTaskStatus.Running, _task.Reason, _task.Progress, _task.Result);

    public void Start(IGameTask<TCtx> task, TCtx ctx)
    {
        Cancel("replaced", ctx);
        _task = task;
    }

    public void Tick(TCtx ctx)
    {
        var task = _task;
        if (task == null) return;

        GameTaskStatus status;
        try
        {
            status = task.Tick(ctx);
        }
        catch (Exception ex)
        {
            Finish(task, GameTaskStatus.Failed, "internal: " + ex.Message);
            return;
        }

        if (status != GameTaskStatus.Running)
            Finish(task, status, task.Reason);
    }

    /// <summary>Cancels the running task; no-op when idle.</summary>
    public void Cancel(string reason, TCtx ctx)
    {
        var task = _task;
        if (task == null) return;
        try { task.OnCancel(ctx); }
        catch { /* a failing cleanup must not block cancellation */ }
        Finish(task, GameTaskStatus.Cancelled, reason);
    }

    private void Finish(IGameTask<TCtx> task, GameTaskStatus status, string? reason)
    {
        _task = null;
        var report = new TaskReport(task.Name, status, reason, task.Progress, task.Result);
        LastFinished = report;
        Finished?.Invoke(report);
    }
}
