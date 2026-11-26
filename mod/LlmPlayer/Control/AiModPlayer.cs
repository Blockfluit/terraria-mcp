using LlmPlayer.Core.Control;
using LlmPlayer.Core.Grid;
using LlmPlayer.Safety;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameInput;
using Terraria.ModLoader;

namespace LlmPlayer.Control;

/// <summary>
/// Applies the AI's controls to the local player while it drives. Only the toggle hotkey (or leaving the world)
/// hands control back (<see cref="HandoverPolicy"/>); the user's own input is ignored while the AI drives.
///
/// Order inside vanilla Player.Update (local player, window focused; verified by decompiling 1.4.4):
///   ResetControls → Triggers.CopyInto (user input) → <b>SetControls</b> → hotbar/scroll handling →
///   tileTargetX/Y = (Main.mouseX/Y + screenPosition) / 16 → SmartCursorLookup → … → <b>PreUpdateMovement</b>
///   → movement → ItemCheck (uses tileTargetX/Y and Main.MouseWorld) → <b>PostUpdate</b>.
/// When the window is unfocused in single player vanilla pauses (Main.DoUpdate returns early); while the AI drives
/// <see cref="UnfocusedPlay"/> keeps it running, so the AI's controls keep applying.
/// </summary>
public sealed class AiModPlayer : ModPlayer
{
    private bool IsLocal => Player.whoAmI == Main.myPlayer;

    public override void ProcessTriggers(TriggersSet triggersSet)
    {
        if (IsLocal) HandleToggle();
    }

    /// <summary>
    /// Called instead of Player.Update while single player is auto-paused (incl. <see cref="PauseWhenIdle"/>), when
    /// ProcessTriggers doesn't run. While the AI drives, keep the hotkey working here so the user can always take
    /// control back from a paused world (which also unpauses it).
    /// </summary>
    public override void UpdateAutopause()
    {
        if (!IsLocal || ControlState.Driver != Driver.Ai) return;
        HandleToggle();
    }

    private void HandleToggle()
    {
        if (LlmPlayerMod.ToggleAiControl is not { JustPressed: true }) return;

        if (ControlState.Driver == Driver.Ai)
        {
            ControlState.SetDriver(Driver.User, "hotkey");
            return;
        }

        if (Player.dead)
        {
            Main.NewText("LlmPlayer: can't hand control to the AI while dead.", 255, 200, 80);
            return;
        }
        // Safety: back the saves up before the AI ever acts this session. No backup → no handover.
        if (!SaveBackup.EnsureForSession(out var error))
        {
            Main.NewText($"LlmPlayer: save backup failed, AI control NOT enabled: {error}", 255, 80, 80);
            return;
        }
        ControlState.SetDriver(Driver.Ai, "hotkey");
    }

    /// <summary>
    /// While the AI drives, own every control: clear everything the user's triggers wrote this frame, then apply
    /// <see cref="ControlState.Pending"/>. Nothing user-derived leaks through (the user's keys and mouse do nothing while the AI drives).
    /// </summary>
    public override void SetControls()
    {
        if (!IsLocal || ControlState.Driver != Driver.Ai) return;

        var p = Player;
        p.controlLeft = p.controlRight = p.controlUp = p.controlDown = p.controlJump = false;
        p.controlUseItem = p.controlUseTile = p.controlThrow = p.controlInv = p.controlHook = false;
        p.controlTorch = p.controlMap = p.controlSmart = p.controlMount = false;
        p.controlQuickHeal = p.controlQuickMana = p.controlCreativeMenu = false;
        p.controlDownHold = false;
        p.mapFullScreen = false;

        var intent = ControlState.Pending;
        p.controlLeft = intent.Left;
        p.controlRight = intent.Right;
        p.controlJump = intent.Jump;
        p.controlDown = intent.Down;
        p.controlUp = intent.Up;
        p.controlUseItem = intent.UseItem;

        if (intent.UseItem)
        {
            // delayUseItem / mouseInterface are UI-click guards for the *real* mouse (e.g. it resting over the
            // hotbar or a just-refocused window). The AI's use isn't a click, so don't let them swallow it.
            p.delayUseItem = false;
            p.mouseInterface = false;
        }

        if (intent.CursorTile is { } tile) AimAt(tile);
    }

    /// <summary>
    /// Re-assert the aim right before movement/ItemCheck: SmartCursorLookup (runs after SetControls) or an
    /// inventory zoom reset may have moved tileTarget / Main.mouseX since SetControls.
    /// </summary>
    public override void PreUpdateMovement()
    {
        if (IsLocal && ControlState.Driver == Driver.Ai && ControlState.Pending.CursorTile is { } tile) AimAt(tile);
    }

    /// <summary>
    /// Cursor-aim mechanism: write the tile's centre into Main.mouseX/Y (screen coords relative to the *current*
    /// Main.screenPosition, matching vanilla's own tileTarget formula, incl. reversed gravity) and set
    /// Player.tileTargetX/Y directly. Vanilla's tileTarget recompute after SetControls then lands on the same tile,
    /// and Main.MouseWorld (item swing direction, projectile aim) points at it too. Main.mouseX is recomputed from
    /// the real device every frame (PlayerInput.SetZoom_MouseInWorld), so the override lasts only for this update.
    /// Reach, tool power and cooldowns are still enforced by vanilla ItemCheck.
    /// </summary>
    private void AimAt(TilePoint tile)
    {
        float wx = tile.X * 16f + 8f, wy = tile.Y * 16f + 8f;
        Main.mouseX = (int)(wx - Main.screenPosition.X);
        Main.mouseY = Player.gravDir == -1f
            ? (int)(Main.screenPosition.Y + Main.screenHeight - wy)
            : (int)(wy - Main.screenPosition.Y);
        Player.tileTargetX = tile.X;
        Player.tileTargetY = tile.Y;
    }

    public override void Kill(double damage, int hitDirection, bool pvp, PlayerDeathReason damageSource)
    {
        // The AI keeps control (only the hotkey takes it back); the current task can't continue though.
        if (IsLocal && ControlState.Driver == Driver.Ai) LlmPlayer.Tasks.TaskHost.Cancel("player_died");
    }

}
