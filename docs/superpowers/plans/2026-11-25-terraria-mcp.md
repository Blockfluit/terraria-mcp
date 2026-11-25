# Terraria MCP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An MCP server plus a tModLoader mod that let an LLM drive the user's own Terraria character when the user hands over control, with smart pathfinding movement and fair vision.

**Architecture:** A pure C# library (`LlmPlayer.Core`, no game references) holds everything testable: tile-grid model, A* pathfinder, path follower, ASCII map renderer, task runner, and user-input detection. The tModLoader mod `LlmPlayer` compiles Core's source in, adapts the game to it (tile grid, controls, observation), and exposes newline-delimited JSON over TCP. A TypeScript MCP server in WSL translates MCP tool calls into those JSON commands.

**Tech Stack:** C# / .NET 8, tModLoader 1.4.4, xUnit; TypeScript, Node 24, `@modelcontextprotocol/sdk`, `zod`, `vitest`.

**Spec:** `docs/superpowers/specs/2026-11-25-terraria-mcp-design.md`

## Global Constraints

- tModLoader 1.4.4 (.NET 8). Core library targets `net8.0` and must not reference XNA/Terraria/tModLoader.
- The mod listens on port `7878` by default and binds to `0.0.0.0` by default (both configurable). It accepts one client at a time.
- The MCP server gets the host from env `TERRARIA_HOST`, falling back to the WSL default gateway. It gets the port from env `TERRARIA_PORT` (default 7878).
- All game reads and writes happen on the main game thread. The TCP thread only parses JSON and enqueues.
- No cheating. Never teleport, spawn items, or ignore reach, tool power or cooldowns.
- The AI never takes control itself. Only the user's hotkey (default `F8`) hands it over. Any real user input while the AI is driving switches back to `user` and cancels the task.
- Fair vision. The AI may only see tiles that are map-revealed or currently lit and roughly on screen. Spelunker, Hunter and Dangersense widen this the same way they do for the player.
- Protected tiles are never broken: containers, furniture and other multi-tile objects, doors, crafting stations, anything inside a valid NPC house, and blocks within `ProtectSpawnRadius` (default 20) of spawn. Town NPCs and critters are never attacked.
- Items are never sold, trashed or dropped unless the LLM explicitly names the item.
- `get_map` radius is capped at 40.
- Coordinates are always absolute tile coordinates, with Y increasing downward. A player "standing at (x,y)" occupies tiles x..x+1 and y-2..y, and needs a solid tile or platform at (x,y+1) or (x+1,y+1).

## Review Focus

1. **The AI's virtual cursor mistaken for the user's mouse.** The mod moves the cursor while it aims. If input detection reads `Main.mouseX` instead of raw device state, the AI hands control back to the user every frame. Pinned by Task 5 `InputWatcherTests.CursorOverrideIsNotUserInput`.
2. **`move_to` an impossible target.** A goal inside solid rock, inside a protected tile, or behind unbreakable blocks must fail fast with a reason. It must not walk forever or stall the game. Pinned by Task 3 `GoalInsideSolid_UsesNearestStandable`, `Unreachable_FailsWithinNodeBudget` and `ProtectedTile_NeverDug`.
3. **The player dies or leaves the world mid-task.** The task must end `cancelled` with the reason `player_died` or `left_world`, and commands from the main menu must return `not_in_world`. Pinned by Task 5 `TaskRunnerTests.CancelAll_ReportsReason` and Task 7's manual checklist.
4. **The game isn't running, the mod reloaded, or the host is wrong.** MCP tools must return a readable error naming host:port, not hang, and reconnect on the next call. Pinned by Task 8 `connection.test.ts` "rejects with readable error when nothing listens" and "reconnects after server restart".
5. **Oversized observations.** A huge `radius` or a crowded area must not blow up the LLM's context. Pinned by Task 4 `MapRendererTests.RadiusClampedTo40` and Task 9's entity cap of 30.

---

## File Structure

```
.gitignore
README.md                               setup + usage (Task 14)
scripts/sync-mod.sh                     copy mod + Core source into tModLoader ModSources
scripts/build-mod.sh                    build the mod via Windows dotnet
mod/Core/LlmPlayer.Core.csproj
mod/Core/Grid/TilePoint.cs              record struct TilePoint(int X, int Y)
mod/Core/Grid/TileInfo.cs               TileKind enum + TileInfo record struct
mod/Core/Grid/ITileGrid.cs
mod/Core/Grid/ArrayTileGrid.cs          in-memory grid + ASCII Parse (tests, also used by mod for snapshots)
mod/Core/Nav/MovementProfile.cs
mod/Core/Nav/PathTypes.cs               MoveKind, PathStep, PathOptions, PathResult
mod/Core/Nav/Pathfinder.cs
mod/Core/Nav/PathFollower.cs            PlayerKinematics, ControlIntent, FollowerOutput
mod/Core/Map/MapRenderer.cs
mod/Core/Tasks/TaskRunner.cs            IGameTask, GameTaskStatus, TaskReport, TaskRunner
mod/Core/Control/InputWatcher.cs        RawInputSnapshot + IsUserInput
mod/Core.Tests/LlmPlayer.Core.Tests.csproj  (+ one test file per Core class)
mod/LlmPlayer/build.txt, description.txt, LlmPlayer.csproj, LlmPlayer.cs
mod/LlmPlayer/Config/LlmConfig.cs
mod/LlmPlayer/Bridge/Protocol.cs, JsonLineServer.cs, CommandRouter.cs, BridgeSystem.cs
mod/LlmPlayer/Control/ControlState.cs, AiModPlayer.cs, ControlIndicator.cs
mod/LlmPlayer/Safety/Protection.cs, SaveBackup.cs
mod/LlmPlayer/Observe/Visibility.cs, GameTileGrid.cs, StateReader.cs, EntityReader.cs, Finder.cs, InventoryReader.cs, ItemLookup.cs
mod/LlmPlayer/Tasks/MoveToTask.cs, ExploreTask.cs, MineTask.cs, ChopTreeTask.cs, PickUpTask.cs, PlaceTask.cs, UseItemTask.cs, AttackTask.cs, WaitTask.cs, Reflexes.cs
mod/LlmPlayer/Commands/*.cs             one static class per command group, registered in CommandRouter
mcp-server/package.json, tsconfig.json, vitest.config.ts
mcp-server/src/index.ts, host.ts, connection.ts, tools.ts
mcp-server/test/host.test.ts, connection.test.ts, tools.test.ts
```

## Wire Protocol (shared by Tasks 6–13)

