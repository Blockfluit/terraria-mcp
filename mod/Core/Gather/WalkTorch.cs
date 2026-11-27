using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;

namespace LlmPlayer.Core.Gather;

/// <summary>What move_to knows this tick when deciding whether to light the way (filled by the glue).</summary>
/// <param name="HasTorch">A torch is in the inventory.</param>
/// <param name="OnGround">Standing (not jumping or falling).</param>
/// <param name="InLiquid">The body is in water/honey/lava (no torch: vanilla refuses them in liquid).</param>
/// <param name="Step">The path step being followed (null: between steps).</param>
/// <param name="Underground">Below the surface layer (on the surface the dark is night, not a tunnel).</param>
/// <param name="Digging">This walk has dug (a fresh tunnel is dark even near the surface).</param>
/// <param name="BodyBrightness">Mean vanilla <c>Lighting.Brightness</c> (0..1) over the body's 6 tiles.</param>
/// <param name="NearestTorch">Chebyshev distance to the nearest torch tile, null if none within the spacing.</param>
/// <param name="ProtectedArea">The player stands in the spawn area or an NPC house (never placed there).</param>
public readonly record struct WalkTorchSense(bool HasTorch, bool OnGround, bool InLiquid, MoveKind? Step, bool Underground,
    bool Digging, float BodyBrightness, int? NearestTorch, bool ProtectedArea);

/// <summary>
/// move_to's torches, like a player lighting a tunnel as they go: whenever the body is dark
/// (<see cref="TorchPlanner.DarkBrightness"/>, the same rule as explore) underground or in a dug tunnel and no torch is
/// within <see cref="TorchPlanner.SpacingTiles"/>, place one within reach without walking.
/// <list type="bullet">
/// <item>Pacing (<see cref="Due"/>): looked at every <see cref="CheckTicks"/> ticks, at most one torch per
/// <see cref="MinGapTicks"/>, and after <see cref="MaxFailuresInARow"/> refused placements it stops trying for
/// <see cref="BackoffTicks"/>, so the walk never stalls on torches.</item>
/// <item>Only from a steady stance (<see cref="Blocker"/>): on the ground, out of liquid, during a walk, step-up or dig
/// step or between steps; never mid-jump, falling, swimming, building or at a door; never in protected areas.</item>
/// <item>Where (<see cref="Spot"/>): <see cref="TorchPlanner.BestSpot"/> aimed a few steps ahead at head height (the
/// light reveals the way), never on a tile the path will build a platform on, never anchored to a block the path is
/// about to dig (the torch would drop).</item>
/// </list>
/// </summary>
public sealed class WalkTorch
{
    public const int CheckTicks = 20, MinGapTicks = 90, MaxFailuresInARow = 3, BackoffTicks = 600, AimAhead = 4, Lookahead = 12;

    private int _cooldown, _failures;

    public int Placed { get; private set; }

    /// <summary>Call once per walking tick; true when it's time to look (a cheap gate before reading the world).</summary>
    public bool Due()
    {
        if (_cooldown > 0)
        {
            _cooldown--;
            return false;
        }
        _cooldown = CheckTicks;
        return true;
    }

    /// <summary>Why no torch goes up now, or null when one should.</summary>
    public static string? Blocker(in WalkTorchSense s)
    {
        if (!s.HasTorch) return "no torches";
        if (s.ProtectedArea) return "protected area";
        if (!s.OnGround || s.InLiquid) return "not standing";
        if (s.Step is { } k && k is not (MoveKind.Walk or MoveKind.StepUp or MoveKind.Dig)) return "busy with a step";
        if (!s.Underground && !s.Digging) return "surface";
        if (!TorchPlanner.ShouldPlace(s.BodyBrightness, s.NearestTorch)) return s.BodyBrightness < TorchPlanner.DarkBrightness ? "torch nearby" : "light enough";
        return null;
    }

    public void OnPlaced()
    {
        Placed++;
        _failures = 0;
        _cooldown = MinGapTicks;
    }

    public void OnFailed()
    {
        if (++_failures < MaxFailuresInARow) return;
        _failures = 0;
        _cooldown = BackoffTicks;
    }

    /// <summary>The tile to put the torch on, within reach from <paramref name="stand"/>, or null.</summary>
    /// <param name="ahead">The path's remaining steps (current first).</param>
    public static TilePoint? Spot(ITileGrid grid, Func<TilePoint, TorchCell> cell, TilePoint stand, ReachSpec reach,
        IEnumerable<PathStep> ahead)
    {
        var steps = ahead.Take(Lookahead).ToList();
        var dig = new HashSet<TilePoint>();
        var build = new HashSet<TilePoint>();
        foreach (var s in steps)
        {
            if (s.WorkTile is not { } w) continue;
            if (s.Kind == MoveKind.Dig) dig.Add(w);
            else if (s.Kind is MoveKind.BuildPlatform or MoveKind.BuildDown) build.Add(w);
        }
        TilePoint? focus = steps.Count == 0 ? null : steps[Math.Min(AimAhead, steps.Count) - 1].Pos is var p ? new TilePoint(p.X, p.Y - 1) : null;
        TorchCell Cell(TilePoint t) => build.Contains(t) || dig.Contains(t) ? new TorchCell(false, false) : cell(t);
        return TorchPlanner.BestSpot(grid, Cell, stand, reach, focus, dig);
    }
}

/// <summary>move_to's structured result (report.result).</summary>
public sealed record MoveToResult(int TorchesPlaced);
