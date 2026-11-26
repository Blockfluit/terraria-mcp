using LlmPlayer.Core.Tasks;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class TaskRunnerTests
{
    private sealed class FakeTask : IGameTask<int>
    {
        public string Name { get; }
        public GameTaskStatus Next = GameTaskStatus.Running;
        public Exception? Throw;
        public int Cancels;
        public int Ticks;
        public string? Reason { get; set; }
        public string? Progress { get; set; }
        public FakeTask(string name = "fake") { Name = name; }
        public GameTaskStatus Tick(int ctx)
        {
            Ticks++;
            if (Throw != null) throw Throw;
            return Next;
        }
        public void OnCancel(int ctx) => Cancels++;
    }

    [Fact]
    public void Start_ThenSucceeds_RaisesFinishedOnce()
    {
        var r = new TaskRunner<int>();
        var reports = new List<TaskReport>();
        r.Finished += reports.Add;
        var t = new FakeTask { Reason = "done" };
        r.Start(t, 0);
        Assert.False(r.IsIdle);
        r.Tick(0);
        Assert.Equal(GameTaskStatus.Running, r.Current!.Status);
        t.Next = GameTaskStatus.Succeeded;
        r.Tick(0);
        r.Tick(0);
        Assert.True(r.IsIdle);
        Assert.Null(r.Current);
        Assert.Single(reports);
        Assert.Equal(GameTaskStatus.Succeeded, r.LastFinished!.Status);
        Assert.Equal("done", r.LastFinished.Reason);
        Assert.Equal(2, t.Ticks);
    }

    [Fact]
    public void StartWhileRunning_CancelsOldWithReplaced()
    {
        var r = new TaskRunner<int>();
        var reports = new List<TaskReport>();
        r.Finished += reports.Add;
        var a = new FakeTask("a");
        var b = new FakeTask("b");
        r.Start(a, 0);
        r.Start(b, 0);
        Assert.Equal(1, a.Cancels);
        Assert.Single(reports);
        Assert.Equal("a", reports[0].Name);
        Assert.Equal(GameTaskStatus.Cancelled, reports[0].Status);
        Assert.Equal("replaced", reports[0].Reason);
        Assert.Equal("b", r.Current!.Name);
    }

    [Fact]
    public void CancelAll_ReportsReason()
    {
        var r = new TaskRunner<int>();
        var t = new FakeTask();
        r.Start(t, 0);
        r.Cancel("player_died", 0);
        Assert.Equal(GameTaskStatus.Cancelled, r.LastFinished!.Status);
        Assert.Equal("player_died", r.LastFinished.Reason);
        Assert.Equal(1, t.Cancels);
        Assert.True(r.IsIdle);
    }

    [Fact]
    public void Cancel_WhenIdle_IsNoOp()
    {
        var r = new TaskRunner<int>();
        int fired = 0;
        r.Finished += _ => fired++;
        r.Cancel("x", 0);
        Assert.Equal(0, fired);
        Assert.Null(r.LastFinished);
    }

    [Fact]
    public void TaskThrows_FailsWithInternalReason()
    {
        var r = new TaskRunner<int>();
        var reports = new List<TaskReport>();
        r.Finished += reports.Add;
        r.Start(new FakeTask { Throw = new InvalidOperationException("boom") }, 0);
        r.Tick(0);
        Assert.Single(reports);
        Assert.Equal(GameTaskStatus.Failed, r.LastFinished!.Status);
        Assert.Equal("internal: boom", r.LastFinished.Reason);
        Assert.True(r.IsIdle);
    }

    [Fact]
    public void Finished_FiresOncePerTask_AcrossCancelAndReplace()
    {
        var r = new TaskRunner<int>();
        int fired = 0;
        r.Finished += _ => fired++;
        r.Start(new FakeTask(), 0);
        r.Cancel("a", 0);
        r.Cancel("b", 0);
        r.Start(new FakeTask(), 0);
        r.Start(new FakeTask(), 0);
        r.Cancel("c", 0);
        Assert.Equal(3, fired);
    }

    [Fact]
    public void Current_ExposesProgress()
    {
        var r = new TaskRunner<int>();
        r.Start(new FakeTask { Progress = "5 tiles left" }, 0);
        r.Tick(0);
        Assert.Equal("5 tiles left", r.Current!.Progress);
    }

    private sealed class ResultTask : IGameTask<int>
    {
        public string Name => "talk";
        public string? Reason => null;
        public string? Progress => null;
        public object? Result { get; set; }
        public GameTaskStatus Tick(int ctx) => GameTaskStatus.Succeeded;
        public void OnCancel(int ctx) { }
    }

    [Fact]
    public void Result_DefaultsToNull()
    {
        var r = new TaskRunner<int>();
        r.Start(new FakeTask(), 0);
        r.Tick(0);
        Assert.Null(r.Current!.Result);
    }

    [Fact]
    public void Result_IsCarriedOnFinishedReport()
    {
        var r = new TaskRunner<int>();
        var reports = new List<TaskReport>();
        r.Finished += reports.Add;
        var payload = new { Chat = "hi" };
        r.Start(new ResultTask { Result = payload }, 0);
        r.Tick(0);
        Assert.Same(payload, reports.Single().Result);
        Assert.Same(payload, r.LastFinished!.Result);
    }

    [Fact]
    public void Result_NullIsOmittedFromJson_PresentWhenSet()
    {
        var opts = LlmPlayer.Core.Bridge.WireProtocol.Json;
        var without = System.Text.Json.JsonSerializer.Serialize(new TaskReport("a", GameTaskStatus.Succeeded, null, null), opts);
        Assert.DoesNotContain("result", without);
        var with = System.Text.Json.JsonSerializer.Serialize(
            new TaskReport("a", GameTaskStatus.Succeeded, null, null, new { Name = "Guide" }), opts);
        Assert.Contains("\"result\":{\"name\":\"Guide\"}", with);
    }
}