- Request: `{"id":<int>,"method":"<name>","params":{...}}\n`
- Response: `{"id":<int>,"result":{...}}\n` or `{"id":<int>,"error":{"code":"<code>","message":"<text>"}}\n`
- Event: `{"event":"<name>", ...}\n`. Events: `task_finished`, `control_changed`, `took_damage`, `reflex` (death surfaces as `control_changed` with reason `player_died`).
- Error codes: `unknown_method`, `bad_params`, `not_in_world`, `user_has_control`, `not_visible`, `protected_tile`, `out_of_reach`, `missing_item`, `missing_station`, `no_path`, `internal`.
- Action methods (they start or modify tasks or change the player) return `user_has_control` while the driver is `user`. Observation methods always work while in a world.

| Method | Params | Task |
|---|---|---|
| `ping` | – | 6 |
| `get_control` / `request_control` | – | 7 |
| `get_state` | – | 6 (min), 9 (full) |
| `get_map` | `radius?=20` | 9 |
| `get_entities` | `radius?=40` | 9 |
| `find` | `category` (`ore`,`tree`,`chest`,`station`,`npc`,`item`), `name?`, `radius?=60` | 9 |
| `get_inventory` | – | 9 |
| `get_task` / `cancel_task` | – | 10 |
| `move_to` | `x,y` or `entity_id`, `allow_dig?=true`, `allow_build?=true` | 10 |
| `explore` | `direction` (`left`,`right`,`down`,`up`), `distance?=50` | 10 |
| `mine` / `mine_area` / `chop_tree` / `pick_up_items` | `x,y` / `x1,y1,x2,y2` / `x?,y?` / `radius?=10` | 11 |
| `select_hotbar` / `equip` / `use_item` / `place` / `craft` / `list_recipes` | `slot` / `item` / `item\|slot, x?, y?` / `item,x,y` / `item,count?=1` / `craftable_only?=true` | 12 |
| `attack` / `set_reflexes` / `wait` | `target` (`entity_id` or `"nearest"`) / `auto_defend?, quick_heal_threshold?, pause_when_idle?` / `seconds` | 13 |
| `talk_to_npc` | `entity_id` | 14 |

`item` params match by display name (case-insensitive), then by internal name.

---

### Task 1: Repo, Core library and grid model

**Files:**
- Create: `.gitignore`, `mod/Core/LlmPlayer.Core.csproj`, `mod/Core/Grid/{TilePoint,TileInfo,ITileGrid,ArrayTileGrid}.cs`
- Create: `mod/Core.Tests/LlmPlayer.Core.Tests.csproj`, `mod/Core.Tests/ArrayTileGridTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct TilePoint(int X, int Y)` with `operator +`.
  - `enum TileKind { Air, Solid, Platform, Water, Lava, Honey, Hazard, Unknown }`
  - `readonly record struct TileInfo(TileKind Kind, int PickPowerRequired = 0, bool Protected = false, char Glyph = '\0', string? Name = null)`. `PickPowerRequired == int.MaxValue` means unbreakable. Properties: `bool IsSolid`, `bool IsStandable` (Solid or Platform), `bool BlocksBody` (Solid only).
  - `interface ITileGrid { TileInfo Get(int x, int y); }`. Out-of-bounds returns `Solid` with `int.MaxValue` pick power.
  - `sealed class ArrayTileGrid : ITileGrid` with `ArrayTileGrid(int originX, int originY, TileInfo[,] tiles)`, `void Set(int x, int y, TileInfo t)`, and `static (ArrayTileGrid Grid, Dictionary<char, TilePoint> Markers) Parse(params string[] rows)` (origin 0,0). Parse glyphs: `#` Solid pick 0, `X` Solid unbreakable, `P` Solid protected, `=` Platform, `.` Air, `~` Water, `L` Lava, `^` Hazard, `?` Unknown. Any letter in `SGABCD` is Air and recorded as a marker.

- [ ] **Step 1: Install the .NET 8 SDK in WSL (user-local) and init git**

Run: `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 && export PATH="$HOME/.dotnet:$PATH" && dotnet --version && git init`
Expected: prints `8.0.x`, then "Initialized empty Git repository". Add `export PATH="$HOME/.dotnet:$PATH"` to `~/.bashrc`.

- [ ] **Step 2: Create both projects** with `dotnet new classlib` / `dotnet new xunit` (net8.0, nullable enabled), the test project referencing Core. `.gitignore` covers `bin/ obj/ node_modules/ dist/`.

- [ ] **Step 3: Write failing tests** in `ArrayTileGridTests.cs`:

```csharp
[Fact] public void Parse_ReadsGlyphsAndMarkers() {
    var (g, m) = ArrayTileGrid.Parse("S.=", "#X~");
    Assert.Equal(new TilePoint(0, 0), m['S']);
    Assert.Equal(TileKind.Air, g.Get(0, 0).Kind);
    Assert.Equal(TileKind.Platform, g.Get(2, 0).Kind);
    Assert.Equal(int.MaxValue, g.Get(1, 1).PickPowerRequired);
    Assert.Equal(TileKind.Water, g.Get(2, 1).Kind);
}
[Fact] public void OutOfBounds_IsUnbreakableSolid() {
    var (g, _) = ArrayTileGrid.Parse("..");
    Assert.True(g.Get(-1, 0).IsSolid);
    Assert.Equal(int.MaxValue, g.Get(5, 5).PickPowerRequired);
}
```

- [ ] **Step 4: Run** `dotnet test mod/Core.Tests`. Expected: build FAIL (types missing).
- [ ] **Step 5: Implement the grid types** listed in Interfaces.
- [ ] **Step 6: Run** `dotnet test mod/Core.Tests`. Expected: 2 passed.
- [ ] **Step 7: Commit** `git add -A && git commit -m "feat(core): tile grid model"`

### Task 2: Pathfinder — walking, jumping, falling, platforms

**Files:**
- Create: `mod/Core/Nav/MovementProfile.cs`, `mod/Core/Nav/PathTypes.cs`, `mod/Core/Nav/Pathfinder.cs`
- Test: `mod/Core.Tests/PathfinderBasicTests.cs`

**Interfaces:**
- Consumes: Task 1 grid types.
- Produces:
  - `sealed record MovementProfile(int MaxJumpUp = 5, int MaxJumpGap = 6, int SafeFallTiles = 20, bool NoFallDamage = false, int PickPower = 0, int BuildItemCount = 0)`. These defaults are conservative vanilla values. The mod builds the real profile from player stats in Task 10.
  - `enum MoveKind { Walk, StepUp, Jump, Fall, DropThrough, Swim, Dig, BuildPlatform }`
  - `readonly record struct PathStep(TilePoint Pos, MoveKind Kind, TilePoint? WorkTile = null)`. `WorkTile` is the tile to dig or place for Dig/BuildPlatform.
  - `sealed record PathOptions(bool AllowDig = true, bool AllowBuild = true, int MaxNodes = 20000, int GoalTolerance = 1)`
  - `sealed record PathResult(bool Success, IReadOnlyList<PathStep> Steps, string? FailureReason, int NodesExpanded)`
  - `static class Pathfinder { PathResult FindPath(ITileGrid grid, TilePoint start, TilePoint goal, MovementProfile profile, PathOptions options); bool CanStand(ITileGrid grid, TilePoint p); }`

