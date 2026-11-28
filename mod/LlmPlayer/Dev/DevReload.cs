using LlmPlayer.Bridge;
using LlmPlayer.Config;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Control;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace LlmPlayer.Dev;

/// <summary>
/// Developer reload loop (scripts/dev-reload.sh). Not an MCP tool.
///
/// <c>dev_save_and_exit {reload?}</c>: gated behind the <see cref="LlmConfig.DevCommands"/> config option (default
/// off), single player only. In a world it runs exactly what the in-game "Save &amp; Exit" button runs (tModLoader
/// 1.4.4 IngameOptions.Draw, read from IL): SteamedWraps.StopPlaytimeTracking → SystemLoader.PreSaveAndQuit →
/// IngameOptions.Close → menuMode 10, gameMenu → WorldGen.SaveAndQuit(callback). SaveAndQuitCallBack (thread pool)
/// saves the player (Player.SavePlayer) and the world (WorldFile.SaveWorld) synchronously, unloads, sets menuMode 0 and
/// only then invokes the callback. The callback writes the autoload file (when reload) and sets
/// <c>Main.GameAskedToQuit</c>, the flag the title menu's Exit button sets; Main.Update then runs QuitGame
/// (SaveSettings, SocialAPI.Shutdown, Game.Exit) on the main thread. At the title menu (nothing to save) it just exits.
/// It replies immediately; the script waits for the port to close.
///
/// <c>dev_status</c>: read-only, works from the menu: the last autoload outcome, so the script can fail fast, plus the
/// fast-dev state of test instances (FastDevSystem).
/// </summary>
[CommandGroup]
public static class DevCommands
{
    private static volatile bool _exiting;

