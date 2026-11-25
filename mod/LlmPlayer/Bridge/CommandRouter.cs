using System.Reflection;
using System.Text.Json;
using LlmPlayer.Control;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Control;
using Terraria;

namespace LlmPlayer.Bridge;

/// <summary>
/// Marks a static class whose <c>public static void Register()</c> registers commands with
/// <see cref="CommandRouter"/>. Picked up automatically at mod load — no central list to edit.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class CommandGroupAttribute : Attribute
{
}

/// <summary>Main-thread command table. Wraps Core's <see cref="CommandRegistry"/> with game checks.</summary>
public static class CommandRouter
{
    private static readonly CommandRegistry Registry = new() { Guard = Guard };

    /// <summary>
    /// Register a command. <paramref name="isAction"/>: starts/modifies tasks or changes the player
    /// (gated on control). <paramref name="requiresWorld"/>: false only for commands that work from the main menu.
    /// Handlers run on the main thread; throw <see cref="CommandError"/> for expected failures and use
    /// <see cref="Params"/> to read params. The return value is serialized camelCase as "result".
    /// </summary>
    public static void Register(string method, Func<JsonElement, object?> handler, bool isAction, bool requiresWorld = true) =>
        Registry.Register(method, handler, isAction, requiresWorld);

    /// <summary>Throws CommandError (unknown_method, not_in_world, …).</summary>
    public static object? Dispatch(string method, JsonElement @params) => Registry.Dispatch(method, @params);

    /// <summary>Dispatch and render the full response line. Never throws.</summary>
    public static string Handle(int id, string method, JsonElement @params) => Registry.Handle(id, method, @params);

    private static void Guard(CommandSpec spec)
    {
        if (spec.RequiresWorld && (Main.gameMenu || Main.LocalPlayer is not { active: true }))
            throw new CommandError(ErrorCodes.NotInWorld, "not in a world (main menu or loading)");

        // Action gate: the AI only acts after the user handed control over with the hotkey.
        if (spec.IsAction && ControlState.Driver == Driver.User)
            throw new CommandError(ErrorCodes.UserHasControl,
                $"the user has control; ask them to press {LlmPlayerMod.ToggleKeyName()} in game to hand it to the AI");
    }

    internal static void RegisterAll(Action<string> log)
    {
        Registry.Clear();
        foreach (var type in typeof(CommandRouter).Assembly.GetTypes())
        {
            if (type.GetCustomAttribute<CommandGroupAttribute>() == null) continue;
            var register = type.GetMethod("Register", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
            if (register == null)
            {
                log($"[CommandGroup] {type.FullName} has no public static Register()");
                continue;
            }
            register.Invoke(null, null);
        }
        log($"registered {Registry.Methods.Count} commands: {string.Join(", ", Registry.Methods.OrderBy(m => m))}");
    }

    internal static void Clear() => Registry.Clear();
}