- [ ] **Step 1: Write failing tests** (every grid needs ≥3 air rows above the floor for the 2×3 body):

```csharp
[Fact] public void FlatGround_WalksToGoal() {
    var (g, m) = ArrayTileGrid.Parse(
        "..........", "..........", "S......G..", "##########");
    var r = Pathfinder.FindPath(g, m['S'], m['G'], new(), new());
    Assert.True(r.Success);
    Assert.Equal(m['G'], r.Steps[^1].Pos);
    Assert.All(r.Steps, s => Assert.Equal(MoveKind.Walk, s.Kind));
}
```
Also, by name and assertion:
- `OneTileLedge_StepsUp`: there is a 1-high step between S and G. Assert the path contains `StepUp` and no `Jump`.
- `FiveTileWall_JumpsOver`: assert it contains `Jump` and succeeds with `MaxJumpUp=5`.
- `SixTileWall_NoDig_Fails`: `AllowDig=false, AllowBuild=false`. Assert `!Success` and that `FailureReason` contains `"unreachable"`.
- `GapOfSix_JumpsAcross` and `GapOfSeven_FallsInIfPitShallow`: the pit is 2 deep with a floor, so the path walks down and back up via StepUp/Jump.
- `LongFall_Avoided`: one route drops 30 tiles and a walking route exists. Assert no single `Fall` step descends more than `SafeFallTiles`. Also `NoFallDamage_AllowsLongFall`.
- `PlatformAbove_JumpsThrough` and `PlatformBelow_DropsThrough`: assert `DropThrough` appears.

- [ ] **Step 2: Run** `dotnet test mod/Core.Tests --filter PathfinderBasicTests`. Expected: build FAIL.
- [ ] **Step 3: Implement `Pathfinder.FindPath`** as A* over standing positions (`CanStand`: body tiles not `BlocksBody`, at least one support tile `IsStandable`). Neighbours: Walk ±1; StepUp (+1 up, ±1 x); Jump to any standable cell within `dy∈[-MaxJumpUp,0]`, `|dx|≤MaxJumpGap`, if a jump arc is clear; Fall to the first standable cell below a cell beside the edge; DropThrough from a platform support. Use Manhattan distance for the heuristic and these costs: Walk 1, StepUp 1.5, Jump 2+|dx|·0.5, Fall 1+dy·0.2, DropThrough 1.5.

  Arc clearance is an algorithm the tests don't determine, so use this: for a jump from s to t, the body must be clear on every cell of the "up then across then down" box path. Go up from s to `min(s.Y,t.Y)-1`, across at that height to t.X, then down to t. Conservative is fine.

  Accept the goal when the Chebyshev distance to it is ≤ `GoalTolerance`. Stop with `FailureReason = "unreachable: searched N nodes"` when the frontier is empty or `MaxNodes` is hit.

- [ ] **Step 4: Run** the filter again. Expected: all pass.
- [ ] **Step 5: Commit** `git commit -am "feat(core): A* pathfinder basic movement"` (after `git add mod/Core`).

### Task 3: Pathfinder — digging, building, liquids, hazards, unknown, budget

**Files:**
- Modify: `mod/Core/Nav/Pathfinder.cs`
- Test: `mod/Core.Tests/PathfinderAdvancedTests.cs`

**Interfaces:** Same signatures as Task 2. New behaviour only.

- [ ] **Step 1: Write failing tests**, by name and assertion:
  - `WallTooHigh_DigsThrough`: `#` wall of 8 between S and G, `PickPower=35`. Assert Success and that the path has `Dig` steps whose `WorkTile` values are `#` tiles.
  - `UnbreakableWall_NotDug`: same layout with `X`. Assert `!Success`.
  - `PickTooWeak_NotDug`: tile with `PickPowerRequired=65`, `PickPower=35`. Assert `!Success`.
  - `ProtectedTile_NeverDug`: the only route is through `P`. Assert `!Success` and no step's `WorkTile` is the `P` tile.
  - `DigCostsMoreThanWalkingAround`: there is a short wall and a 10-tile detour. Assert no `Dig` steps.
  - `ShaftOutOfReach_BuildsPlatform`: S is at the bottom of a 10-deep 2-wide shaft, `PickPower=0`, `BuildItemCount=20`. Assert `BuildPlatform` steps exist and their count ≤ 20.
  - `NoBuildItems_NoBuildSteps`: same layout, `BuildItemCount=0`. Assert `!Success`.
  - `LavaAvoided`: the shorter route crosses `L`. Assert no step's body overlaps lava.
  - `HazardAvoided`: same with `^`.
  - `WaterSwims`: G is reachable only through `~`. Assert a `Swim` step exists.
  - `UnknownTreatedAsPassableAtExtraCost`: the route passes through `?` tiles. Assert Success.
  - `GoalInsideSolid_UsesNearestStandable`: G is inside `#`. Assert Success and that the final step is within 2 tiles of G.
  - `Unreachable_FailsWithinNodeBudget`: a 200×200 open grid with G sealed in a box of `X`, `MaxNodes=5000`. Assert `!Success` and `NodesExpanded <= 5000`.

- [ ] **Step 2: Run** `dotnet test mod/Core.Tests --filter PathfinderAdvancedTests`. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - Dig edges: entering a cell whose body overlaps diggable solids. A solid is diggable if not `Protected` and `PickPowerRequired <= PickPower`. Cost is 3 per tile dug, with one `Dig` step per tile followed by the move.
  - BuildPlatform edges (when `AllowBuild && BuildItemCount > 0`): place a platform at a support position, either 1 above the current support when climbing a shaft or the next gap cell when bridging. Cost 4. Track placements used per path, carried in the node state as `buildsUsed` with a cap.
  - Swim: cells whose body is in Water/Honey are traversable in any direction, cost 2.
  - Lava/Hazard: never enter.
  - Unknown: treat as Air, cost +2.
  - Goal inside solid: before searching, replace the goal with the nearest `CanStand` cell within radius 3 (BFS). Fail with `"goal_not_standable"` if there is none.
- [ ] **Step 4: Run** the full suite `dotnet test mod/Core.Tests`. Expected: all pass, including Task 2.
- [ ] **Step 5: Commit** `git add mod/Core mod/Core.Tests && git commit -m "feat(core): pathfinder dig, build, liquids, budget"`

