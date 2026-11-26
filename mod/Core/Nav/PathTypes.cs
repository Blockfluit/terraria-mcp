using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Nav;

public enum MoveKind { Walk, StepUp, Jump, Fall, DropThrough, Swim, Dig, BuildPlatform }

/// <summary>One step of a path. Pos is the standing position (feet-left tile) after the step.
/// WorkTile is the tile to dig (Dig) or place a platform on (BuildPlatform).</summary>
public readonly record struct PathStep(TilePoint Pos, MoveKind Kind, TilePoint? WorkTile = null);

public sealed record PathOptions(
    bool AllowDig = true,
    bool AllowBuild = true,
    int MaxNodes = 20000,
    int GoalTolerance = 1);

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
