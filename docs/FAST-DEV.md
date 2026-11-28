# Fast development: test instances at full speed

Goal: test the mod against the real Terraria engine as fast as possible, in **test instances only**. Exactly one
instance, instance 0 (the user's real world "The Weird Fjord", Steam launch, port 7878, normal UI), plays the game:
it stays fully fair and runs at normal speed. Everything on this page is dev tooling for instances 1, 2, ...
(`scripts/instance.sh`), which are throwaway copies and need no UI at all.

## Summary

| Idea | Verdict | What we have |
|---|---|---|
| Speed the game up | **Works** (not yet measured in game) | `dev_timescale {factor: 1..8 \| "max"}`: runs `Main.Update` several times per engine frame |
| Headless / no drawing | **Works with a minimal pass** | `dev_render {mode: "full" \| "minimal" \| "off"}`: off skips all drawing; the mod runs the camera and lighting itself |
| Real headless client | Not possible | tModLoader has no headless client; a dedicated server has no local player and no lighting |
| Journey "time speed" (`dayRate`) | Not useful | Only advances the clock and weather, not physics or AI |
| Test scenes without travel | **Works** | `dev_setup {...}`: teleport, items, life, time, clear/spawn NPCs, build terrain |
| Engine tests | **Works** | `scripts/engine-test.sh`: scenario files, parallel across instances, pass/fail |
| Defaults | **Fastest** | `instance.sh start N` launches with `-llmrender off -llmtimescale max`; `--watch` = full drawing at 1x |

All of these are refused with `not_test_instance` in the main game, even with `DevCommands` on (see [Gate](#the-gate)).

## The gate

`FastDev.Check` (mod/Core/Control/FastDev.cs, unit-tested) allows the test-only commands only when **all** hold:

1. `DevCommands` is on in the mod config (instance.sh turns it on in the instance's own config copy).
2. The command line has `-tmlsavedirectory <dir>` (instance.sh passes it; a Steam launch never does) and
   `Main.SavePath` is that folder.
3. That folder holds `LlmPlayerTestInstance.json` = `{"instance": N}` with N >= 1. Only `instance.sh` writes it, and
   only into `<instances>/N` (it refuses an instances root inside the main save folder).
4. The bridge is listening, and not on 7878.

Instance 0 fails 2, 3 and 4. With the defaults (time scale 1, render full) the `Main.Update` detour is a plain
pass-through, and nothing else can change them: instance 0 runs exactly as before.

## 1. Time scale

Terraria runs one world update per engine frame at a fixed 60 Hz. FNA's `Game.Tick` sleeps until
`TargetElapsedTime` (1/60 s) has passed, then calls `Update` (once, or several times to catch up, capped at 500 ms)
and `Draw` once, unless `SuppressDraw()` was called
([FNA Game.cs](https://github.com/FNA-XNA/FNA/blob/master/src/Game.cs), `Tick`).
tModLoader's `Main.Update` wraps `DoUpdate` (input, the world update with every ModPlayer/ModSystem hook) in a
try/catch and then runs main-thread actions
([Main.cs.patch](https://github.com/tModLoader/tModLoader/blob/1.4.4/patches/tModLoader/Terraria/Main.cs.patch),
hunk `@@ -13392`).

Options considered:

- **Shorter `TargetElapsedTime`** (1/(60k) s): FNA would call Update k times as often, but it also draws after every
  tick, so drawing would run k times as often too; pacing would then hang on the draw cost. Not used.
- **Detour `Main.Update`, call the original k times per frame** (used): every tick runs the whole vanilla update,
  so tasks, the path follower, reflexes, NPC AI and physics run exactly as at 60 Hz, only more ticks per wall
  second. All the mod's logic is tick-based (`Main.GameUpdateCount`, tick counters, `wait` uses 60 ticks per
  second), so it behaves the same per update. Wall-clock things don't scale: MCP `wait_for_task` timeouts, the
  autosave timer, call.mjs deadlines (all fine, things just finish sooner).
- **Journey mode time rate / `Main.dayRate`**: tModLoader replaced it with `desiredWorldEventsUpdateRate`, which only
  repeats `UpdateWeather` and the clock (`Main.cs.patch` hunk `@@ -13787`). Physics and AI stay at 60 Hz. Not useful.

How `FastDevSystem` (mod/LlmPlayer/Dev/FastDevSystem.cs) does it:

- `factor` 1..8: `TimeScalePacer` turns wall time into owed updates (k per 1/60 s), whatever rate the engine calls
  Update at, and drops the backlog beyond 4 frames instead of spiralling on a slow PC.
- `"max"`: updates until 15 ms of the frame are used (10 ms when the frame is drawn), at most 40 per frame.
- Vanilla's frame pacing (`Main.UpdateTimeAccumulator`, used with frame skip off) is topped up to 1/60 s before each
  call so it doesn't skip one. `dev_status.fastDev.extraUpdateEffect` is the share of extra calls that really
  advanced `GameUpdateCount` (should be 1.0; lower while the game is paused, e.g. PauseWhenIdle).
- Input: `PlayerInput.UpdateInput` runs every update; the AI sets its controls every update anyway, and nobody uses
  the test window's keyboard. Audio updates k times too (volume is 0 in test instances).

## 2. Rendering: headless as far as it goes

tModLoader has no headless client mode. What the AI depends on that lives in the draw path (`Main.DoDraw`):

- **The camera.** `Main.screenPosition` is set in `Main.DoDraw_UpdateCameraPosition`. Fair vision's screen rectangle
  (`Visibility.Screen`) and mouse aiming (`AiModPlayer` maps world → mouse with it) use it.
- **Lighting**, and with it **map reveal**: `Main.DoLightTiles` calls `Lighting.LightTiles(firstX, lastX, firstY,
  lastY)`; the lighting engine also exports to the map (`Main.Map`), which is what `IsRevealed` reads. Fair vision
  uses `Lighting.Brightness` and `Main.Map.IsRevealed`. Lighting is client-only ("Lighting is not calculated on the
  server", [Lighting.cs.patch](https://github.com/tModLoader/tModLoader/blob/1.4.4/patches/tModLoader/Terraria/Lighting.cs.patch)).
- Everything else in DoDraw is pictures: tiles (tModLoader notes `RenderTiles()` alone is "~30ms at peak",
  `Main.cs.patch` hunk `@@ -50898`), walls, backgrounds, entities, dust, UI. The mod's chest, shop, chat and crafting
  code calls the game APIs directly (`SetTalkNPC`, `npcShop`, `BuyItem`, ...), not the UI.

The symbols were checked by compiling against the installed tModLoader (`On_Main.DoDraw_UpdateCameraPosition`,
`On_Main.DoLightTiles`, `Lighting.LightTiles(int,int,int,int)`, `Main.UpdateTimeAccumulator` (double),
`Game.SuppressDraw()`), not by decompiling.

Modes:

- `full`: vanilla.
- `minimal`: one drawn frame in 12 (about 5 fps at 1x): enough to watch what happens.
- `off`: `SuppressDraw()` every frame: no Draw, no Present, the window keeps its last picture. After every update
  that no vanilla draw follows, `HeadlessPass` runs vanilla's camera update (`Main.DoDraw_UpdateCameraPosition`, by
  reflection; fallback: centre on the player) and lights the screen area like `DoLightTiles`. That is one camera +
  lighting step per game update, the same as vanilla at 60 fps, so fair vision sees the same thing per tick.
  With a time scale above 1 the same pass runs between the extra updates in every mode, so lighting never lags.

Trade-offs and what to verify in game (the agent could not run the game):

- `off` with the camera/light pass: compare `get_map` and `find` near the player in `off` vs `full`; they should
  match. `dev_status.fastDev.headlessError` is set if the pass ever threw.
- Biome detection (scene metrics) and anything else vanilla might compute only while drawing: compare
  `get_state.biome` in both modes.
- `minimal`/`off` save GPU and the draw CPU time, the biggest part of a frame; lighting stays (it is needed).
- Smaller windows (`--res`) light fewer tiles but also shrink the screen rectangle, i.e. what fair vision sees.
  Keep the main game's resolution for vision-dependent tests. Retro/Trippy lighting is cheaper but gives different
  brightness than instance 0's Color mode, so test results wouldn't carry over; kept at Color.
- `--no-audio` launches with `SDL_AUDIODRIVER=dummy` (SDL's null audio device, which FNA/FAudio open through SDL);
  opt-in until it has been tried once.

### Dedicated server?

Not for this. A tModLoader server process has no local player (`Main.LocalPlayer` input, `ModPlayer.SetControls`
are client-side), no lighting (see above) and therefore no fair vision. A "bot client" would have to be a full
client anyway, which is what a test instance with `render off` is.

## 3. Test scenes and engine tests

`dev_setup` (test instances only; it cheats by design) builds a scene in one call, in this order:

```json
{"place_tiles": [{"x1":0,"y1":0,"x2":50,"y2":12,"tile":"Stone"}, {"x1":25,"y1":0,"y2":10,"clear":true},
                 {"x1":16,"y1":0,"x2":55,"y2":6,"liquid":"water"}, {"x1":14,"y1":3,"tile":"Heart"}],
 "clear_hostiles": true, "clear_items": true,
 "x": 3, "y": -1,
 "clear_inventory": true, "items": [{"name": "Copper Pickaxe"}, {"name": "Wooden Arrow", "count": 999}],
 "life": 100, "clear_buffs": true, "time": "noon",
 "spawn": [{"npc": "King Slime", "x": 95, "y": -2}]}
```

- Coordinates as everywhere: tile x,y, y down; `x,y` is the feet-left standing tile (as `get_state`).
- Tiles: TileID names (`Stone`, `Dirt`, `Platforms`, `Heart` = Life Crystal at its bottom-left tile,
  `PressurePlates`, `Torches`, `WoodBlock`); `clear` removes tile, wall and liquid; liquids `water|lava|honey|shimmer`.
  Max 40 000 tiles per call.
- Items by display or internal name; ammo goes into ammo slots. Time: `dawn|noon|dusk|midnight|H:MM`.
- It cancels the running task first and returns `{tilesChanged, tilesFailed?, items, spawned, position, life}`.

`scripts/engine-test.sh [-i 1,2] [--start] [--filter TEXT] [files]` runs scenario files
(`scripts/engine-test/scenarios/*.json`) through `scripts/engine-test/run.mjs`, one scenario at a time per instance,
instances in parallel. A scenario:

```json
{"name": "...", "anchor": {"dx": 300},
 "setup": {...dev_setup, coordinates relative to the anchor...},
 "settle_s": 1,
 "steps": [{"method": "move_to", "params": {...}, "timeout_s": 30},
           {"method": "attack", "params": {...}, "run_s": 30, "stop_when_done": true}],
 "expect": {"task": "succeeded", "near": {"x": 45, "y": -1, "tol": 1}, "alive": true, "life_min": 1,
            "buff": "Water Walking", "inventory": {"Life Crystal": ">=1"}, "reason_contains": "..."}}
```

- The anchor is `spawnX + dx`, `y = 0.45 * worldSurface` (from `dev_status.world`, test instances only): sky above
  the spawn area, below the space layer (low gravity starts at 0.35 * worldSurface), on any world size. Every
  `x/x1/x2` and `y/y1/y2` in setup, steps and expect is shifted by it. Each scenario clears its own box first, so it
  doesn't depend on the world's terrain.
- Steps call bridge methods (the same names and params as the MCP tools). `timeout_s` and `run_s` are **game**
  seconds (polled through `dev_status.fastDev.gameUpdateCount`), so a scenario means the same at any time scale.
- Deaths are detected through `get_state.lastDeaths`. Exit code 0 = all passed.
- Offline self-test of the runner and the scenario files: `node --test scripts/engine-test/lib.test.mjs`.

The five scenarios come from failures seen in play: walk over a 1-wide hole, climb out of a flooded pit, cross a lake
with Water Walking, collect a Life Crystal next to a pressure plate under water, survive King Slime for 30 s.
They are regression tests: some may fail until the behaviour they describe is fixed.

## 4. Other speed-ups

- **Reuse running instances.** Scenarios rebuild their own terrain, so `engine-test.sh` runs on instances that are
  already up; only a new mod build needs `instance.sh build` + `reload N` (~25 s; tModLoader can't swap a mod's
  assembly without reloading all mods, which costs about the same).
- **Parallel instances.** `-i 1,2,3` spreads the scenarios over three instances (each ~1-2 GB RAM; with drawing off
  the GPU cost is small).
- **Startup.** Test instances load only LlmPlayer (`enabled.json`) and skip the menus with the auto-load file
  (menus and world loading are always drawn: the fast-dev modes apply in a world only). The auto-load waits 3 s at the title screen for input profiles to settle;
  that could be shortened for test instances if it proves safe.
- **Change speed without restarting:** `scripts/instance.sh speed N --timescale 4` / `--render minimal` / `--watch`.

## Command reference (bridge only, never MCP tools)

| Command | Gate | Reply |
|---|---|---|
| `dev_timescale {factor}` | test instance | `dev_status.fastDev` |
| `dev_render {mode}` | test instance | `dev_status.fastDev` |
| `dev_setup {...}` | test instance, in a world | what was done |
| `dev_status` | none | adds `fastDev {testInstance, testInstanceReason, timeScale, render, ups, fps, engineFramesPerSecond, gameUpdateCount, extraUpdateEffect, headlessPasses, headlessError, launchNote}` and, in test instances, `world {spawnX, spawnY, maxTilesX, maxTilesY, worldSurface}` |

Launch options (test instances; ignored with a log line elsewhere): `-llmrender off|minimal|full`,
`-llmtimescale 1..8|max`. `instance.sh start/reload N` passes `--render` (default `off`) and `--timescale`
(default `max`); `--watch` = `--render full --timescale 1`.
