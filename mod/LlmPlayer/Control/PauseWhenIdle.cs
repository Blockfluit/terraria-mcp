using LlmPlayer.Config;
using LlmPlayer.Core.Control;
using LlmPlayer.Tasks;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace LlmPlayer.Control;

/// <summary>
/// <c>pause_when_idle</c>: freeze a single-player world while the AI drives and no task runs, so the LLM can think.
///
/// Mechanism (tModLoader 1.4.4 Main.DoUpdate, read from IL): after input handling (PlayerInput.UpdateInput →
/// SystemLoader.PostUpdateInput, where BridgeSystem drains requests) it does
/// <c>if (CanPauseGame()) { DoUpdate_WhilePaused(); PlayerLoader.UpdateAutopause(localPlayer); gamePaused = true; return; }</c>
/// — vanilla's auto-pause (inventory open with autoPause, settings menu). A detour on the private
/// <c>Main.CanPauseGame</c> ORs in <see cref="ShouldPause"/>, so we pause through exactly that path:
/// <list type="bullet">
/// <item>Requests are still processed while paused (PostUpdateInput runs first), so a command that starts a task
/// unpauses the world in the same update: CanPauseGame is evaluated after the drain and sees a running task.</item>
/// <item>Driver → User unpauses immediately (ShouldPause requires Ai). Player.Update doesn't run while paused, so
/// the hotkey and user-input takeover are checked from <see cref="AiModPlayer.UpdateAutopause"/>.</item>
/// <item>Coexists with <see cref="UnfocusedPlay"/>: that edit runs earlier in DoUpdate (it only keeps the update
/// from taking the unfocused-window early return); this one decides the later auto-pause branch.</item>
/// </list>
/// </summary>
public sealed class PauseWhenIdle : ModSystem
{
    /// <summary>Pause now? Config on, single player, in a world, AI driving, alive, no task, not defending.</summary>
    public static bool ShouldPause =>
        LlmConfig.Current.PauseWhenIdle
        && Main.netMode == NetmodeID.SinglePlayer
        && !Main.gameMenu
        && ControlState.Driver == Driver.Ai
        && Main.LocalPlayer is { active: true, dead: false }
        && TaskHost.Runner.IsIdle
        && !Reflexes.Defending;

    public override void Load()
    {
        if (Main.dedServ) return;
        On_Main.CanPauseGame += CanPauseGame;
    }

    public override void Unload() => On_Main.CanPauseGame -= CanPauseGame;

    private static bool CanPauseGame(On_Main.orig_CanPauseGame orig) => orig() || ShouldPause;
}
