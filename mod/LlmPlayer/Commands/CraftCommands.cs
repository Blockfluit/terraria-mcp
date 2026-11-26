using LlmPlayer.Bridge;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Inventory;
using Terraria;

namespace LlmPlayer.Commands;

/// <summary>craft (instant action) and list_recipes (observation). See <see cref="Crafting"/> for how faithful
/// crafting is guaranteed.</summary>
[CommandGroup]
public static class CraftCommands
{
    public static void Register()
    {
        CommandRouter.Register("list_recipes", p =>
        {
            bool craftableOnly = Params.Bool(p, "craftable_only", true);
            var (recipes, total) = Crafting.List(craftableOnly, Params.StringOrNull(p, "name"));
            return new { Recipes = recipes, Total = total, Truncated = total > recipes.Count };
        }, isAction: false);

        CommandRouter.Register("craft", p =>
        {
            int count = Params.Int(p, "count", 1);
            if (count < 1 || count > Crafting.MaxCraftCount)
                throw new CommandError(ErrorCodes.BadParams, $"count must be 1..{Crafting.MaxCraftCount}");
            return Crafting.Craft(Main.LocalPlayer, Params.String(p, "item"), count);
        }, isAction: true);
    }
}
