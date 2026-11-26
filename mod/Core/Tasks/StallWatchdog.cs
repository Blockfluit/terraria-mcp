namespace LlmPlayer.Core.Tasks;

/// <summary>
/// Detects a task that keeps running without getting anywhere (seen in game: mine_vein swimming in circles in a pool
/// for minutes at "walking: 18 tiles remaining"). Fed once per tick while a task runs. Progress is any of:
/// <list type="bullet">
/// <item>a progress string this task hasn't reported before (attack's target life, "mined 3/9", …),</item>
/// <item>standing on a tile this task hasn't visited before (a walk, an exploration),</item>
/// <item>an inventory change (mining, chopping, placing, crafting, picking up).</item>
/// </list>
/// Moving back and forth over the same tiles with the same messages and no item changes is not progress. Ticks while
/// the task is paused (auto-defend) don't count. <see cref="Tick"/> returns true once
/// <see cref="LimitTicks"/> ticks pass without progress; the host then stops the task.
/// </summary>
public sealed class StallWatchdog
{
    public const int DefaultLimitTicks = 20 * 60;

    private readonly HashSet<string> _seenProgress = new(StringComparer.Ordinal);
    private readonly HashSet<(int, int)> _visited = new();
    private object? _task;
    private long _inventory;
    private int _idleTicks;

    public StallWatchdog(int limitTicks = DefaultLimitTicks) => LimitTicks = limitTicks;

    public int LimitTicks { get; }

    /// <summary>Seconds without progress so far (for messages).</summary>
    public int IdleSeconds => _idleTicks / 60;

    /// <param name="task">The running task instance (a new instance resets the watchdog).</param>
    /// <param name="inventorySignature">Any value that changes when the inventory does.</param>
    /// <param name="paused">The task wasn't ticked this tick (reflexes own the controls).</param>
    public bool Tick(object task, string? progress, int tileX, int tileY, long inventorySignature, bool paused)
    {
        if (!ReferenceEquals(task, _task))
        {
            Reset();
            _task = task;
            _inventory = inventorySignature;
        }
        bool progressed = false;
        if (progress != null && _seenProgress.Add(progress)) progressed = true;
        if (_visited.Add((tileX, tileY))) progressed = true;
        if (inventorySignature != _inventory)
        {
            _inventory = inventorySignature;
            progressed = true;
        }
        if (progressed) _idleTicks = 0;
        else if (!paused) _idleTicks++;
        return _idleTicks >= LimitTicks;
    }

    public void Reset()
    {
        _task = null;
        _seenProgress.Clear();
        _visited.Clear();
        _idleTicks = 0;
    }
}
