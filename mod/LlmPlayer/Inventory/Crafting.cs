using LlmPlayer.Bridge;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Inventory;
using LlmPlayer.Core.Observe;
using LlmPlayer.Observe;
using Terraria;
using Terraria.Audio;
using Terraria.ID;
using Terraria.Localization;
using Terraria.Map;
using Terraria.ModLoader;

namespace LlmPlayer.Inventory;

public sealed record IngredientView(string Name, int Stack);

public sealed record RecipeView(string Result, int Stack, List<IngredientView> Ingredients, List<string> Stations);

public sealed record CraftResult(string Item, int Crafted, int Requested, string? Reason, int DroppedAtFeet);

/// <summary>
/// Reading and crafting recipes exactly as the crafting UI does. Main thread only.
/// <para>Availability is always the game's own: <see cref="Recipe.FindRecipes"/> fills <c>Main.availableRecipe</c>
/// from the player's items (inventory, void bag, open chest), <c>Player.adjTile</c> (stations in range, refreshed
/// by <see cref="Refresh"/> before every lookup), recipe conditions (water, biome, …) and
/// <c>RecipeLoader.RecipeAvailable</c>.
/// A craft is only attempted for a recipe in that list.</para>
/// <para>A craft mirrors <c>Main.CraftItem</c> (the method behind clicking a recipe), except the result goes into the
/// inventory instead of the mouse cursor: clone <c>createItem</c>, roll its prefix, <see cref="Recipe.Create"/>
/// (consumes ingredients through <c>RecipeLoader.ConsumeIngredient</c>, so alchemy-table savings and mod consumption
/// hooks apply, then re-runs FindRecipes), then <c>RecipeLoader.OnCraft</c>. The result is handed to
/// <see cref="Player.GetItem"/>; anything that doesn't fit is dropped at the player's feet and reported.</para>
/// <para>While a guide item sits in the Guide's slot, FindRecipes lists the guide's recipes instead of craftable
/// ones, so the guide slot is emptied for the duration of the call and restored afterwards (no frame passes).</para>
/// </summary>
public static class Crafting
{
    public const int MaxListed = 50;
    public const int MaxCraftCount = 999;

    // ---- listing -----------------------------------------------------------------------------------------------

    public static (List<RecipeView> Recipes, int Total) List(bool craftableOnly, string? name)
    {
        var views = new List<RecipeView>();
        int total = 0;
        WithoutGuideItem(() =>
        {
            Refresh(Main.LocalPlayer);
            foreach (var r in craftableOnly ? AvailableRecipes() : AllRecipes())
            {
                if (!RecipeMath.NameFilter(ResultName(r), name)) continue;
                total++;
                if (views.Count < MaxListed) views.Add(View(r));
            }
        });
        return (views, total);
    }

    public static RecipeView View(Recipe r) => new(
        r.createItem.Name,
        r.createItem.stack,
        r.requiredItem.Where(i => !i.IsAir).Select(i => new IngredientView(IngredientName(r, i), i.stack)).ToList(),
        r.requiredTile.Where(t => t >= 0).Select(StationName).Concat(r.Conditions.Select(c => c.Description.Value)).ToList());

    // ---- crafting ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Craft <paramref name="query"/> up to <paramref name="count"/> times. Throws missing_item (no recipe makes it,
    /// or ingredients short), missing_station (stations/conditions unmet) when not even one craft is possible.
    /// </summary>
    public static CraftResult Craft(Player p, string query, int count)
    {
        CraftResult? result = null;
        WithoutGuideItem(() => result = CraftInner(p, query, count));
        return result!;
    }

