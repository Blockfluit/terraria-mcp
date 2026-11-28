namespace LlmPlayer.Core.Combat;

/// <summary>Static strategy facts about one boss (Classic/normal mode, Terraria 1.4.4), for get_boss_info.</summary>
public sealed record BossFact(
    string Key, string Name, string Summon, string Recipe, string SummonWhen, string NaturalSpawn, string Leaves,
    string Stats, string[] Tips);

/// <summary>
/// The bosses get_boss_info knows about: Eye of Cthulhu and King Slime only. Short, factual, normal mode; the
/// world's defeated flags are added by the game side. Keep entries to what the vanilla game does, not guesses.
/// </summary>
public static class BossFacts
{
    public static readonly BossFact EyeOfCthulhu = new(
        Key: "eye_of_cthulhu",
        Name: "Eye of Cthulhu",
        Summon: "Suspicious Looking Eye (consumed on use)",
        Recipe: "6 Lens at a Demon Altar or Crimson Altar (Lens drop from Demon Eyes at night)",
        SummonWhen: "night only; nothing happens if used during the day",
        NaturalSpawn: "at nightfall, 1 in 3 chance if not yet defeated, a player has at least 200 max life and more "
            + "than 10 defense, and enough town NPCs live in the world (\"You feel an evil presence watching you...\")",
        Leaves: "flies away (despawns, no loot) at dawn, or if every player is dead or far away",
        Stats: "2800 life, 12 defense, contact 15; below 50% life it transforms (mouth form): contact 23, 0 defense, "
            + "dashes more often and faster. Summons Servants of Cthulhu (8 life, contact 12) that home in. Flies "
            + "through blocks.",
        Tips: new[]
        {
            "Fight on a wide arena: 2-3 rows of platforms 60-100 tiles long, ~5 tiles apart (build_platforms rows/spacing), with nothing to trip over.",
            "Ranged weapons work best (a bow with plenty of arrows); attack kites across the arena and dodges the dashes.",
            "Bring Lesser Healing Potions and raise the quick-heal threshold for the fight (attack heal_below, e.g. 0.5).",
            "Summon it right after dusk so the whole night is available; it leaves at 4:30 AM.",
            "Drops Demonite or Crimtane Ore and Corrupt/Crimson Seeds (Unholy Arrows in the Corruption).",
        });

    public static readonly BossFact KingSlime = new(
        Key: "king_slime",
        Name: "King Slime",
        Summon: "Slime Crown (consumed on use)",
        Recipe: "20 Gel + 1 Gold Crown (or Platinum Crown) at a Demon Altar or Crimson Altar",
        SummonWhen: "any time of day",
        NaturalSpawn: "rarely on the surface in the outer thirds of the world, or during a Slime Rain after enough "
            + "slimes are killed",
        Leaves: "despawns if every player is dead or far away",
        Stats: "2000 life, 10 defense, contact 40; jumps at the player, teleports when it can't reach you, and "
            + "releases Blue Slimes when hit. Shrinks as it loses life.",
        Tips: new[]
        {
            "A wide flat open area (80+ tiles, nothing to trip over): it hops toward you faster than you run (small, small, medium, big jumps), so run under it while it is high in the air rather than into a corner; attack does this itself.",
            "Platforms help little: its big jump reaches ~17 tiles up.",
            "Any weapon works; ranged lets you keep away from its jumps.",
            "Drops a Solidifier, often a Slime Hook, Slime Gun, Slimy Saddle or Ninja armor piece.",
        });

    public static readonly IReadOnlyList<BossFact> All = new[] { EyeOfCthulhu, KingSlime };

    /// <summary>Find by key or name, ignoring case, spaces and underscores ("eoc"/"eye" also match the Eye).</summary>
    public static BossFact? Find(string query)
    {
        string q = Norm(query);
        if (q is "eoc" or "eye") return EyeOfCthulhu;
        if (q is "ks" or "slime") return KingSlime;
        foreach (var b in All)
            if (Norm(b.Key) == q || Norm(b.Name) == q) return b;
        return null;
    }

    private static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
