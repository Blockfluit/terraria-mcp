namespace LlmPlayer.Core.Inventory;

/// <summary>One ingredient of a <see cref="ChainRecipe"/>: <paramref name="Count"/> of any item type in
/// <paramref name="Accepted"/> (a recipe group accepts several); <paramref name="Primary"/> is the type to craft when
/// short.</summary>
public sealed record ChainIngredient(string Name, int Primary, IReadOnlySet<int> Accepted, int Count);

/// <summary>A recipe as the chain planner sees it. <paramref name="StationsMet"/>: every station and condition is
/// satisfied right now (the planner never plans a craft that isn't); <paramref name="MissingStations"/> names the
/// unmet ones for explanations.</summary>
public sealed record ChainRecipe(int Id, string Name, int Result, int ResultStack, IReadOnlyList<ChainIngredient> Ingredients,
    bool StationsMet, IReadOnlyList<string> MissingStations);

/// <summary>Craft recipe <paramref name="RecipeId"/> (result <paramref name="Name"/>) <paramref name="Times"/> times.</summary>
public readonly record struct CraftStep(int RecipeId, string Name, int Times);

/// <summary>Ok: the steps in order (intermediates first, the requested recipe last). Not ok: why, e.g.
/// "Bottle x1 (have 0): Glass x1 (have 0): Glass needs Furnace".</summary>
public sealed record ChainPlan(bool Ok, IReadOnlyList<CraftStep> Steps, string? Problem);

/// <summary>
/// Plans <c>craft with_intermediates</c>: to craft a recipe N times, first craft the ingredients the player is short
/// of from their own recipes, recursively (Lesser Healing Potion ← Bottle ← Glass ← Sand), using only items the player
/// owns and stations that are in range now. Pure simulation over item counts; the game still checks every real craft.
/// <list type="bullet">
/// <item>For a short ingredient, recipes making its primary type are tried in the given order; the first that works
/// (with its own sub-plan) wins. Each attempt runs on a copy of the simulated inventory, so a failed attempt leaves
/// nothing behind.</item>
/// <item>Depth is limited (<see cref="MaxDepth"/>) and an item already being planned up the chain is never crafted
/// again (no Wood → Wood Platform → Wood loops).</item>
/// <item>Ingredient consumption takes the primary type first, then other accepted types (recipe groups).</item>
/// <item>Alchemy-table savings are ignored (planning may over-craft slightly, never under-supply).</item>
/// </list>
/// </summary>
public static class CraftChain
{
    public const int MaxDepth = 4;

    public static ChainPlan Plan(ChainRecipe top, int times, IReadOnlyDictionary<int, int> owned,
        Func<int, IReadOnlyList<ChainRecipe>> recipesFor, int maxDepth = MaxDepth)
    {
        var inv = new Dictionary<int, int>(owned);
        var steps = new List<CraftStep>();
        var visiting = new HashSet<int> { top.Result };
        var problem = TryRecipe(top, Math.Max(1, times), inv, steps, recipesFor, visiting, 0, maxDepth);
        return problem == null ? new ChainPlan(true, Merge(steps), null) : new ChainPlan(false, Array.Empty<CraftStep>(), problem);
    }

    /// <summary>Plan crafting <paramref name="r"/> <paramref name="times"/> times into <paramref name="steps"/>,
    /// updating <paramref name="inv"/>. Returns null on success, else the problem (inv/steps then undefined).</summary>
    private static string? TryRecipe(ChainRecipe r, int times, Dictionary<int, int> inv, List<CraftStep> steps,
        Func<int, IReadOnlyList<ChainRecipe>> recipesFor, HashSet<int> visiting, int depth, int maxDepth)
    {
        if (!r.StationsMet)
            return $"{r.Name} needs {(r.MissingStations.Count > 0 ? string.Join(", ", r.MissingStations) : "a station or condition that isn't met")}";
        // Crafting one intermediate can eat into another ingredient (Wood for platforms when the recipe also needs Wood),
        // so re-check until every ingredient is covered at once.
        for (int pass = 0; ; pass++)
        {
            bool allCovered = true;
            foreach (var ing in r.Ingredients)
            {
                long need = (long)ing.Count * times;
                int have = Have(inv, ing);
                if (have >= need) continue;
                allCovered = false;
                if (pass >= 2) return $"{ing.Name} x{need} (have {have}): not enough left after crafting the other ingredients";
                int shortBy = (int)Math.Min(int.MaxValue, need - have);
                var why = TryMake(ing, shortBy, inv, steps, recipesFor, visiting, depth + 1, maxDepth);
                if (why != null) return $"{ing.Name} x{need} (have {have}): {why}";
            }
            if (allCovered) break;
        }
        foreach (var ing in r.Ingredients) Take(inv, ing, (long)ing.Count * times);
        inv[r.Result] = inv.GetValueOrDefault(r.Result) + r.ResultStack * times;
        steps.Add(new CraftStep(r.Id, r.Name, times));
        return null;
    }

    /// <summary>Plan making at least <paramref name="amount"/> more of <paramref name="ing"/>'s primary type.</summary>
    private static string? TryMake(ChainIngredient ing, int amount, Dictionary<int, int> inv, List<CraftStep> steps,
        Func<int, IReadOnlyList<ChainRecipe>> recipesFor, HashSet<int> visiting, int depth, int maxDepth)
    {
        if (depth > maxDepth) return "too many crafting steps deep";
        if (visiting.Contains(ing.Primary)) return "no recipe without a loop";
        var recipes = recipesFor(ing.Primary).Where(r => r.Result == ing.Primary && r.ResultStack > 0).ToList();
        if (recipes.Count == 0) return "not craftable (gather or buy it)";

        string? first = null;
        visiting.Add(ing.Primary);
        try
        {
            foreach (var r in recipes)
            {
                int times = (amount + r.ResultStack - 1) / r.ResultStack;
                var invCopy = new Dictionary<int, int>(inv);
                var stepsCopy = new List<CraftStep>();
                var why = TryRecipe(r, times, invCopy, stepsCopy, recipesFor, visiting, depth, maxDepth);
                if (why == null)
                {
                    inv.Clear();
                    foreach (var kv in invCopy) inv[kv.Key] = kv.Value;
                    steps.AddRange(stepsCopy);
                    return null;
                }
                first ??= why;
            }
        }
        finally
        {
            visiting.Remove(ing.Primary);
        }
        return first;
    }

    private static int Have(Dictionary<int, int> inv, ChainIngredient ing)
    {
        long n = 0;
        foreach (int t in ing.Accepted.Append(ing.Primary).Distinct()) n += inv.GetValueOrDefault(t);
        return (int)Math.Min(int.MaxValue, n);
    }

    private static void Take(Dictionary<int, int> inv, ChainIngredient ing, long amount)
    {
        foreach (int t in new[] { ing.Primary }.Concat(ing.Accepted.Where(a => a != ing.Primary).OrderBy(a => a)))
        {
            if (amount <= 0) return;
            int have = inv.GetValueOrDefault(t);
            int used = (int)Math.Min(have, amount);
            inv[t] = have - used;
            amount -= used;
        }
    }

    /// <summary>Consecutive steps of the same recipe become one.</summary>
    private static List<CraftStep> Merge(List<CraftStep> steps)
    {
        var merged = new List<CraftStep>();
        foreach (var s in steps)
        {
            if (merged.Count > 0 && merged[^1].RecipeId == s.RecipeId)
                merged[^1] = merged[^1] with { Times = merged[^1].Times + s.Times };
            else
                merged.Add(s);
        }
        return merged;
    }
}