    public static void Register()
    {
        CommandRouter.Register("dev_save_and_exit", SaveAndExit, isAction: false, requiresWorld: false);
        CommandRouter.Register("dev_status", _ => new
        {
            DevCommands = LlmConfig.Current.DevCommands,
            InWorld = InWorld,
            Autoload = DevAutoloadSystem.Status,
            Port = BridgeSystem.ListenPort,
            SavePath = Main.SavePath,
            Pid = Environment.ProcessId,
            // Test instances (docs/FAST-DEV.md): gate result, time scale, render mode, measured updates/draws per second.
            FastDev = FastDevSystem.Status(),
            // Test instances only: where scenario scripts may build (scripts/engine-test).
            World = InWorld && FastDevSystem.Gate().Allowed
                ? new { SpawnX = Main.spawnTileX, SpawnY = Main.spawnTileY, Main.maxTilesX, Main.maxTilesY, WorldSurface = (int)Main.worldSurface }
                : null,
        }, isAction: false, requiresWorld: false);

        // Dev fixture capture (DevCommands only, not an MCP tool, never used by the playing AI): the real tiles of a
        // rectangle as ArrayTileGrid.Parse glyphs, so navigation can be fuzz-tested offline on real terrain.
        CommandRouter.Register("dev_dump", p =>
        {
            if (!LlmConfig.Current.DevCommands) throw new CommandError("dev_commands_disabled", "dev commands are off");
            int x1 = Params.Int(p, "x1"), y1 = Params.Int(p, "y1"), x2 = Params.Int(p, "x2"), y2 = Params.Int(p, "y2");
            if (x2 < x1 || y2 < y1 || (long)(x2 - x1 + 1) * (y2 - y1 + 1) > 200_000)
                throw new CommandError(ErrorCodes.BadParams, "rectangle too big (max 200k tiles) or inverted");
            var grid = new LlmPlayer.Observe.GameTileGrid(Main.LocalPlayer, fairVision: false);
            var rows = new List<string>();
            var sb = new System.Text.StringBuilder();
            for (int y = y1; y <= y2; y++)
            {
                sb.Clear();
                for (int x = x1; x <= x2; x++)
                {
                    var t = grid.Get(x, y);
                    if (Main.tile[x, y].HasTile && Main.tile[x, y].TileType == Terraria.ID.TileID.Heart) { sb.Append('H'); continue; }
                    sb.Append(LlmPlayer.Core.Grid.ArrayTileGrid.GlyphOf(t)); // hardness before shape (hard slopes: ')' '(' '-' 'R' 'Q')
                }
                rows.Add(sb.ToString());
            }
            return new { X1 = x1, Y1 = y1, Rows = rows };
        }, isAction: false);

        // Dev diagnostics (DevCommands only, not an MCP tool): the path move_to would plan from here, and whether a
        // follower would call it invalidated right away. For debugging navigation bugs seen in game.
        CommandRouter.Register("dev_plan", p =>
        {
            if (!LlmConfig.Current.DevCommands) throw new CommandError("dev_commands_disabled", "dev commands are off");
            var pl = Main.LocalPlayer;
            var goal = new LlmPlayer.Core.Grid.TilePoint(Params.Int(p, "x"), Params.Int(p, "y"));
            int tol = Params.Has(p, "tolerance") ? Params.Int(p, "tolerance") : 1;
            var grid = new LlmPlayer.Observe.GameTileGrid(pl);
            var k = LlmPlayer.Core.Nav.NavMath.FromPixels(pl.position.X, pl.position.Y, pl.velocity.X, pl.velocity.Y, pl.height);
            var here = LlmPlayer.Core.Nav.NavMath.StandingTile(k, grid);
            var r = LlmPlayer.Core.Nav.Pathfinder.FindPath(grid, here, goal, LlmPlayer.Nav.ProfileBuilder.From(pl),
                new LlmPlayer.Core.Nav.PathOptions(GoalTolerance: tol));
            string? firstTick = null;
            if (r.Success && r.Steps.Count > 0)
            {
                var f = new LlmPlayer.Core.Nav.PathFollower(r.Steps, start: here, physics: LlmPlayer.Nav.ProfileBuilder.Physics(pl));
                firstTick = f.Tick(k, grid).Status.ToString();
            }
            return new
            {
                From = here, Goal = goal, r.Success, r.FailureReason, r.ResolvedGoal, r.NodesExpanded, FirstTick = firstTick,
                Steps = r.Steps.Take(40).Select(st => new { st.Kind, st.Pos, st.WorkTile }).ToList(),
            };
        }, isAction: false);
    }

    private static bool InWorld => !Main.gameMenu && Main.LocalPlayer is { active: true };

    private static object SaveAndExit(System.Text.Json.JsonElement p)
    {
        if (!LlmConfig.Current.DevCommands)
            throw new CommandError("dev_commands_disabled",
                "dev commands are off: enable DevCommands in Workshop → Manage Mods → LLM Player → Config");
        if (Main.netMode != NetmodeID.SinglePlayer)
            throw new CommandError("not_singleplayer", "dev_save_and_exit only works in single player");
        if (_exiting) return new { Saving = true, AlreadyExiting = true };
        var reload = Params.Bool(p, "reload", false);

        if (!InWorld)
        {
            // Any menu outside a world is safe to leave (everything was saved on leaving the world) — except while a
            // world is being generated or saved. A failed auto-load leaves the game on a status screen, and refusing
            // there (title screen only, as before) stranded the automated loop on the first real run.
            // menuMode 10 is the status screen vanilla shows while saving/loading/generating (SaveAndQuitCallBack sets
            // it before saving); our own auto-load failure screen is 1000000, which is safe to leave.
            if (WorldGen.generatingWorld || Main.menuMode == 10)
                throw new CommandError(ErrorCodes.NotInWorld, "a world is being generated, loaded or saved; try again when it's done");
            _exiting = true;
            LlmPlayerMod.Log("dev_save_and_exit: at the title screen, exiting");
            RequestExit();
            return new { Saving = false, Exiting = true, Reload = false };
        }

        if (!Main.mouseItem.IsAir)
            throw new CommandError("item_on_cursor", "an item is held on the mouse cursor; put it in the inventory first");

