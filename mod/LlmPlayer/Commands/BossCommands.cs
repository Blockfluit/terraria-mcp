using LlmPlayer.Bridge;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Combat;
using Terraria;

namespace LlmPlayer.Commands;

/// <summary>get_boss_info (observation): static strategy facts (Core <see cref="BossFacts"/>) plus the world's
/// defeated flags, which the player could see in the bestiary / boss checklist.</summary>
[CommandGroup]
public static class BossCommands
{
    public static void Register()
    {
        CommandRouter.Register("get_boss_info", p =>
        {
            string? query = Params.StringOrNull(p, "boss");
            IEnumerable<BossFact> facts = BossFacts.All;
            if (!string.IsNullOrWhiteSpace(query))
            {
                var one = BossFacts.Find(query)
                    ?? throw new CommandError(ErrorCodes.BadParams, $"no info for '{query}' (known: Eye of Cthulhu, King Slime)");
                facts = new[] { one };
            }
            return new
            {
                Bosses = facts.Select(f => new
                {
                    f.Key,
                    f.Name,
                    Defeated = Defeated(f.Key),
                    f.Summon,
                    f.Recipe,
                    f.SummonWhen,
                    f.NaturalSpawn,
                    f.Leaves,
                    f.Stats,
                    f.Tips,
                }).ToList(),
                IsNight = !Main.dayTime,
            };
        }, isAction: false);
    }

    private static bool? Defeated(string key) => key switch
    {
        "eye_of_cthulhu" => NPC.downedBoss1,
        "king_slime" => NPC.downedSlimeKing,
        _ => null,
    };
}
