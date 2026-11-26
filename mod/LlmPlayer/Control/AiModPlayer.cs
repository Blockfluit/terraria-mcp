using LlmPlayer.Core.Control;
using LlmPlayer.Core.Grid;
using LlmPlayer.Safety;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameInput;
using Terraria.ModLoader;

namespace LlmPlayer.Control;

/// <summary>
/// Applies the AI's controls to the local player while it drives, and hands control back on real user input,
/// death, or the toggle hotkey.
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
    private RawInputSnapshot _prevRaw = Empty;

    private static readonly RawInputSnapshot Empty = new(new HashSet<int>(), false, false, 0, 0, 0);

    private bool IsLocal => Player.whoAmI == Main.myPlayer;

    public override void OnEnterWorld() => _prevRaw = ReadRawInput(out _);

    public override void ProcessTriggers(TriggersSet triggersSet)
    {
        if (!IsLocal || LlmPlayerMod.ToggleAiControl is not { JustPressed: true }) return;

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
    /// <see cref="ControlState.Pending"/>. Nothing user-derived leaks through (e.g. a key held while pressing F8).
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
    /// It never feeds user-input detection: that reads raw XNA Keyboard/Mouse state only (<see cref="ReadRawInput"/>).
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

    public override void PostUpdate()
    {
        if (!IsLocal) return;
        var cur = ReadRawInput(out var toggleKey);
        var prev = _prevRaw;
        _prevRaw = cur;

        // Unfocused: nothing counts as user input (the user may be typing in a terminal while the AI plays).
        if (ControlState.Driver != Driver.Ai || !Main.instance.IsActive) return;
        if (InputWatcher.IsUserInput(prev, cur, toggleKey))
            ControlState.SetDriver(Driver.User, "user_input");
    }

    public override void Kill(double damage, int hitDirection, bool pvp, PlayerDeathReason damageSource)
    {
        if (IsLocal) ControlState.SetDriver(Driver.User, "player_died");
    }

    /// <summary>
    /// Raw device state straight from XNA/FNA — never Main.mouseX/PlayerInput, which the AI's aim overrides.
    /// Keys bound to the toggle keybind are removed so pressing it isn't "user input"; <paramref name="toggleKey"/>
    /// is the first of them (or -1).
    /// </summary>
    private static RawInputSnapshot ReadRawInput(out int toggleKey)
    {
        var toggle = ToggleKeys();
        toggleKey = toggle.Count > 0 ? toggle[0] : -1;

        var keys = new HashSet<int>();
        foreach (var k in Keyboard.GetState().GetPressedKeys())
            if (!toggle.Contains((int)k)) keys.Add((int)k);

        var m = Mouse.GetState();
        return new RawInputSnapshot(keys, m.LeftButton == ButtonState.Pressed, m.RightButton == ButtonState.Pressed,
            m.X, m.Y, m.ScrollWheelValue);
    }

    private static List<int> ToggleKeys()
    {
        var result = new List<int>();
        var bound = LlmPlayerMod.ToggleAiControl?.GetAssignedKeys(InputMode.Keyboard);
        if (bound == null) return result;
        foreach (var name in bound)
            if (Enum.TryParse<Keys>(name, out var k)) result.Add((int)k);
        return result;
    }
}
