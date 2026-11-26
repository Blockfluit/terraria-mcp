using LlmPlayer.Core.Observe;

namespace LlmPlayer.Core.Inventory;

/// <summary>One recipe ingredient: how many one craft needs and how many the player has (groups summed).</summary>
public readonly record struct Ingredient(string Name, int Need, int Have);

public sealed record MissingIngredient(string Name, int Need, int Have);

/// <summary>A recipe producing the requested item, as seen by the picker.</summary>
public readonly record struct RecipeCandidate(bool Available, bool StationsMet, int MissingKinds);

/// <summary>Pure recipe logic for craft / list_recipes. The game decides availability; this only explains it.</summary>
public static class RecipeMath
{
    /// <summary>Indices of results whose display name equals <paramref name="query"/> (case-insensitive); if none,
    /// those whose internal name does. Empty if nothing matches.</summary>
    public static List<int> MatchResults(IReadOnlyList<ItemName> results, string query)
    {
        var q = query.Trim();
        var found = new List<int>();
        if (q.Length == 0) return found;
        for (int i = 0; i < results.Count; i++)
            if (string.Equals(results[i].Display, q, StringComparison.OrdinalIgnoreCase)) found.Add(i);
        if (found.Count > 0) return found;
        for (int i = 0; i < results.Count; i++)
            if (string.Equals(results[i].Internal, q, StringComparison.OrdinalIgnoreCase)) found.Add(i);
        return found;
    }

    /// <summary>list_recipes name filter: display name contains it, or internal name equals it (case-insensitive).
    /// A null/blank filter matches everything.</summary>
    public static bool NameFilter(ItemName result, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var f = filter.Trim();
        return result.Display.Contains(f, StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.Internal, f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ingredients the player has too few of for <paramref name="times"/> crafts (Need scaled by times).</summary>
    public static List<MissingIngredient> Missing(IEnumerable<Ingredient> ingredients, int times = 1)
    {
        var list = new List<MissingIngredient>();
        foreach (var i in ingredients)
        {
            long need = (long)i.Need * Math.Max(1, times);
            if (i.Have < need) list.Add(new MissingIngredient(i.Name, (int)Math.Min(need, int.MaxValue), i.Have));
        }
        return list;
    }

    /// <summary>"Wood x10 (have 3), Iron Bar x2 (have 0)".</summary>
    public static string Describe(IEnumerable<MissingIngredient> missing) =>
        string.Join(", ", missing.Select(m => $"{m.Name} x{m.Need} (have {m.Have})"));

    /// <summary>How many times the recipe can be crafted from what the player has (ignores alchemy savings).</summary>
    public static int MaxCrafts(IEnumerable<Ingredient> ingredients)
    {
        int max = int.MaxValue;
        foreach (var i in ingredients)
            if (i.Need > 0) max = Math.Min(max, Math.Max(0, i.Have) / i.Need);
        return max;
    }

    /// <summary>Best recipe to craft or explain: available first, then one with every ingredient at hand (only a
    /// station is missing: "stand near a Furnace" beats "you need Iron Fence" for Iron Bar, seen in game), then one
    /// whose stations are met, then the fewest missing ingredient kinds; ties keep the earliest. -1 for an empty
    /// list.</summary>
    public static int PickRecipe(IReadOnlyList<RecipeCandidate> candidates)
    {
        int best = -1;
        for (int i = 0; i < candidates.Count; i++)
            if (best < 0 || Better(candidates[i], candidates[best])) best = i;
        return best;
    }

    private static bool Better(RecipeCandidate a, RecipeCandidate b)
    {
        if (a.Available != b.Available) return a.Available;
        bool aHas = a.MissingKinds == 0, bHas = b.MissingKinds == 0;
        if (aHas != bHas) return aHas;
        if (a.StationsMet != b.StationsMet) return a.StationsMet;
        return a.MissingKinds < b.MissingKinds;
    }

    /// <summary>Crafts to attempt for a request: all of them if possible, else as many as possible plus a reason.</summary>
    public static (int Count, string? Reason) PlanCount(int requested, int maxCraftable) =>
        requested <= maxCraftable ? (requested, null) : (Math.Max(0, maxCraftable), $"only enough ingredients for {Math.Max(0, maxCraftable)}");
}
