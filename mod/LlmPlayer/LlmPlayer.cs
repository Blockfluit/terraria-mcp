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
    public static string ToggleKeyName() =>
        ToggleAiControl?.GetAssignedKeys(InputMode.Keyboard) is { Count: > 0 } keys ? keys[0] : "Insert";

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