    private static CraftResult CraftInner(Player p, string query, int count)
    {
        Refresh(p);
        var candidates = AllRecipes().ToList();
        var matches = RecipeMath.MatchResults(candidates.Select(ResultName).ToList(), query);
        if (matches.Count == 0)
            throw new CommandError(ErrorCodes.MissingItem, $"no recipe makes '{query.Trim()}' (check the item name with list_recipes)");

        var available = new HashSet<int>(AvailableRecipes().Select(r => r.RecipeIndex));
        var recipes = matches.Select(i => candidates[i]).ToList();
        var picked = recipes[RecipeMath.PickRecipe(recipes.Select(r => new RecipeCandidate(
            available.Contains(r.RecipeIndex), MissingStations(p, r).Count == 0, RecipeMath.Missing(Ingredients(p, r)).Count)).ToList())];

        if (!available.Contains(picked.RecipeIndex)) throw Unavailable(p, picked);

        // Craft one at a time while the game still offers the recipe (alchemy savings can stretch ingredients past
        // a precomputed maximum, so the game's availability is the stop condition).
        int crafted = 0, dropped = 0;
        while (crafted < count && IsAvailable(picked))
        {
            dropped += CraftOnce(p, picked);
            crafted++;
        }

        var (_, reason) = RecipeMath.PlanCount(count, crafted);
        if (reason != null)
        {
            var missing = RecipeMath.Missing(Ingredients(p, picked));
            var stations = MissingStations(p, picked);
            reason += missing.Count > 0 ? $" (next one needs {RecipeMath.Describe(missing)})"
                : stations.Count > 0 ? $" (missing {string.Join(", ", stations)})"
                : " (recipe no longer available)";
        }
        if (dropped > 0)
            reason = (reason == null ? "" : reason + "; ") + $"inventory full: {dropped} dropped at the player's feet";
        return new CraftResult(picked.createItem.Name, crafted, count, reason, dropped);
    }

    /// <summary>One craft, as Main.CraftItem. Returns how many result items had to be dropped (inventory full).</summary>
    private static int CraftOnce(Player p, Recipe r)
    {
        var item = r.createItem.Clone();
        item.Prefix(-1);
        r.Create(); // consumes ingredients (RecipeLoader.ConsumeIngredient), notifies achievements, FindRecipes
        RecipeLoader.OnCraft(item, r, new Item()); // destination: an empty mouse cursor, like a normal craft
        PopupText.NewText(PopupTextContext.ItemCraft, item, item.stack);
        SoundEngine.PlaySound(SoundID.Grab);

        var left = p.GetItem(p.whoAmI, item, GetItemSettings.InventoryUIToInventorySettingsShowAsNew);
        if (left == null || left.IsAir) return 0;
        int n = left.stack;
        p.QuickSpawnItem(p.GetSource_Misc("LlmPlayer:craft"), left, n);
        return n;
    }

    private static CommandError Unavailable(Player p, Recipe r)
    {
        var stations = MissingStations(p, r);
        if (stations.Count > 0)
            return new CommandError(ErrorCodes.MissingStation,
                $"{r.createItem.Name} needs: {string.Join(", ", stations)} (stand near it; stations count within a few tiles)");
        var missing = RecipeMath.Missing(Ingredients(p, r));
        if (missing.Count > 0)
            return new CommandError(ErrorCodes.MissingItem, $"{r.createItem.Name} needs {RecipeMath.Describe(missing)}");
        return new CommandError(ErrorCodes.MissingItem, $"{r.createItem.Name} can't be crafted right now (a mod or hidden condition blocks it)");
    }

    /// <summary>
    /// Re-detect the stations in range, then the craftable recipes. Vanilla only calls <c>Player.AdjTiles</c> from
    /// <c>Player.Update</c> while the inventory is open, and the AI never opens it, so without this
    /// <c>Player.adjTile</c> stays stale and a Work Bench right next to the player doesn't count.
    /// </summary>
    private static void Refresh(Player p)
    {
        p.AdjTiles();
        Recipe.FindRecipes();
    }

    // ---- recipe facts ------------------------------------------------------------------------------------------

    private static IEnumerable<Recipe> AvailableRecipes()
    {
        for (int i = 0; i < Main.numAvailableRecipes; i++)
            yield return Main.recipe[Main.availableRecipe[i]];
    }

