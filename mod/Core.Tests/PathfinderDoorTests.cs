using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class PathfinderDoorTests
{
    /// <summary>Two rooms split by an unbreakable wall with a door ('|', column 14, rows 5..7) at floor level.</summary>
    private static (ArrayTileGrid Grid, Dictionary<char, TilePoint> M) TwoRooms(int goalX = 24) =>
        new GridBuilder(30, 10).Fill(0, 8, 29, 9, '#').Fill(14, 0, 14, 4, 'X').Fill(14, 5, 14, 7, '|')
            .Put(2, 7, 'S').Put(goalX, 7, 'G').Parse();

    private static MovementProfile Profile(int pick = 0) => new(PickPower: pick);

    [Fact]
    public void DoorInWall_PathOpensItWalksThroughAndClosesIt()
    {
        var (g, m) = TwoRooms();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], Profile(), new(AllowDig: false, AllowBuild: false));
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(m['G'], r.Steps[^1].Pos);

        int open = r.Steps.ToList().FindIndex(s => s.Kind == MoveKind.OpenDoor);
        Assert.True(open >= 0);
        var openStep = r.Steps[open];
        Assert.Equal(14, openStep.WorkTile!.Value.X);
        Assert.InRange(openStep.WorkTile!.Value.Y, 5, 7);
        // Opened from where the player stands, next to the door, then the walk into the door column.
        Assert.Equal(new TilePoint(12, 7), openStep.Pos);
        Assert.Equal(new PathStep(new TilePoint(13, 7), MoveKind.Walk), r.Steps[open + 1]);
        Assert.Equal(1, r.Steps.Count(s => s.Kind == MoveKind.OpenDoor));

        int close = r.Steps.ToList().FindIndex(s => s.Kind == MoveKind.CloseDoor);
        Assert.True(close > open + 1);
        var closeStep = r.Steps[close];
        Assert.Equal(openStep.WorkTile, closeStep.WorkTile);
        // Closed from the first position whose body is past the door column, standing still (Pos = previous Pos).
        Assert.Equal(new TilePoint(15, 7), closeStep.Pos);
        Assert.Equal(r.Steps[close - 1].Pos, closeStep.Pos);
        // Everything else is plain walking.
        Assert.All(r.Steps, s => Assert.Contains(s.Kind, new[] { MoveKind.Walk, MoveKind.OpenDoor, MoveKind.CloseDoor }));
    }

    [Fact]
    public void DoorInWall_LeftwardsToo()
    {
        var (g, m) = TwoRooms();
        var r = Pathfinder.FindPath(g, m['G'], m['S'], Profile(), new(AllowDig: false, AllowBuild: false));
        Assert.True(r.Success, r.FailureReason);
        var open = r.Steps.Single(s => s.Kind == MoveKind.OpenDoor);
        Assert.Equal(new TilePoint(15, 7), open.Pos);
        var close = r.Steps.Single(s => s.Kind == MoveKind.CloseDoor);
        Assert.Equal(new TilePoint(12, 7), close.Pos);
    }

    [Fact]
    public void NoDoorOpenedWhenNotNeeded()
    {
        var (g, m) = TwoRooms(goalX: 9);
        var r = Pathfinder.FindPath(g, m['S'], m['G'], Profile(), new(AllowDig: false, AllowBuild: false));
        Assert.True(r.Success, r.FailureReason);
        Assert.DoesNotContain(r.Steps, s => s.Kind is MoveKind.OpenDoor or MoveKind.CloseDoor);
    }

    [Fact]
    public void OpenGapInsteadOfDoor_NoDoorSteps()
    {
        var (g, m) = new GridBuilder(30, 10).Fill(0, 8, 29, 9, '#').Fill(14, 0, 14, 4, 'X')
            .Put(2, 7, 'S').Put(24, 7, 'G').Parse();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], Profile(), new());
        Assert.True(r.Success);
        Assert.DoesNotContain(r.Steps, s => s.Kind is MoveKind.OpenDoor or MoveKind.CloseDoor);
    }

    [Fact]
    public void DoorsDisabled_Unreachable()
    {
        var (g, m) = TwoRooms();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], Profile(), new(AllowDig: false, AllowBuild: false, OpenDoors: false));
        Assert.False(r.Success);
    }

    [Fact]
    public void CloseDoorsBehindDisabled_LeavesItOpen()
    {
        var (g, m) = TwoRooms();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], Profile(), new(AllowDig: false, AllowBuild: false, CloseDoorsBehind: false));
        Assert.True(r.Success);
        Assert.Contains(r.Steps, s => s.Kind == MoveKind.OpenDoor);
        Assert.DoesNotContain(r.Steps, s => s.Kind == MoveKind.CloseDoor);
    }

    [Fact]
    public void GoalInTheClosedDoorway_SnapsInFrontOfItWithoutOpening()
    {
        // A body overlapping the closed door isn't a standable goal: it snaps to the nearest free cell.
        var (g, m) = TwoRooms(goalX: 13);
        var r = Pathfinder.FindPath(g, m['S'], m['G'], Profile(), new(AllowDig: false, AllowBuild: false, GoalTolerance: 0));
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(new TilePoint(12, 7), r.Steps[^1].Pos);
        Assert.DoesNotContain(r.Steps, s => s.Kind is MoveKind.OpenDoor or MoveKind.CloseDoor);
    }

    [Fact]
    public void ProtectedSolidIsNotADoor()
    {
        var (g, m) = new GridBuilder(30, 10).Fill(0, 8, 29, 9, '#').Fill(14, 0, 14, 4, 'X').Fill(14, 5, 14, 7, 'P')
            .Put(2, 7, 'S').Put(24, 7, 'G').Parse();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], Profile(pick: 100), new(AllowBuild: false));
        Assert.False(r.Success);
    }

    [Fact]
    public void DoorIsNeverDug()
    {
        var (g, m) = TwoRooms();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], Profile(pick: 1000), new(AllowBuild: false, OpenDoors: false));
        Assert.False(r.Success);
    }

    [Fact]
    public void HouseWithTwoDoors_InAndOut()
    {
        var (g, m) = new GridBuilder(34, 12).Fill(0, 9, 33, 11, '#').Fill(5, 0, 20, 3, 'X')
            .Fill(5, 4, 5, 5, 'X').Fill(5, 6, 5, 8, '|').Fill(20, 4, 20, 5, 'X').Fill(20, 6, 20, 8, '|')
            .Put(1, 8, 'S').Put(27, 8, 'G').Parse();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], Profile(), new(AllowDig: false, AllowBuild: false));
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(new[] { 5, 20 }, r.Steps.Where(s => s.Kind == MoveKind.OpenDoor).Select(s => s.WorkTile!.Value.X));
        Assert.Equal(new[] { 5, 20 }, r.Steps.Where(s => s.Kind == MoveKind.CloseDoor).Select(s => s.WorkTile!.Value.X));
    }
}
