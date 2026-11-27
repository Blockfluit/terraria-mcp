using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;
using Xunit.Abstractions;

namespace LlmPlayer.Core.Tests;

/// <summary>Breath: submerged steps (head in water/honey) cost extra and are capped per run, unless water breathing.</summary>
public class PathfinderBreathTests(ITestOutputHelper output)
{
    private static readonly PathOptions NoWork = new(AllowDig: false, AllowBuild: false);

    private static int MaxSubmergedRun(ArrayTileGrid g, TilePoint start, PathResult r)
    {
        bool Wet(TilePoint p) => g.Get(p.X, p.Y - 2).Kind is TileKind.Water or TileKind.Honey
                                 || g.Get(p.X + 1, p.Y - 2).Kind is TileKind.Water or TileKind.Honey;
        int run = Wet(start) ? 1 : 0, max = run;
        foreach (var s in r.Steps)
        {
            run = Wet(s.Pos) ? run + 1 : 0;
            max = Math.Max(max, run);
        }
        return max;
    }

    private void Log(PathResult r) =>
        output.WriteLine($"{r.Success} {r.FailureReason} " + string.Join(" ", r.Steps.Select(s => $"{s.Kind}{s.Pos}")));

    [Fact]
    public void VanillaBreath_GivesElevenSteps()
    {
        Assert.Equal(11, BreathMath.VanillaMaxSubmergedSteps);
        Assert.Equal(11, BreathMath.MaxSubmergedSteps(200, 7));
        Assert.Equal(BreathMath.VanillaMaxSubmergedSteps, new MovementProfile().MaxSubmergedSteps);
        Assert.True(BreathMath.MaxSubmergedSteps(200, 7 * 6) > 30); // diving gear
        Assert.Equal(BreathMath.MinSteps, BreathMath.MaxSubmergedSteps(0, 7));
    }

    /// <summary>
    /// Upper corridor (S) and lower corridor (G) joined by a short flooded shaft at x=3..4 and a dry shaft at the
    /// far end (walk right, drop, walk back).
    /// </summary>
    private static (ArrayTileGrid, Dictionary<char, TilePoint>) TwoWays() =>
        new GridBuilder(16, 14).Fill(0, 0, 15, 13, 'X')
            .Fill(1, 1, 14, 3, '.').Fill(1, 9, 14, 11, '.').Fill(13, 4, 14, 8, '.').Fill(3, 4, 4, 8, '~')
            .Put(1, 3, 'S').Put(1, 11, 'G').Parse();

    [Fact]
    public void PrefersTheDryDetour_OverDiving()
    {
        var (g, m) = TwoWays();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(), NoWork);
        Log(r);
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(0, MaxSubmergedRun(g, m['S'], r)); // may splash through the shaft's top, never dives
        Assert.Contains(r.Steps, s => s.Pos.X >= 12 && s.Pos.Y == 11); // down the dry shaft
    }

    [Fact]
    public void WaterBreathing_TakesTheShortDive()
    {
        var (g, m) = TwoWays();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(WaterBreathing: true), NoWork);
        Log(r);
        Assert.True(r.Success, r.FailureReason);
        Assert.True(MaxSubmergedRun(g, m['S'], r) > 0);
        Assert.DoesNotContain(r.Steps, s => s.Pos.X >= 12 && s.Pos.Y == 11);
    }

    /// <summary>Two dry rooms joined only by a fully flooded 3-tall tunnel, <paramref name="length"/> tiles long.</summary>
    private static (ArrayTileGrid, Dictionary<char, TilePoint>) Tunnel(int length, int? pocketAt = null)
    {
        int w = length + 14;
        var b = new GridBuilder(w, 12).Fill(0, 0, w - 1, 11, 'X')
            .Fill(1, 1, 6, 8, '.').Fill(w - 7, 1, w - 2, 8, '.').Fill(7, 6, w - 8, 8, '~');
        if (pocketAt is { } px) b.Fill(px, 3, px + 3, 5, '.'); // an air pocket above the tunnel
        return b.Put(2, 8, 'S').Put(w - 4, 8, 'G').Parse();
    }

    [Fact]
    public void LongUnderwaterTunnel_IsRefused()
    {
        var (g, m) = Tunnel(30);
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(), NoWork);
        Log(r);
        Assert.False(r.Success);
    }

    [Fact]
    public void LongUnderwaterTunnel_WithWaterBreathing_IsSwum()
    {
        var (g, m) = Tunnel(30);
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(WaterBreathing: true), NoWork);
        Log(r);
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(m['G'], r.Steps[^1].Pos);
        Assert.True(MaxSubmergedRun(g, m['S'], r) > 20);
    }

    [Fact]
    public void ShortUnderwaterTunnel_IsSwum_WithinTheCap()
    {
        var (g, m) = Tunnel(6);
        var p = new MovementProfile();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], p, NoWork);
        Log(r);
        Assert.True(r.Success, r.FailureReason);
        Assert.InRange(MaxSubmergedRun(g, m['S'], r), 1, p.MaxSubmergedSteps);
    }

    [Fact]
    public void AirPocket_ResetsTheRun()
    {
        // 22 tiles of tunnel: too long in one breath of 14 steps, fine with a breather in the pocket halfway.
        var p = new MovementProfile(MaxSubmergedSteps: 14);
        var (g, m) = Tunnel(22);
        Assert.False(Pathfinder.FindPath(g, m['S'], m['G'], p, NoWork).Success);
        (g, m) = Tunnel(22, pocketAt: 16);
        var r = Pathfinder.FindPath(g, m['S'], m['G'], p, NoWork);
        Log(r);
        Assert.True(r.Success, r.FailureReason);
        Assert.InRange(MaxSubmergedRun(g, m['S'], r), 1, 14);
    }

    [Fact]
    public void SubmergedStart_StillFindsTheWayOut()
    {
        // Start at the bottom of a deep pool: the start counts as the first submerged step.
        var (g, m) = new GridBuilder(12, 14).Fill(0, 0, 11, 13, 'X').Fill(1, 1, 10, 4, '.').Fill(3, 5, 6, 12, '~')
            .Fill(1, 5, 2, 5, 'X').Put(3, 12, 'S').Put(8, 4, 'G').Parse();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(), NoWork);
        Log(r);
        Assert.True(r.Success, r.FailureReason);
    }
}