    private static IEnumerable<Recipe> AllRecipes()
    {
        for (int i = 0; i < Recipe.numRecipes; i++)
        {
            var r = Main.recipe[i];
            if (r != null && !r.Disabled && !r.createItem.IsAir) yield return r;
        }
    }

    private static bool IsAvailable(Recipe r)
    {
        for (int i = 0; i < Main.numAvailableRecipes; i++)
            if (Main.availableRecipe[i] == r.RecipeIndex) return true;
        return false;
    }

    private static ItemName ResultName(Recipe r) => new(r.createItem.Name, ItemLookup.InternalName(r.createItem.type));

    /// <summary>Unmet stations (by name) and unmet conditions (by description, e.g. "Near Water").</summary>
    public static List<string> MissingStations(Player p, Recipe r)
    {
        var list = new List<string>();
        foreach (int t in r.requiredTile)
            if (t >= 0 && !p.adjTile[t]) list.Add(StationName(t));
        foreach (var c in r.Conditions)
            if (!c.IsMet()) list.Add(c.Description.Value);
        return list;
    }

    /// <summary>Per ingredient: need for one craft, and how many the player has (recipe groups count every member).</summary>
    public static List<Ingredient> Ingredients(Player p, Recipe r)
    {
        var owned = OwnedItems(p);
        var list = new List<Ingredient>();
        foreach (var req in r.requiredItem)
        {
            if (req.IsAir) continue;
            int have = 0;
            foreach (var (type, stack) in owned)
                if (type == req.type || r.AcceptedByItemGroups(type, req.type)) have += stack;
            list.Add(new Ingredient(IngredientName(r, req), req.stack, have));
        }
        return list;
    }

    /// <summary>Items FindRecipes crafts from: inventory 0-57, the void bag if active, and the open chest/bank.</summary>
    private static Dictionary<int, int> OwnedItems(Player p)
    {
        var owned = new Dictionary<int, int>();
        void Add(Item[]? items, int n)
        {
            if (items == null) return;
            for (int i = 0; i < n && i < items.Length; i++)
                if (items[i] is { IsAir: false } it) owned[it.type] = owned.GetValueOrDefault(it.type) + it.stack;
        }
        Add(p.inventory, 58);
        if (p.useVoidBag()) Add(p.bank4.item, p.bank4.item.Length);
        var open = p.chest switch
        {
            >= 0 when p.chest < Main.chest.Length => Main.chest[p.chest]?.item,
            -2 => p.bank.item,
            -3 => p.bank2.item,
            -4 => p.bank3.item,
            -5 when !p.useVoidBag() => p.bank4.item,
            _ => null,
        };
        Add(open, open?.Length ?? 0);
        return owned;
    }

    private static string IngredientName(Recipe r, Item req)
    {
        foreach (int g in r.acceptedGroups)
            if (RecipeGroup.recipeGroups.TryGetValue(g, out var group) && group.ContainsItem(req.type)) return group.GetText();
        return req.Name;
    }

    /// <summary>The station's name as the crafting UI shows it ("Work Bench", "Iron Anvil").</summary>
    public static string StationName(int tile)
    {
        string name = Lang.GetMapObjectName(MapHelper.TileToLookup(tile, Recipe.GetRequiredTileStyle(tile)));
        if (!string.IsNullOrWhiteSpace(name)) return name;
        if (TileLoader.GetTile(tile) is { } mt) return mt.Name;
        return TileID.Search.TryGetName(tile, out var n) ? n : $"tile {tile}";
    }

    private static void WithoutGuideItem(Action action)
    {
        var guide = Main.guideItem;
        bool swap = guide != null && !guide.IsAir;
        if (swap) Main.guideItem = new Item();
        try
        {
            action();
        }
        finally
        {
            if (swap)
            {
                Main.guideItem = guide!;
                Recipe.FindRecipes();
            }
        }
    }
}