        var player = Main.ActivePlayerFileData ?? throw new CommandError(ErrorCodes.Internal, "no active player file");
        var world = Main.ActiveWorldFileData ?? throw new CommandError(ErrorCodes.Internal, "no active world file");
        var request = new AutoloadRequest(player.Name, world.Name, Path.GetFileName(player.Path), Path.GetFileName(world.Path));

        _exiting = true;
        LlmPlayerMod.Log($"dev_save_and_exit: saving {player.Name} in {world.Name} and exiting (reload={reload})");
        // Run between frames (Main.Update consumes these after the update, before drawing), not mid-update.
        Main.QueueMainThreadAction(() => StartSaveAndQuit(reload ? request : null));
        return new { Saving = true, Reload = reload, Player = player.Name, World = world.Name };
    }

    private static void StartSaveAndQuit(AutoloadRequest? reload)
    {
        try
        {
            Terraria.Social.Steam.SteamedWraps.StopPlaytimeTracking();
            SystemLoader.PreSaveAndQuit();
            IngameOptions.Close();
            Main.menuMode = 10;
            Main.gameMenu = true;
            WorldGen.SaveAndQuit(() => AfterSave(reload));
        }
        catch (Exception e)
        {
            _exiting = false;
            LlmPlayerMod.Log($"dev_save_and_exit: save & quit failed to start: {e}");
        }
    }

    /// <summary>Thread pool, after the player and world were saved and the game is back at the title screen.</summary>
    private static void AfterSave(AutoloadRequest? reload)
    {
        if (reload != null)
        {
            try
            {
                File.WriteAllText(DevAutoloadSystem.FilePath, DevAutoload.Serialize(reload with { CreatedUtc = DateTime.UtcNow }));
                LlmPlayerMod.Log($"dev_save_and_exit: wrote {DevAutoloadSystem.FilePath}");
            }
            catch (Exception e)
            {
                LlmPlayerMod.Log($"dev_save_and_exit: could not write the autoload file: {e.Message}");
            }
        }
        LlmPlayerMod.Log("dev_save_and_exit: saved, exiting the game");
        RequestExit();
    }

    // Main.GameAskedToQuit is internal; it's what the title menu's Exit button sets (Main.DrawMenu), and Main.Update
    // then calls QuitGame on the main thread. Fallback if it's ever renamed: Game.Exit from the main thread.
    private static readonly System.Reflection.FieldInfo? GameAskedToQuit = typeof(Main).GetField("GameAskedToQuit",
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

    private static void RequestExit()
    {
        if (GameAskedToQuit?.FieldType == typeof(bool))
            GameAskedToQuit.SetValue(null, true);
        else
            Main.QueueMainThreadAction(() => Main.instance.Exit());
    }
}

/// <summary>
/// One-shot auto-load: once per game process, when the title screen is first shown after mods loaded, consume
/// <c>&lt;Main.SavePath&gt;/LlmPlayerAutoload.json</c> (deleted before anything else, so it can never loop) and enter
/// that player and world the way the menus do (Main.SelectPlayer's single-player branch, then UIWorldListItem.PlayGame:
/// SetAsActive, menuMode 10, WorldGen.playWorld). Any problem: logged, shown on the status screen, nothing loaded.
/// Entering a world by hand first disables it for this process.
/// </summary>
public sealed class DevAutoloadSystem : ModSystem
{
    /// <summary>Frames the title screen must be up before acting (lets the menu settle after mod loading).</summary>
    private const int SettleFrames = 180; // 3 s: input profiles/keybinds must be ready before entering a world

    private static bool _done;
    private static int _titleFrames;

    /// <summary>"none" (no file), "started: …", "failed: …"; null before the check ran.</summary>
    public static string? Status { get; private set; }

    public static string FilePath => Path.Combine(Main.SavePath, DevAutoload.FileName);

    public override void Load()
    {
        if (Main.dedServ) return;
        On_Main.DoUpdate += CheckThenUpdate;
    }

