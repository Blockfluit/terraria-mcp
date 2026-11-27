namespace LlmPlayer.Core.Npc;

/// <summary>A visible town NPC as talk_to_npc / buy can pick it by name: personal name ("Demetrius") and profession
/// ("Arms Dealer", vanilla's type name; equal to the name for NPCs without a personal name).</summary>
public readonly record struct NpcCandidate(int Id, string Name, string Profession, double Distance);

public static class NpcNames
{
    /// <summary>The NPC whose personal name or profession equals <paramref name="query"/> (trimmed, case-insensitive),
    /// the nearest one when several do (e.g. two Guides). -1 if none.</summary>
    public static int Match(IEnumerable<NpcCandidate> candidates, string query)
    {
        string q = Normalize(query);
        if (q.Length == 0) return -1;
        int best = -1;
        double bestDist = double.MaxValue;
        foreach (var c in candidates)
        {
            if (Normalize(c.Name) != q && Normalize(c.Profession) != q) continue;
            if (c.Distance < bestDist)
            {
                best = c.Id;
                bestDist = c.Distance;
            }
        }
        return best;
    }

    /// <summary>"Demetrius (Arms Dealer), Andrew (Guide)" for an error message; "none" when empty.</summary>
    public static string List(IEnumerable<NpcCandidate> candidates)
    {
        var parts = candidates.OrderBy(c => c.Distance)
            .Select(c => string.Equals(c.Name, c.Profession, StringComparison.OrdinalIgnoreCase) ? c.Name : $"{c.Name} ({c.Profession})")
            .ToList();
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    private static string Normalize(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}
