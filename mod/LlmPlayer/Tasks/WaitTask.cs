using LlmPlayer.Control;
using LlmPlayer.Core.Tasks;
using Terraria;

namespace LlmPlayer.Tasks;

/// <summary>
/// Stand still for N seconds. Reflexes treat it like idle (auto-defend and quick-heal keep running). The deadline is
/// in game ticks from the start, so time spent defending counts toward it.
/// </summary>
public sealed class WaitTask : IGameTask<GameCtx>
{
    public const float MaxSeconds = 60f;

    private readonly uint _end;

    public string Name => "wait";
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }

    public WaitTask(float seconds)
    {
        _end = Main.GameUpdateCount + (uint)Math.Ceiling(Math.Clamp(seconds, 0f, MaxSeconds) * 60f);
    }

    public GameTaskStatus Tick(GameCtx ctx)
    {
        ControlState.Pending = default;
        long left = (long)_end - Main.GameUpdateCount;
        if (left <= 0)
        {
            Progress = "done";
            return GameTaskStatus.Succeeded;
        }
        Progress = $"{left / 60.0:0.0} s left";
        return GameTaskStatus.Running;
    }

    public void OnCancel(GameCtx ctx) => ControlState.Pending = default;
}