    public override void Unload()
    {
        On_Main.DoUpdate -= CheckThenUpdate;
        _done = false;
        _titleFrames = 0;
        Status = null;
    }

    public override void OnWorldLoad() => _done = true;

    private static void CheckThenUpdate(On_Main.orig_DoUpdate orig, Main self, ref Microsoft.Xna.Framework.GameTime gameTime)
    {
        if (!_done)
        {
            if (Main.gameMenu && Main.menuMode == 0 && Main.netMode == NetmodeID.SinglePlayer)
            {
                if (++_titleFrames >= SettleFrames)
                {
                    _done = true;
                    try
                    {
                        TryAutoload();
                    }
                    catch (Exception e)
                    {
                        Fail($"unexpected error: {e.Message}");
                        LlmPlayerMod.Log(e.ToString());
                    }
                }
            }
            else
            {
                _titleFrames = 0;
            }
        }
        orig(self, ref gameTime);
    }

    private static void TryAutoload()
    {
        var path = FilePath;
        if (!File.Exists(path))
        {
            Status = "none";
            return;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
            File.Delete(path); // one-shot: gone before we act on it
        }
        catch (Exception e)
        {
            Fail($"could not read/delete {path}: {e.Message}");
            return;
        }

        var req = DevAutoload.Parse(text, out var error);
        if (req == null)
        {
            Fail($"{DevAutoload.FileName}: {error}");
            return;
        }
        if (!DevAutoload.IsFresh(req, DateTime.UtcNow))
        {
            Fail($"{DevAutoload.FileName} is older than {DevAutoload.MaxAge.TotalMinutes:0} minutes; ignored");
            return;
        }

        Main.LoadPlayers();
        var players = Main.PlayerList;
        var pi = DevAutoload.Pick(players.Select(d => new SaveEntry(d.Name, Path.GetFileName(d.Path))).ToList(),
            req.Player, req.PlayerFile, "player", out error);
        if (pi < 0)
        {
            Fail(error);
            return;
        }
        var playerData = players[pi];
        if (playerData.Player.loadStatus != 0)
        {
            Fail($"player \"{playerData.Name}\" failed to load (status {playerData.Player.loadStatus})");
            return;
        }

        // Main.SelectPlayer, single-player branch.
        Main.menuMultiplayer = false;
        Main.menuServer = false;
        Main.ServerSideCharacter = false;
        Main.myPlayer = 0;
        playerData.SetAsActive();
        Main.player[Main.myPlayer].position = Microsoft.Xna.Framework.Vector2.Zero;
        Main.LoadWorlds();

        var worlds = Main.WorldList;
        var wi = DevAutoload.Pick(worlds.Select(d => new SaveEntry(d.Name, Path.GetFileName(d.Path))).ToList(),
            req.World, req.WorldFile, "world", out error);
        if (wi < 0)
        {
            Fail(error);
            return;
        }
        var worldData = worlds[wi];
        if (!worldData.IsValid)
        {
            Fail($"world \"{worldData.Name}\" is not valid");
            return;
        }
        var mismatch = DevAutoload.GameModeMismatch(playerData.Player.difficulty == PlayerDifficultyID.Creative,
            worldData.GameMode == GameModeID.Creative);
        if (mismatch != null)
        {
            Fail(mismatch);
            return;
        }

        // UIWorldListItem.PlayGame, single-player branch.
        worldData.SetAsActive();
        Main.clrInput();
        Main.menuMode = 10;
        Status = $"started: {playerData.Name} in {worldData.Name}";
        LlmPlayerMod.Log($"autoload: entering {worldData.Name} as {playerData.Name}");
        WorldGen.playWorld();
    }

    private static void Fail(string why)
    {
        Status = "failed: " + why;
        LlmPlayerMod.Log("autoload failed: " + why);
        // Vanilla's status screen (as the menus use for "can't load this world"); it has a Back button.
        Main.statusText = "LLM Player auto-load: " + why;
        Main.menuMode = 1000000;
    }
}
