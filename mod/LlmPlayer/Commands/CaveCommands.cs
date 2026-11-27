using System.Text.Json;
using LlmPlayer.Bridge;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Observe;
using LlmPlayer.Interact;
using LlmPlayer.Observe;
using LlmPlayer.Tasks;
using Terraria;

namespace LlmPlayer.Commands;

/// <summary>Cave loot: collect (break a pot / life crystal / … and pick up the drops), loot_chest (open + Loot All +
/// close) and explore_caves (frontier exploration with stop_on). All start tasks.</summary>
[CommandGroup]
public static class CaveCommands
{
    public const int MaxRadius = 200, DefaultRadius = 80, MaxSeconds = 600, DefaultSeconds = 120;

    public static void Register()
    {
        CommandRouter.Register("collect", p =>
        {
            var target = CollectTask.Resolve(Main.LocalPlayer, Params.Int(p, "x"), Params.Int(p, "y"),
                allowOrb: Params.Has(p, "allow_orb") && Params.Bool(p, "allow_orb"),
                allowBossSummon: Params.Has(p, "allow_boss_summon") && Params.Bool(p, "allow_boss_summon"));
            var task = new CollectTask(target);
            TaskHost.Start(task);
            return new { Started = true, Task = task.Name, Target = new { target.At.X, target.At.Y, target.Name } };
        }, isAction: true);

        // The only path that may use explosives: safety-checked and planned in Core (BlastSafety, BlastPlanner).
        CommandRouter.Register("blast", p =>
        {
            var s = BlastTask.Resolve(Main.LocalPlayer, Params.Int(p, "x"), Params.Int(p, "y"), Params.StringOrNull(p, "item"),
                allowOrb: Params.Has(p, "allow_orb") && Params.Bool(p, "allow_orb"),
                allowBossSummon: Params.Has(p, "allow_boss_summon") && Params.Bool(p, "allow_boss_summon"));
            var task = new BlastTask(s);
            TaskHost.Start(task);
            var r = s.Report;
            return new
            {
                Started = true, Task = task.Name, Item = s.Spec.Name, Target = new { s.Target.X, s.Target.Y, Name = s.TargetName },
                Stand = new { s.Plan.Stand.X, s.Plan.Stand.Y },
                PredictedExplosion = new { s.Plan.DetTile.X, s.Plan.DetTile.Y, Miss = MathF.Round(s.Plan.Miss, 1) },
                Radius = s.Spec.TileRadius, FuseSeconds = MathF.Round(s.Spec.FuseTicks / 60f, 1),
                ExpectedDestroyed = new
                {
                    Count = r.Destroys.Count, ByName = r.DestroysByName,
                    Tiles = r.Destroys.Take(40).Select(t => new { t.X, t.Y }).ToList(),
                    Unseen = r.UnseenInRadius, WallsInRadius = r.Walls, r.Orbs,
                },
            };
        }, isAction: true);

        CommandRouter.Register("loot_chest", p =>
        {
            var pl = Main.LocalPlayer;
            ContainerRef c;
            if (Params.Has(p, "x") || Params.Has(p, "y"))
            {
                if (!Params.Has(p, "x") || !Params.Has(p, "y")) throw new CommandError(ErrorCodes.BadParams, "give both x and y, or neither");
                int x = Params.Int(p, "x"), y = Params.Int(p, "y");
                if (!WorldGen.InWorld(x, y, 10)) throw new CommandError(ErrorCodes.BadParams, $"({x},{y}) is outside the world");
                if (!Visibility.IsTileVisible(x, y)) throw new CommandError(ErrorCodes.NotVisible, $"({x},{y}) is not visible");
                c = ChestAccess.Resolve(x, y) ?? throw new CommandError(ErrorCodes.BadParams, $"no chest or other container at ({x},{y})");
            }
            else
            {
                c = Finder.Find(pl, "chest", null)
                        .Select(h => ChestAccess.Resolve(h.X, h.Y))
                        .FirstOrDefault(r => r is { } cr && ChestAccess.Refusal(pl, cr) == null)
                    ?? throw new CommandError(ErrorCodes.BadParams, "no visible chest you can open nearby (give x,y or explore_caves)");
            }
            if (ChestAccess.Refusal(pl, c) is { } why) throw new CommandError(ErrorCodes.BadParams, why);
            var task = new LootChestTask(c);
            TaskHost.Start(task);
            return new { Started = true, Task = task.Name, Chest = new { c.X, c.Y, Name = ChestAccess.NameOf(c) } };
        }, isAction: true);

        CommandRouter.Register("explore_caves", p =>
        {
            int radius = Params.Int(p, "radius", DefaultRadius);
            if (radius < 10 || radius > MaxRadius) throw new CommandError(ErrorCodes.BadParams, $"radius must be 10..{MaxRadius}");
            int seconds = Params.Int(p, "max_seconds", DefaultSeconds);
            if (seconds < 5 || seconds > MaxSeconds) throw new CommandError(ErrorCodes.BadParams, $"max_seconds must be 5..{MaxSeconds}");
            var stopOn = StopOn(p);
            var task = new ExploreCavesTask(radius, seconds, stopOn, Params.Bool(p, "collect_pots", false), Params.Bool(p, "torches", true));
            TaskHost.Start(task);
            return new { Started = true, Task = task.Name, Radius = radius, MaxSeconds = seconds, StopOn = stopOn.Select(Valuables.Key) };
        }, isAction: true);
    }

    /// <summary>stop_on: an array of kind keys (default <see cref="Valuables.DefaultStopOn"/>; [] = never stop early).</summary>
    private static List<ValuableKind> StopOn(JsonElement p)
    {
        if (!Params.Has(p, "stop_on")) return Valuables.DefaultStopOn.ToList();
        var v = p.GetProperty("stop_on");
        if (v.ValueKind != JsonValueKind.Array)
            throw new CommandError(ErrorCodes.BadParams, $"stop_on must be an array of: {string.Join(", ", Valuables.AllKeys)}");
        var list = new List<ValuableKind>();
        foreach (var e in v.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String || !Valuables.TryParse(e.GetString(), out var k))
                throw new CommandError(ErrorCodes.BadParams, $"stop_on: unknown kind {e}; use {string.Join(", ", Valuables.AllKeys)}");
            if (!list.Contains(k)) list.Add(k);
        }
        return list;
    }
}
