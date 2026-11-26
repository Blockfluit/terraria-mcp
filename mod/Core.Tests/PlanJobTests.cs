using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class PlanJobTests
{
    /// <summary>Counts reads and records the calling thread, to prove the search never touches the live grid.</summary>
    private sealed class LiveGrid(ITileGrid inner) : ITileGrid
    {
        public int Reads;
        public readonly HashSet<int> Threads = new();
        public TileInfo Get(int x, int y)
        {
            Reads++;
            lock (Threads) Threads.Add(Environment.CurrentManagedThreadId);
            return inner.Get(x, y);
        }
    }

    private static PathResult RunToEnd(PlanJob job, ITileGrid source, out int ticks)
    {
        ticks = 0;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!job.Step(source))
        {
            ticks++;
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            Thread.Sleep(1);
        }
        return job.Result!;
    }

    private static (ArrayTileGrid, Dictionary<char, TilePoint>) Wide(int w) =>
        new GridBuilder(w, 20).Fill(0, 15, w - 1, 19, '#').Put(2, 14, 'S').Put(w - 4, 14, 'G').Parse();

    [Fact]
    public void SameResultAsDirectSearch_AndSearchNeverReadsLiveGrid()
    {
        var (g, m) = Wide(80);
        var live = new LiveGrid(g);
        int mainThread = Environment.CurrentManagedThreadId;
        var job = new PlanJob(m['S'], m['G'], new MovementProfile(), new PathOptions());
        var r = RunToEnd(job, live, out _);
        var direct = Pathfinder.FindPath(g, m['S'], m['G'], new MovementProfile(), new PathOptions());
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(direct.Steps, r.Steps);
        Assert.Equal(new[] { mainThread }, live.Threads);
        Assert.False(job.IsPartial);
    }

    [Fact]
    public void SnapshotIsSpreadOverTicks()
    {
        var (g, m) = new GridBuilder(150, 120).Fill(0, 110, 149, 119, '#').Put(2, 109, 'S').Put(140, 10, 'G').Parse();
        var live = new LiveGrid(g);
        var job = new PlanJob(m['S'], m['G'], new MovementProfile(), new PathOptions());
        int before = 0, maxPerTick = 0;
        for (int i = 0; i < 1000 && !job.Step(live); i++)
        {
            maxPerTick = Math.Max(maxPerTick, live.Reads - before);
            before = live.Reads;
            Thread.Sleep(1);
        }
        Assert.InRange(maxPerTick, 1, PlanJob.TilesPerTick);
    }

    [Fact]
    public void FarGoal_PlansPartwayTowardIt()
    {
        var (g, m) = Wide(600);
        var job = new PlanJob(m['S'], m['G'], new MovementProfile(), new PathOptions());
        var r = RunToEnd(job, g, out _);
        Assert.True(r.Success, r.FailureReason);
        Assert.True(job.IsPartial);
        var end = r.Steps[^1].Pos;
        Assert.True(end.X > m['S'].X + PlanJob.MaxWidth / 2, $"ended at {end}");
        Assert.Equal(job.PlannedGoal, end);
    }

    [Fact]
    public void Cancel_DropsResult()
    {
        var (g, m) = Wide(80);
        var job = new PlanJob(m['S'], m['G'], new MovementProfile(), new PathOptions());
        job.Cancel();
        Assert.True(job.Step(g));
        Assert.False(job.Result!.Success);
        Assert.Contains("cancelled", job.Result.FailureReason);
    }
}
