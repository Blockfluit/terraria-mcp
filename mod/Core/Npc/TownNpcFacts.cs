namespace LlmPlayer.Core.Npc;

/// <summary>A move-in condition the game checks live (vanilla 1.4.4 Main.UpdateTime_SpawnTownNPCs / NPC.SpawnAllowed_*).</summary>
public enum SpawnCondition
{
    Always,
    /// <summary>NPC.SpawnAllowed_Merchant: 50 silver worth of coins in the inventory (58 slots, coin slots included).</summary>
    Coins50Silver,
    /// <summary>NPC.SpawnAllowed_Nurse: the player has used a Life Crystal.</summary>
    LifeCrystalUsed,
    /// <summary>NPC.SpawnAllowed_Demolitionist: an explosive (bomb, grenade, dynamite…) in the inventory.</summary>
    Explosive,
    /// <summary>NPC.SpawnAllowed_ArmsDealer: bullets or a gun that fires bullets in the inventory.</summary>
    BulletOrGun,
    /// <summary>Eye of Cthulhu, Eater of Worlds/Brain of Cthulhu or Skeletron defeated.</summary>
    AnyBossDefeated,
    SkeletronDefeated,
    /// <summary>NPC.SpawnAllowed_DyeTrader: a dye or dye material in the inventory.</summary>
    DyeItem,
    FourTownNpcs,
    EightTownNpcs,
    RescuedAngler,
    RescuedStylist,
    RescuedGoblin,
    RescuedMechanic,
}

/// <summary>One town NPC's arrival rule. <paramref name="NpcType"/> is the vanilla NPCID; <paramref name="NeedsPresent"/>
/// other NPC types that must already live in the world.</summary>
public sealed record TownNpcFact(int NpcType, string Name, IReadOnlyList<SpawnCondition> Conditions, IReadOnlyList<int> NeedsPresent,
    string Condition, string Useful);

/// <summary>What get_npcs reports for an NPC the world doesn't have yet.</summary>
public sealed record NpcProspect(string Name, bool CanMoveIn, string Condition, string? Missing, string Useful);

/// <summary>
/// Static facts about the town NPCs worth having early (toward the Eye of Cthulhu), from vanilla 1.4.4
/// <c>Main.UpdateTime_SpawnTownNPCs</c>. Every NPC also needs a free valid house, and new residents arrive during the
/// day (vanilla runs the spawn check only in daytime). The live part (which conditions hold now) comes from the game;
/// <see cref="Prospects"/> combines both.
/// </summary>
public static class TownNpcFacts
{
    public const int Merchant = 17, Nurse = 18, ArmsDealer = 19, Dryad = 20, Guide = 22, Demolitionist = 38, Clothier = 54,
        GoblinTinkerer = 107, Mechanic = 124, DyeTrader = 207, Painter = 227, Stylist = 353, Angler = 369;

    private static TownNpcFact F(int type, string name, SpawnCondition c, string text, string useful, params int[] needs) =>
        new(type, name, new[] { c }, needs, text, useful);

