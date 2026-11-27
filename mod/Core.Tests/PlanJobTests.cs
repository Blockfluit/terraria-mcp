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

    [Fact]
    public void PartialPlan_SkipsAnUnreachableNearestCandidate()
    {
        // Goal far right beyond the window. Near the window's right edge, the cell nearest the goal is a sealed pocket
        // (unbreakable box); the open floor a bit further left is reachable. Without retries the job failed.
        int w = 400;
        var b = new GridBuilder(w, 30).Fill(0, 20, w - 1, 29, '#');
        int edge = PlanJob.MaxWidth - PlanJob.Margin; // roughly where the window ends going right
        // A sealed pocket high up near the edge, closer to the goal row than the floor.
        b.Fill(edge - 12, 3, edge - 4, 9, 'X').Fill(edge - 11, 4, edge - 5, 8, '.').Fill(edge - 11, 9, edge - 5, 9, 'X');
        b.Put(2, 19, 'S');
        var (g, m) = b.Parse();
        var goal = new TilePoint(w - 5, 8); // high, so the pocket is nearer than the floor
        var job = new PlanJob(m['S'], goal, new MovementProfile(), new PathOptions(AllowDig: false, AllowBuild: false));
        var r = RunToEnd(job, g, out _);
        Assert.True(r.Success, r.FailureReason);
        Assert.True(job.IsPartial);
    }

    [Fact]
    public void GoalSet_BeyondTheWindow_StillPlansPartway_ThenEndsAtAMember()
    {
        // A goal set around a goal 400 tiles off: the first plan is partial (intermediate targets drop the set), and a
        // plan from near the goal ends at the nearest member.
        var (g, m) = Wide(420);
        var set = new HashSet<TilePoint> { new(m['G'].X - 5, 14), new(m['G'].X - 4, 14) };
        var opts = new PathOptions(AllowDig: false, AllowBuild: false, GoalSet: set);
        var far = RunToEnd(new PlanJob(m['S'], m['G'], new MovementProfile(), opts), g, out _);
        Assert.True(far.Success, far.FailureReason);
        var job = new PlanJob(new TilePoint(m['G'].X - 40, 14), m['G'], new MovementProfile(), opts);
        var near = RunToEnd(job, g, out _);
        Assert.True(near.Success, near.FailureReason);
        Assert.False(job.IsPartial);
        Assert.Equal(new TilePoint(m['G'].X - 5, 14), near.Steps[^1].Pos);
        Assert.Equal(near.Steps[^1].Pos, near.ResolvedGoal);
    }
}
