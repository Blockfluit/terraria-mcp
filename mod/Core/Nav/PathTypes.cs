using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Nav;

public enum MoveKind { Walk, StepUp, Jump, Fall, DropThrough, Swim, Dig, BuildPlatform, OpenDoor, CloseDoor, BuildDown }

/// <summary>One step of a path. Pos is the standing position (feet-left tile) after the step.
/// WorkTile is the tile to dig (Dig), place a platform on (BuildPlatform, BuildDown) or the door tile to open/close
/// (OpenDoor/CloseDoor: Pos = the previous position, the player stands still and interacts).</summary>
public readonly record struct PathStep(TilePoint Pos, MoveKind Kind, TilePoint? WorkTile = null);

/// <param name="OpenDoors">Walk through closed doors, opening them on the way (an OpenDoor step before the Walk).</param>
/// <param name="CloseDoorsBehind">Close each door the path opened once the body is past it (best effort: a
/// CloseDoor step; the follower gives up on it if the door won't close).</param>
/// <param name="DigToGoal">A goal inside diggable rock is kept (the path digs the body out) instead of snapping to
/// the nearest open cell. For reaching a tile to mine when no open spot is in reach; needs AllowDig.</param>
/// <param name="BestEffort">When the goal can't be reached, return a path to the reached spot closest to it (if
/// that is closer than the start) instead of failing; ResolvedGoal is that spot. Used for goals beyond the planning
/// window.</param>
/// <param name="GoalSet">When set, the search ends at the first of these standing positions it reaches (its ResolvedGoal
/// is that position) and nowhere else: the goal and GoalTolerance only steer it, and a goal the body can't occupy is no
/// failure. For "anywhere within talk range of an NPC" (TalkStands). Partial plans to intermediate targets
/// (PlanJob) drop it.</param>
public sealed record PathOptions(
    bool AllowDig = true,
    bool AllowBuild = true,
    int MaxNodes = 20000,
    int GoalTolerance = 1,
    bool OpenDoors = true,
    bool CloseDoorsBehind = true,
    bool DigToGoal = false,
    bool BestEffort = false,
    IReadOnlySet<TilePoint>? GoalSet = null);

/// <param name="ResolvedGoal">Where the search actually aimed: the requested goal, or the nearest standable cell
/// when the requested one wasn't (snapped up to <see cref="Pathfinder.GoalSearchRadius"/> tiles). A successful
/// path ends within <see cref="PathOptions.GoalTolerance"/> of this, not necessarily of the requested goal.
/// Null on failure.</param>
public sealed record PathResult(
    bool Success,
    IReadOnlyList<PathStep> Steps,
    string? FailureReason,
    int NodesExpanded,
    TilePoint? ResolvedGoal = null);
