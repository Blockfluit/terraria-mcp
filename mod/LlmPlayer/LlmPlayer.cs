using LlmPlayer.Control;
using Microsoft.Xna.Framework.Input;
using Terraria.GameInput;
using Terraria.ModLoader;

namespace LlmPlayer;

// Named LlmPlayerMod (not LlmPlayer) so the type doesn't shadow the LlmPlayer.* namespaces
// (e.g. LlmPlayer.Core.Bridge) inside this mod's code.
public sealed class LlmPlayerMod : Mod
{
    /// <summary>The only way to hand control to the AI and the only way to take it back. Default Insert.</summary>
    public static ModKeybind? ToggleAiControl { get; private set; }

    private static Action<string> _log = _ => { };

    public static void Log(string msg) => _log(msg);

    /// <summary>Display name of the first key bound to the toggle (as the user configured it), else "Insert".</summary>
    public static string ToggleKeyName() => ToggleKeys() is { Count: > 0 } keys ? keys[0] : "Insert";

    /// <summary>
    /// Keys bound to the toggle, or an empty list. Never throws: right after launch (the dev auto-load enters a world
    /// within a second) the keybind may not be in the input profile yet and GetAssignedKeys threw
    /// KeyNotFoundException from OnEnterWorld — a game crash ("Main engine crash", seen in game).
    /// </summary>
    public static List<string> ToggleKeys()
    {
        try
        {
            return ToggleAiControl?.GetAssignedKeys(InputMode.Keyboard) ?? new List<string>();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    public override void Load()
    {
        _log = msg => Logger.Info(msg);
        ToggleAiControl = KeybindLoader.RegisterKeybind(this, "ToggleAi", Keys.Insert);
    }

    public override void Unload()
    {
        ToggleAiControl = null;
        ControlState.Unload();
        _log = _ => { };
    }
}