### Task 4: Path follower and map renderer

**Files:**
- Create: `mod/Core/Nav/PathFollower.cs`, `mod/Core/Map/MapRenderer.cs`
- Test: `mod/Core.Tests/PathFollowerTests.cs`, `mod/Core.Tests/MapRendererTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3.
- Produces:
  - `readonly record struct PlayerKinematics(float X, float Y, float VelX, float VelY, bool OnGround)`. X/Y are the feet-left position in tiles (float).
  - `record struct ControlIntent(bool Left, bool Right, bool Jump, bool Down, bool UseItem, TilePoint? CursorTile)`
  - `enum FollowerStatus { Running, Arrived, Stuck, NeedsReplan }`
  - `readonly record struct FollowerOutput(ControlIntent Intent, FollowerStatus Status, PathStep? Current)`
  - `sealed class PathFollower { PathFollower(IReadOnlyList<PathStep> steps, int stuckTicks = 90); FollowerOutput Tick(PlayerKinematics k, ITileGrid grid); int RemainingSteps { get; } }`
  - `static class MapRenderer { const int MaxRadius = 40; string Render(ITileGrid grid, TilePoint center, int radius, TilePoint player, IReadOnlyDictionary<TilePoint, char>? markers = null); }`

- [ ] **Step 1: Write failing follower tests:**
  - `WalkRight_HoldsRight`: Walk path to x+5, kinematics at start on ground. Assert `Intent.Right && !Intent.Left`.
  - `ReachesWaypoint_Advances`: kinematics at step 0's position. Assert `RemainingSteps` decreases.
  - `JumpStep_PressesJumpWhenOnGround`
  - `DigStep_AimsAtWorkTileAndUsesItem`: tile still solid. Assert `UseItem` and `CursorTile == WorkTile`. Once the tile is air in the grid, the step completes.
  - `BuildStep_AimsAtWorkTileAndUsesItem`
  - `DropThrough_PressesDown`
  - `NoProgress90Ticks_ReportsStuck`
  - `LastStepReached_ReportsArrived`
  - `PlannedTileChanged_ReportsNeedsReplan`: a Walk step's support became Air.
- [ ] **Step 2: Write failing renderer tests:**
  - `RendersGlyphsAndPlayer`: a small grid. Assert the output contains `@` at the player cell and a header line `origin: x=<cx-r> y=<cy-r>`.
  - `UnknownShownAsQuestionMark`
  - `MarkersOverrideTiles`: the markers dict puts `E` at a cell.
  - `LegendListsOnlyGlyphsPresent`: the output ends with `legend:` lines naming each glyph used, e.g. `T tree`, `c Copper Ore`, using `TileInfo.Glyph/Name` for named tiles.
  - `RadiusClampedTo40`: `radius=500`. Assert the number of grid rows is 81.
- [ ] **Step 3: Run** `dotnet test mod/Core.Tests`. Expected: FAIL.
- [ ] **Step 4: Implement both.** Default glyphs: `.` air, `#` solid, `=` platform, `~` water, `L` lava, `h` honey, `^` hazard, `?` unknown, `@` player (overrides). A `TileInfo.Glyph` takes precedence over kind defaults. The follower steers toward `Current.Pos.X` with a deadzone of 0.3 tiles. It advances when the feet are within 0.5 tiles of the waypoint and on the ground (or swimming).
- [ ] **Step 5: Run** the tests. Expected: all pass.
- [ ] **Step 6: Commit** `git add mod && git commit -m "feat(core): path follower and ascii map renderer"`

### Task 5: Task runner and input watcher

**Files:**
- Create: `mod/Core/Tasks/TaskRunner.cs`, `mod/Core/Control/InputWatcher.cs`
- Test: `mod/Core.Tests/TaskRunnerTests.cs`, `mod/Core.Tests/InputWatcherTests.cs`

**Interfaces:**
- Produces:
  - `enum GameTaskStatus { Running, Succeeded, Failed, Cancelled }`
  - `sealed record TaskReport(string Name, GameTaskStatus Status, string? Reason, string? Progress)`
  - `interface IGameTask<TCtx> { string Name { get; } GameTaskStatus Tick(TCtx ctx); string? Reason { get; } string? Progress { get; } void OnCancel(TCtx ctx); }`
  - `sealed class TaskRunner<TCtx> { void Start(IGameTask<TCtx> task, TCtx ctx); void Tick(TCtx ctx); void Cancel(string reason, TCtx ctx); TaskReport? Current { get; } TaskReport? LastFinished { get; } bool IsIdle { get; } event Action<TaskReport>? Finished; }`. Starting a task while one runs cancels the old one with reason `"replaced"`.
  - `readonly record struct RawInputSnapshot(IReadOnlySet<int> KeysDown, bool LeftButton, bool RightButton, int MouseX, int MouseY, int ScrollValue)`. This is raw device state, never game-adjusted mouse values.
  - `static class InputWatcher { bool IsUserInput(RawInputSnapshot prev, RawInputSnapshot cur, int toggleKey, int mouseMoveThreshold = 8); }`

- [ ] **Step 1: Write failing tests:**
  - `Start_ThenSucceeds_RaisesFinishedOnce`
  - `StartWhileRunning_CancelsOldWithReplaced`
  - `CancelAll_ReportsReason`: `Cancel("player_died")`. Assert `LastFinished.Status == Cancelled`, `Reason == "player_died"`, and `OnCancel` called.
  - `TaskThrows_FailsWithInternalReason`: `Tick` throws, so the runner records `Failed` with the message. It doesn't propagate.
  - `NewKeyPressed_IsUserInput`
  - `ToggleKeyIgnored`
  - `MouseMovedBeyondThreshold_IsUserInput`
  - `MouseJitterBelowThreshold_Ignored`
  - `CursorOverrideIsNotUserInput`: identical raw snapshots. Assert false. Document in the test comment that the mod's cursor override never feeds the snapshot.
  - `HeldKeyFromBeforeHandoff_NotUserInput`: the key is in both prev and cur.
- [ ] **Step 2: Run** `dotnet test mod/Core.Tests`. Expected: FAIL.
- [ ] **Step 3: Implement** both types.
- [ ] **Step 4: Run** the tests. Expected: all pass.
- [ ] **Step 5: Commit** `git add mod && git commit -m "feat(core): task runner and user input watcher"`

### Task 6: Mod skeleton, build scripts, TCP bridge

**Prerequisite (user):** tModLoader is installed and has been launched once (this creates `ModSources` and `ModSources/tModLoader.targets`). The .NET 8 SDK is installed on Windows (`winget install Microsoft.DotNet.SDK.8`).