    public static readonly IReadOnlyList<TownNpcFact> All = new[]
    {
        F(Guide, "Guide", SpawnCondition.Always, "always moves in when there is none", "shows recipes for an item"),
        F(Merchant, "Merchant", SpawnCondition.Coins50Silver, "50 silver in your inventory",
            "sells torches, rope, Lesser Healing/Mana Potions, arrows, Piggy Bank"),
        F(Nurse, "Nurse", SpawnCondition.LifeCrystalUsed, "you have used a Life Crystal, and the Merchant lives here",
            "heals you for coins (great during boss fights)", Merchant),
        F(ArmsDealer, "Arms Dealer", SpawnCondition.BulletOrGun, "bullets or a bullet-firing gun in your inventory",
            "sells Musket Balls, Flintlock Pistol, Minishark"),
        F(Demolitionist, "Demolitionist", SpawnCondition.Explosive, "an explosive (bomb, grenade, dynamite) in your inventory, and the Merchant lives here",
            "sells grenades, bombs and dynamite", Merchant),
        F(Dryad, "Dryad", SpawnCondition.AnyBossDefeated, "a boss defeated (Eye of Cthulhu, Eater of Worlds/Brain of Cthulhu or Skeletron)",
            "sells Purification Powder and seeds; reports the world's evil percentage"),
        new(DyeTrader, "Dye Trader", new[] { SpawnCondition.DyeItem, SpawnCondition.FourTownNpcs }, Array.Empty<int>(),
            "a dye or dye material in your inventory and 4 town NPCs", "sells dyes"),
        F(Painter, "Painter", SpawnCondition.EightTownNpcs, "8 other town NPCs", "sells paint and wallpaper"),
        F(Angler, "Angler", SpawnCondition.RescuedAngler, "found asleep at the ocean and woken up (talk to him)", "fishing quests"),
        F(Stylist, "Stylist", SpawnCondition.RescuedStylist, "rescued from a spider cave (talk to her)", "hairstyles"),
        F(Clothier, "Clothier", SpawnCondition.SkeletronDefeated, "Skeletron defeated", "sells vanity clothes"),
        F(GoblinTinkerer, "Goblin Tinkerer", SpawnCondition.RescuedGoblin, "found bound in the cavern layer after a Goblin Army (talk to him)",
            "reforges items; combines accessories"),
        F(Mechanic, "Mechanic", SpawnCondition.RescuedMechanic, "rescued in the Dungeon (talk to her)", "sells wire and mechanisms"),
    };

    public static TownNpcFact? ByType(int npcType) => All.FirstOrDefault(f => f.NpcType == npcType);

    /// <summary>
    /// For each fact whose NPC isn't present: whether it can move in now (every condition met per
    /// <paramref name="met"/>, every required NPC present) and, if not, what is missing. Town-NPC counts come from
    /// <paramref name="townNpcCount"/>.
    /// </summary>
    public static List<NpcProspect> Prospects(IReadOnlySet<int> present, int townNpcCount, Func<SpawnCondition, bool> met)
    {
        var list = new List<NpcProspect>();
        foreach (var f in All)
        {
            if (present.Contains(f.NpcType)) continue;
            var missing = new List<string>();
            foreach (var c in f.Conditions)
            {
                bool ok = c switch
                {
                    SpawnCondition.Always => true,
                    SpawnCondition.FourTownNpcs => townNpcCount >= 4,
                    SpawnCondition.EightTownNpcs => townNpcCount >= 8,
                    _ => met(c),
                };
                if (!ok) missing.Add(Describe(c, townNpcCount));
            }
            foreach (int need in f.NeedsPresent)
                if (!present.Contains(need)) missing.Add($"the {ByType(need)?.Name ?? $"NPC {need}"} must live here first");
            list.Add(new NpcProspect(f.Name, missing.Count == 0, f.Condition, missing.Count == 0 ? null : string.Join("; ", missing), f.Useful));
        }
        return list;
    }

    public static string Describe(SpawnCondition c, int townNpcCount = 0) => c switch
    {
        SpawnCondition.Coins50Silver => "carry at least 50 silver in coins",
        SpawnCondition.LifeCrystalUsed => "use a Life Crystal",
        SpawnCondition.Explosive => "carry an explosive (e.g. a Bomb or Grenade)",
        SpawnCondition.BulletOrGun => "carry bullets (Musket Ball) or a gun that fires them",
        SpawnCondition.AnyBossDefeated => "defeat a boss (e.g. the Eye of Cthulhu)",
        SpawnCondition.SkeletronDefeated => "defeat Skeletron",
        SpawnCondition.DyeItem => "carry a dye or dye material (e.g. Yellow Marigold)",
        SpawnCondition.FourTownNpcs => $"4 town NPCs (have {townNpcCount})",
        SpawnCondition.EightTownNpcs => $"8 town NPCs (have {townNpcCount})",
        SpawnCondition.RescuedAngler => "find and wake the Angler at the ocean",
        SpawnCondition.RescuedStylist => "rescue the Stylist from a spider cave",
        SpawnCondition.RescuedGoblin => "rescue the Goblin Tinkerer in the caverns",
        SpawnCondition.RescuedMechanic => "rescue the Mechanic in the Dungeon",
        _ => c.ToString(),
    };
}
