using LlmPlayer.Core.Control;
using MonoMod.Cil;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace LlmPlayer.Control;

/// <summary>
/// Keeps single player running while the AI drives and the window is not focused (the user is typing in a terminal).
///
/// Mechanism (tModLoader 1.4.4 Main.DoUpdate, decompiled): <c>hasFocus = IsActive; if (!hasFocus &amp;&amp; netMode == 0)
/// { delayUseItem = true; …; gamePaused = true; return; }</c> — the whole world update is skipped. An IL edit right
/// after that store sets <c>Main.hasFocus = true</c> again while <see cref="Override"/> holds, so the update runs
/// and AiModPlayer.SetControls applies the AI's controls as usual. When the user has control nothing changes:
/// vanilla pauses on focus loss.
///
/// What still sees the real focus: <c>Game.IsActive</c> is untouched, so PlayerInput keeps ignoring mouse buttons
/// of an unfocused window, and user-takeover detection (AiModPlayer.PostUpdate) only runs when IsActive.
/// </summary>
public sealed class UnfocusedPlay : ModSystem
{
    /// <summary>True when the AI drives a single-player world (the only case the override applies).</summary>
    public static bool Override => ControlState.Driver == Driver.Ai && Main.netMode == NetmodeID.SinglePlayer && !Main.gameMenu;

    public override void Load()
    {
        if (Main.dedServ) return;
        IL_Main.DoUpdate += PatchDoUpdate;
    }

    public override void Unload() => IL_Main.DoUpdate -= PatchDoUpdate;

    private void PatchDoUpdate(ILContext il)
    {
        var c = new ILCursor(il);
        if (!c.TryGotoNext(MoveType.After, i => i.MatchStsfld<Main>(nameof(Main.hasFocus))))
        {
            Mod.Logger.Warn("UnfocusedPlay: Main.hasFocus store not found in Main.DoUpdate; the game will pause when unfocused");
            return;
        }
        c.EmitDelegate(KeepFocus);
    }

    private static void KeepFocus()
    {
        if (!Main.hasFocus && Override) Main.hasFocus = true;
    }
}