**Files:**
- Create: `scripts/sync-mod.sh`, `scripts/build-mod.sh`
- Create: `mod/LlmPlayer/{build.txt,description.txt,LlmPlayer.csproj,LlmPlayer.cs}`
- Create: `mod/LlmPlayer/Config/LlmConfig.cs`
- Create: `mod/LlmPlayer/Bridge/{Protocol,JsonLineServer,CommandRouter,BridgeSystem}.cs`

**Interfaces:**
- Consumes: Core (copied in by the sync script under `ModSources/LlmPlayer/Core/`, excluding csproj/bin/obj).
- Produces:
  - `LlmConfig : ModConfig` (ClientSide): `int Port = 7878`, `string BindAddress = "0.0.0.0"`, `int ProtectSpawnRadius = 20`, `int BackupKeep = 5`, `bool AutoDefend = true`, `float QuickHealThreshold = 0.35f`, `bool PauseWhenIdle = false`, `bool ShowPathOverlay = true`.
  - `sealed class CommandError(string Code, string Message) : Exception`
  - `static class CommandRouter { void Register(string method, Func<JsonElement, object?> handler, bool isAction); object? Dispatch(string method, JsonElement @params); }`. Unknown methods → `unknown_method`. Actions are gated by `ControlState` (wired in Task 7, a no-op for now). Outside a world (`Main.gameMenu`) → `not_in_world`.
  - `JsonLineServer { Start(string bind, int port); Stop(); ConcurrentQueue<(int Id, string Method, JsonElement Params)> Inbound; void Send(object message); }`. This is a background thread with a single client; a new client replaces the old.
  - `BridgeSystem : ModSystem`: `OnWorldLoad`/`OnModLoad` start the server, `Unload` stops it. `PostUpdateEverything` drains Inbound → Dispatch → Send response, then calls `BridgeSystem.Tick` hooks. It exposes `static void Emit(string evt, object payload)`.
  - Commands now: `ping` → `{pong:true, version}`. `get_state` (minimal) → `{x, y, life, lifeMax, inWorld}`.

- [ ] **Step 1: Write `scripts/sync-mod.sh`.** `TML_MODSOURCES` defaults to `<Windows Documents>/My Games/Terraria/tModLoader/ModSources`. If it's missing, exit 1 with "ModSources not found — launch tModLoader once or set TML_MODSOURCES". Rsync `mod/LlmPlayer/` and `mod/Core/` (excluding `*.csproj bin obj`) into `$TML_MODSOURCES/LlmPlayer/`.
- [ ] **Step 2: Write `scripts/build-mod.sh`.** Run sync, then `cmd.exe /c "cd /d <wslpath -w dest> && dotnet build -c Release"`, and exit non-zero unless the output contains `Build succeeded`.
- [ ] **Step 3: Implement the mod files** listed above. Use `System.Text.Json`, with responses serialized camelCase. The csproj imports `..\tModLoader.targets` (the standard template).
- [ ] **Step 4: Build** with `scripts/build-mod.sh`. Expected: `Build succeeded`, and `LlmPlayer.tmod` appears in `Documents/My Games/Terraria/tModLoader/Mods`.
- [ ] **Step 5: Manual check (user runs the game).** Enable the mod, enter a world, allow the Windows Firewall prompt. From WSL run `printf '{"id":1,"method":"ping","params":{}}\n{"id":2,"method":"get_state","params":{}}\n' | nc -q1 $(ip route | awk '/default/{print $3}') 7878`. Expected: two response lines, the second with numeric `x`, `y`. From the main menu, `get_state` returns error `not_in_world`.
- [ ] **Step 6: Commit** `git add scripts mod/LlmPlayer && git commit -m "feat(mod): skeleton, build scripts, TCP JSON bridge"`

### Task 7: Control handoff, indicator, save backup

**Files:**
- Create: `mod/LlmPlayer/Control/{ControlState,AiModPlayer,ControlIndicator}.cs`, `mod/LlmPlayer/Safety/SaveBackup.cs`, `mod/LlmPlayer/Commands/ControlCommands.cs`
- Modify: `mod/LlmPlayer/Bridge/CommandRouter.cs` (action gating), `mod/LlmPlayer/LlmPlayer.cs` (keybind)

**Interfaces:**
- Consumes: `InputWatcher`, `RawInputSnapshot` (Task 5); `CommandRouter`, `BridgeSystem.Emit` (Task 6).
- Produces:
  - `enum Driver { User, Ai }`
  - `static class ControlState { Driver Driver; void SetDriver(Driver d, string reason); ControlIntent Pending; event Action<Driver,string> Changed; }`. `SetDriver(User, …)` cancels the active task (Task 10's runner hooks `Changed`) and emits `control_changed {driver, reason}`.
  - `AiModPlayer : ModPlayer`. In `SetControls`, while `Driver == Ai`, apply `ControlState.Pending` to `Player.controlLeft/Right/Jump/Down/UseItem` and aim at `Pending.CursorTile` (the builder picks the cursor override mechanism, per the spec). Each `PostUpdate`, it builds a `RawInputSnapshot` from `Keyboard.GetState()` / `Mouse.GetState()` (raw XNA, never `Main.mouseX`). If `InputWatcher.IsUserInput` returns true, it calls `SetDriver(User, "user_input")`. On death it calls `SetDriver(User, "player_died")`.
  - Keybind `"Toggle AI control"`, default `F8`, toggles the driver. When switching to Ai, call `SaveBackup.EnsureForSession()` first.
  - `SaveBackup.EnsureForSession()`: once per world session, copy the on-disk `.plr` and `.wld` files of `Main.ActivePlayerFileData` / `Main.ActiveWorldFileData` to `.../tModLoader/LlmPlayerBackups/<yyyyMMdd-HHmmss>/`, keeping the newest `BackupKeep`. **Deviation noted:** it copies the last saved files rather than forcing a save mid-game. That's safer for save integrity, and the backup is the state before the AI ever acted.
  - `ControlIndicator : ModSystem`. In `PostDrawInterface`, draw "AI driving: <task name or idle> (F8 to take back)" near the top-center when Ai.
  - Commands: `get_control` → `{driver}`. `request_control` → `{driver, granted: driver=="ai", hint: "Press F8 in game to hand control to the AI"}`. It never changes the driver.
  - Action gating: `CommandRouter` throws `user_has_control` for `isAction` methods when `Driver == User`.

- [ ] **Step 1: Implement** the files above.
- [ ] **Step 2: Build** with `scripts/build-mod.sh`. Expected: `Build succeeded`.
- [ ] **Step 3: Manual checklist (user):**
  - F8 shows the indicator, and `get_control` → `ai`. A backup folder appears with `.plr` and `.wld` files.
  - Pressing any movement key or clicking hides the indicator, and `get_control` → `user`. An `nc` client sees a `control_changed` event with reason `user_input`.
  - Holding A while pressing F8 does not immediately revoke control.
  - `request_control` while user-driven → `granted:false`, and the driver doesn't change.
  - Dying while AI-driven → driver `user`, reason `player_died`.
- [ ] **Step 4: Commit** `git add mod/LlmPlayer && git commit -m "feat(mod): control handoff, indicator, save backup"`

### Task 8: MCP server

**Files:**
- Create: `mcp-server/{package.json,tsconfig.json,vitest.config.ts}`, `mcp-server/src/{index,host,connection,tools}.ts`
- Test: `mcp-server/test/{host,connection,tools}.test.ts`

**Interfaces:**
- Consumes: the wire protocol table above.
- Produces:
  - `resolveHost(env: NodeJS.ProcessEnv, routeTable: string): string` in `host.ts`. Uses `TERRARIA_HOST` if set, else parses the default gateway from `/proc/net/route` text (little-endian hex), else `"127.0.0.1"`.
  - `class ModConnection` in `connection.ts`. Constructor `(host: string, port: number)`. `call(method: string, params?: object, timeoutMs = 10000): Promise<unknown>` connects lazily, rejects with `ModError(code, message)` on error responses, and on connection failure rejects with `ModError("not_connected", "Terraria not reachable at <host>:<port> — is tModLoader running with LlmPlayer enabled and a world loaded?")`. `drainEvents(): ModEvent[]` returns buffered events. It reconnects on the next call after a drop.
  - `registerTools(server: McpServer, conn: ModConnection): void` in `tools.ts`. Registers one MCP tool per protocol method (excluding `ping`) with zod schemas matching the params table, plus `wait_for_task({timeout_s?: number = 30})`. That tool polls `get_task` every 250 ms until status ≠ `running` or the timeout, and returns the final report. Every tool result is JSON text and appends drained events under `events` when any exist. A `ModError` becomes `{isError: true, content: [{type:"text", text:"<code>: <message>"}]}`.
  - Tool descriptions must tell the LLM the coordinate convention, that actions start tasks (use `wait_for_task`/`get_task`), and that `user_has_control` means "ask the user to press F8".
  - `index.ts`: stdio transport, server name `terraria`.

- [ ] **Step 1: Scaffold** with `npm init -y`, `npm i @modelcontextprotocol/sdk zod`, `npm i -D typescript vitest @types/node tsx`. Scripts: `build: tsc`, `test: vitest run`, `start: node dist/index.js`. ESM, `"type": "module"`.
- [ ] **Step 2: Write failing tests:**
  - host: "uses TERRARIA_HOST when set"; "parses gateway 0140A8C0 as 192.168.64.1"; "falls back to 127.0.0.1 on empty table".
  - connection, each test against a `net.createServer` fake on port 0: "sends request line and resolves result"; "rejects ModError on error response"; "buffers events and drainEvents returns them once"; "rejects with readable error when nothing listens" (message contains host:port); "reconnects after server restart"; "times out with code timeout".
  - tools, with a fake `ModConnection` that records calls: "move_to forwards x,y,allow_dig"; "user_has_control becomes isError with F8 hint text"; "wait_for_task polls until succeeded"; "wait_for_task returns running report on timeout"; "events are appended to results".
- [ ] **Step 3: Run** `cd mcp-server && npm test`. Expected: FAIL.
- [ ] **Step 4: Implement** `host.ts`, `connection.ts`, `tools.ts`, `index.ts`.
- [ ] **Step 5: Run** `npm test && npm run build`. Expected: all pass, `dist/index.js` exists.
- [ ] **Step 6: Register with Claude Code and smoke test (user in a world):** `claude mcp add terraria -- node /path/to/terraria-mcp/mcp-server/dist/index.js`. In a new session, calling `get_state` returns position. `move_to` while user-driven returns the F8 hint. (`move_to` returns `unknown_method` until Task 10. That's acceptable here.)
- [ ] **Step 7: Commit** `git add mcp-server && git commit -m "feat(mcp): stdio MCP server bridging to mod"`

### Task 9: Observation — visibility, protection, state, map, entities, find, inventory

**Files:**
- Create: `mod/LlmPlayer/Observe/{Visibility,GameTileGrid,StateReader,EntityReader,Finder,InventoryReader,ItemLookup}.cs`, `mod/LlmPlayer/Safety/Protection.cs`, `mod/LlmPlayer/Commands/ObserveCommands.cs`

**Interfaces:**
- Consumes: `ITileGrid`, `TileInfo`, `MapRenderer` (Core); `CommandRouter` (Task 6).
- Produces:
  - `static class Visibility { bool IsTileVisible(int x, int y); bool IsEntityVisible(Entity e); }`. A tile is visible if `Main.Map.IsRevealed(x,y)`, OR (it's within the screen rect + 4 tiles AND `Lighting.Brightness(x,y) > 0.05f`), OR (Spelunker: `player.findTreasure && Main.tileSpelunker[type]` and on screen), OR (Dangersense: `player.dangerSense` and the tile is dangerous and on screen). An entity is visible if it's within the screen rect + 4 tiles AND (lit at its center OR `player.detectCreature`).
  - `static class Protection { bool IsProtected(int x, int y); void Refresh(); }`. Containers (`Main.tileContainer`), `Main.tileFrameImportant` multi-tile objects (furniture, stations, doors), tiles within `ProtectSpawnRadius` of `(Main.spawnTileX, Main.spawnTileY)` and of the player's bed spawn if set, and tiles inside the rooms of housed town NPCs. Rooms are computed via `WorldGen.StartRoomCheck(homeTileX, homeTileY-1)` bounds and cached; `Refresh()` runs every 600 ticks.
  - `sealed class GameTileGrid : ITileGrid`. Maps `Main.tile`, with not-visible tiles → `Unknown`. It sets `PickPowerRequired` from a vanilla minimum-pick table (builder verifies against `Player.GetPickaxeDamage`) and `Protected` from `Protection`. Named glyphs: trees `T`, ores lowercase letters assigned per ore type with `Name` = the localized tile name, chests `C`, stations `W`.
  - `get_state` (full) → `{x, y, life, lifeMax, mana, manaMax, defense, buffs:[name], heldItem, timeOfDay:"day 6:30", biome, layer:"surface|underground|cavern|underworld|space", spawn:{x,y}, driver, task, recentEvents:[...]}`
  - `get_map {radius}` → `{map: string}` via `MapRenderer.Render(new GameTileGrid(), player, radius, player, markers)`. Markers: visible hostile NPCs `E`, town NPCs `N`, dropped items `i`.
  - `get_entities {radius}` → `{entities:[{id, name, kind:"hostile|town|critter|item", x, y, life?, lifeMax?, distance}]}`, visible only, sorted by distance, **capped at 30**.
  - `find {category, name?, radius}` → `{results:[{name, x, y, distance}]}`, at most 10, visible tiles only.
  - `get_inventory` → `{hotbar:[{slot, name, stack}], inventory:[...], armor:[...], accessories:[...], ammo:[...], coins:{copper,silver,gold,platinum}, selectedSlot}`. Empty slots are omitted.
  - `ItemLookup.FindSlot(Player p, string itemOrSlot) -> int`. Matches display name case-insensitively, then the internal name. Throws `missing_item` if not found.

- [ ] **Step 1: Implement** the files above.
- [ ] **Step 2: Build** with `scripts/build-mod.sh`. Expected: `Build succeeded`.
- [ ] **Step 3: Manual checklist via Claude Code (user in a world):**
  - In a dark unexplored cave, `get_map` shows `?` beyond the lit area. After drinking a Spelunker potion, ores inside the screen appear.
  - `find ore` doesn't return ores in unexplored rock far off-screen.
  - `get_entities` excludes an enemy in a pitch-dark area unless a Hunter potion is active.
  - `get_map radius=500` returns 81 rows.
  - `get_inventory` matches the in-game inventory.
- [ ] **Step 4: Commit** `git add mod/LlmPlayer && git commit -m "feat(mod): fair-vision observation and protection"`

### Task 10: Movement tasks — move_to, explore, task commands, path overlay

**Files:**
- Create: `mod/LlmPlayer/Tasks/{MoveToTask,ExploreTask}.cs`, `mod/LlmPlayer/Tasks/TaskHost.cs`, `mod/LlmPlayer/Commands/TaskCommands.cs`, `mod/LlmPlayer/Nav/ProfileBuilder.cs`, `mod/LlmPlayer/Nav/PathOverlay.cs`

**Interfaces:**
- Consumes: `Pathfinder`, `PathFollower`, `TaskRunner<TCtx>` (Core); `GameTileGrid`; `ControlState.Pending`; `BridgeSystem` tick and `Emit`.
- Produces:
  - `sealed class GameCtx { Player Player; GameTileGrid Grid; }`
  - `static class TaskHost { TaskRunner<GameCtx> Runner; void Start(IGameTask<GameCtx> t); }`. It ticks each frame only while `Driver == Ai` and resets `ControlState.Pending` to default when idle. It subscribes to `ControlState.Changed` (→ `Cancel(reason)`) and to world unload (→ `Cancel("left_world")`). `Runner.Finished` → `Emit("task_finished", report)`.
  - `ProfileBuilder.From(Player p) -> MovementProfile`. Derives `MaxJumpUp`/`MaxJumpGap` from `Player.jumpHeight`, `Player.jumpSpeed` and `maxRunSpeed` (the builder verifies the formula; never above observed reality), `SafeFallTiles` from `25 + extraFall - 5` margin, `NoFallDamage` from `noFallDmg`, `PickPower` from the best pickaxe in inventory, and `BuildItemCount` from platforms + dirt/stone/wood blocks.
  - `MoveToTask(TilePoint goal | int entityId, bool allowDig, bool allowBuild)`:
    - It plans with `Pathfinder` and follows with `PathFollower`, writing `ControlState.Pending`.
    - Dig steps select the best pickaxe. Build steps select a platform or the cheapest block.
    - It replans on `Stuck`/`NeedsReplan`, every 120 ticks when the path crosses `Unknown` tiles, and every 60 ticks for entity goals. It fails after 5 consecutive failed replans with the last `FailureReason`.
    - `Progress` = `"N tiles remaining"`.
  - `ExploreTask(direction, distance)`: repeatedly `move_to` toward the farthest standable visible tile in that direction until `distance` is covered or 3 consecutive replans make no progress.
  - Commands (actions): `move_to`, `explore`, `cancel_task`. Observation: `get_task` → `{current, lastFinished}`.
  - `PathOverlay` draws the current path as small squares when `ShowPathOverlay`.

- [ ] **Step 1: Implement** the files above.
- [ ] **Step 2: Build** with `scripts/build-mod.sh`. Expected: `Build succeeded`.
- [ ] **Step 3: Manual checklist via Claude Code (AI driving):**
  - `move_to` 40 tiles across hilly surface terrain → `wait_for_task` reports `succeeded`, and the overlay showed the path.
  - `move_to` a target on top of a 5-high cliff jumps it. A 10-high cliff digs through, or builds platforms when `allow_dig=false`.
  - `move_to` the bottom of a small cave → it drops through and descends without fall damage.
  - `move_to` inside a protected house wall → it fails with a reason and the house is intact.
  - Pressing a key mid-move → the task is `cancelled` with reason `user_input` and the player stops.
  - `explore right 100` moves ~100 tiles.
- [ ] **Step 4: Commit** `git add mod/LlmPlayer && git commit -m "feat(mod): move_to and explore tasks with smart pathfinding"`

### Task 11: Gathering — mine, mine_area, chop_tree, pick_up_items

**Files:**
- Create: `mod/LlmPlayer/Tasks/{MineTask,ChopTreeTask,PickUpTask}.cs`, `mod/LlmPlayer/Commands/GatherCommands.cs`

**Interfaces:**
- Consumes: `MoveToTask` (composed as a sub-task), `Protection`, `Visibility`, `ItemLookup`, `TaskHost`.
- Produces:
  - `MineTask(IReadOnlyList<TilePoint> tiles)`:
    - Before starting, it validates each tile. Not visible → `not_visible`, protected → `protected_tile`, pick too weak → error `missing_item` with the required pick power.
    - For each tile, if out of reach (`Player.tileRangeX/Y` + item `tileBoost`) it moves within reach first, then selects the best pickaxe and holds UseItem with the cursor on the tile until it's air.
    - `mine_area` orders tiles top-to-bottom so the player isn't buried, and caps at 200 tiles.
  - `ChopTreeTask(TilePoint? at)`: no coords → the nearest visible tree via `Finder`. It walks next to the trunk base, selects the best axe, and chops until the trunk is gone.
  - `PickUpTask(int radius)`: moves to each visible dropped item within radius, nearest first, until none remain or 20 s pass.
  - All three are actions. They return `{started:true, task:name}` immediately, or a `CommandError` for validation failures.

- [ ] **Step 1: Implement** the files above.
- [ ] **Step 2: Build** with `scripts/build-mod.sh`. Expected: `Build succeeded`.
- [ ] **Step 3: Manual checklist (AI driving):** `chop_tree` gives wood in inventory. `mine` on visible copper ore gives copper. `mine` on a chest → `protected_tile`. `mine` on an unexplored tile → `not_visible`. `mine_area` 3×3 leaves a hole and the player isn't stuck. `pick_up_items` collects drops.
- [ ] **Step 4: Commit** `git add mod/LlmPlayer && git commit -m "feat(mod): mining, chopping, pickup tasks"`

### Task 12: Inventory actions — select, equip, use, place, craft, recipes

**Files:**
- Create: `mod/LlmPlayer/Tasks/{PlaceTask,UseItemTask}.cs`, `mod/LlmPlayer/Commands/InventoryCommands.cs`, `mod/LlmPlayer/Commands/CraftCommands.cs`

**Interfaces:**
- Consumes: `ItemLookup`, `TaskHost`, `MoveToTask`, `Protection`.
- Produces:
  - `select_hotbar {slot 0-9}` (instant action) sets `Player.selectedItem`.
  - `equip {item}` (instant action) moves armor or an accessory into the right slot using the game's swap logic. Error `bad_params` if not equippable.
  - `use_item {item|slot, x?, y?}` (task): moves the item to the selected slot if needed, aims at x,y if given, and uses it once (potions, tools, summons). Fails if on cooldown.
  - `place {item, x, y}` (task): validates that the target is visible, empty and not protected. It moves within reach, selects the item, and uses it on the tile. It succeeds when the tile is occupied.
  - `list_recipes {craftable_only}` (observation): after `Recipe.FindRecipes()`, it returns `{recipes:[{result, stack, ingredients:[{name,stack}], stations:[name]}]}`, at most 50. With `craftable_only=false` it searches all recipes whose result matches an optional `name` param.
  - `craft {item, count}` (instant action): finds a recipe with that result. If it isn't in `Main.availableRecipe`, it returns `missing_station` (naming the required stations) or `missing_item` (naming the missing ingredients). Otherwise it crafts `count` times through tModLoader's recipe create path, so ingredients are consumed and `OnCraft` fires. Results go into the inventory (builder verifies the API; no item may be created without consuming ingredients). Returns `{crafted:n}`.

- [ ] **Step 1: Implement** the files above.
- [ ] **Step 2: Build** with `scripts/build-mod.sh`. Expected: `Build succeeded`.
- [ ] **Step 3: Manual checklist (AI driving):**
  - With 10 wood, `craft Work Bench` gives a Work Bench, and the wood drops by 10.
  - `place Work Bench` places it next to the player.
  - `craft Wooden Sword` away from a bench → `missing_station` naming the Work Bench. Next to the bench → it succeeds.
  - `use_item Lesser Healing Potion` heals.
  - `equip Wood Helmet` equips it.
- [ ] **Step 4: Commit** `git add mod/LlmPlayer && git commit -m "feat(mod): inventory, placing and crafting"`

### Task 13: Combat and reflexes — attack, set_reflexes, pause_when_idle, wait

**Files:**
- Create: `mod/LlmPlayer/Tasks/{AttackTask,WaitTask,Reflexes}.cs`, `mod/LlmPlayer/Commands/CombatCommands.cs`

**Interfaces:**
- Consumes: `TaskHost`, `MoveToTask`, `EntityReader`, `Visibility`, `ControlState`.
- Produces:
  - `AttackTask(int entityId | nearest)`: targets visible hostile NPCs only (never town NPCs or critters → `bad_params`). It picks the highest-damage weapon it has ammo or mana for. For melee it closes in with `MoveToTask` toward the entity and keeps within reach. For ranged or magic it keeps 8–15 tiles away. It aims at the target's center and holds UseItem. It succeeds when the target dies and fails if the target isn't visible for 5 s.
  - `Reflexes` (ticked by `TaskHost` while Ai):
    - Auto-defend, if `AutoDefend`: when idle or in a move task and a visible hostile is within weapon reach, it temporarily aims and attacks. The move task is paused, not cancelled.
    - Quick-heal: when `life < QuickHealThreshold·lifeMax` and the player has no Potion Sickness, it calls `Player.QuickHeal()`.
    - Each firing emits `reflex {kind}`. Damage taken emits `took_damage {amount, source}`.
  - Pause when idle: when `PauseWhenIdle && Driver==Ai && Runner.IsIdle && singleplayer`, it pauses the game. It unpauses immediately when a task starts or the driver becomes User (the builder picks the pause mechanism).
  - `set_reflexes {auto_defend?, quick_heal_threshold?, pause_when_idle?}` (action) updates the config in memory and returns current values.
  - `wait {seconds ≤ 60}` (task): does nothing (reflexes still run) for N seconds.

- [ ] **Step 1: Implement** the files above.
- [ ] **Step 2: Build** with `scripts/build-mod.sh`. Expected: `Build succeeded`.
- [ ] **Step 3: Manual checklist (AI driving, night):**
  - `attack nearest` kills a zombie.
  - With the AI idle, a slime approaching gets auto-attacked and a `reflex` event appears.
  - At low HP with a healing potion, it quick-heals.
  - `attack` on the Guide → `bad_params`.
  - With `pause_when_idle=true`, the world pauses while idle, resumes on `move_to`, and never pauses after pressing a key (user control).
- [ ] **Step 4: Commit** `git add mod/LlmPlayer && git commit -m "feat(mod): combat and reflexes"`

### Task 14: NPC talk, README, end-to-end acceptance

**Files:**
- Create: `mod/LlmPlayer/Commands/NpcCommands.cs`, `README.md`

**Interfaces:**
- Produces: `talk_to_npc {entity_id}` (action, task). It moves within talk range of a visible town NPC, opens chat, and returns `{name, chat: Main.npcChatText}`, plus `{shop:[{name, price}]}` if the NPC has a shop. Buying and selling are out of scope for v1.

- [ ] **Step 1: Implement `talk_to_npc`.** Build with `scripts/build-mod.sh`. Expected: `Build succeeded`. Manual: talking to the Guide returns chat text.
- [ ] **Step 2: Write `README.md`.** Cover: prerequisites (tModLoader, Windows .NET 8 SDK, WSL .NET for tests), `scripts/build-mod.sh`, enabling the mod, the firewall prompt, `claude mcp add terraria …`, the F8 handoff, where backups live, and the config options.
- [ ] **Step 3: Run the end-to-end acceptance run (spec "v1 done when").** Fresh character on a new world. The user presses F8, and Claude Code, using only MCP tools, chops trees → crafts a Work Bench and wooden tools → explores and mines copper/iron → crafts a pickaxe upgrade → `move_to` spawn → survives the first night with reflexes on. Also confirm that a key press takes control back at any moment, and that no protected tile was broken (houses and chests intact). Record the outcome and any failures in `docs/acceptance-2026-11-25.md`.
- [ ] **Step 4: Run** `dotnet test mod/Core.Tests && (cd mcp-server && npm test)`. Expected: all pass.
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat: npc talk, README, v1 acceptance notes"`
