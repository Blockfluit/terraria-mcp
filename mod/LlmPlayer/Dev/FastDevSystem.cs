using System.Diagnostics;
using System.Reflection;
using LlmPlayer.Bridge;
using LlmPlayer.Config;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Control;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace LlmPlayer.Dev;

/// <summary>
/// Fast development in TEST instances only (docs/FAST-DEV.md): time scale (k game updates per 1/60 s, or "max") and
/// render modes (full / minimal / off). Instance 0, the user's real game, can never turn either on: every setter goes
/// through <see cref="Require"/> (DevCommands + <see cref="FastDev.Check"/>), and with the defaults (factor 1, full)
/// the Main.Update detour is a plain pass-through.
///
/// Mechanism. A detour on <c>Main.Update</c> (the XNA/FNA update: Main.DoUpdate = input, bridge drain, the whole world
/// update incl. every ModPlayer/ModSystem hook, then main-thread actions) calls the original several times per engine
/// frame, so everything per tick (tasks, follower, reflexes, NPC AI, physics) runs exactly as at 60 Hz, only more of
/// it per wall-clock second. Drawing is what Main.Draw does; in minimal/off it is skipped with FNA's
/// <c>Game.SuppressDraw()</c>. Two things the AI depends on live in the draw path (Main.DoDraw): the camera
/// (<c>Main.screenPosition</c>, which fair vision's screen rectangle and mouse aiming use) and lighting
/// (<c>Lighting.LightTiles</c>, which also reveals the map: the lighting engine exports to the minimap). So after
/// every update that is not followed by a vanilla draw, <see cref="HeadlessPass"/> runs vanilla's camera update
/// (Main.DoDraw_UpdateCameraPosition, by reflection) and lights the screen area like Main.DoLightTiles: one camera +
/// lighting step per game update, as vanilla does at 60 fps, so fair vision sees the same thing per game tick.
/// </summary>
public sealed class FastDevSystem : ModSystem
{
    public const string NotTestInstance = "not_test_instance";

    private static readonly TimeScalePacer Pacer = new();
    private static readonly RateMeter Meter = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double _lastFrame;
    private static long _frames, _draws, _extraCalls, _extraAdvanced, _headless;
    private static bool _launchChecked;
    private static Action? _cameraUpdate;
    private static bool _cameraResolved;

    public static RenderMode Render { get; private set; } = RenderMode.Full;
    public static int TimeScale => Pacer.Factor;
    public static string? HeadlessError { get; private set; }
    public static string? LaunchNote { get; private set; }

    public override void Load()
    {
        if (Main.dedServ) return;
        On_Main.Update += UpdateHook;
        On_Main.DoDraw += CountDraw;
    }

    public override void Unload()
    {
        On_Main.Update -= UpdateHook;
        On_Main.DoDraw -= CountDraw;
        Pacer.SetFactor(1);
        Render = RenderMode.Full;
        _launchChecked = false;
        _cameraResolved = false;
        _cameraUpdate = null;
        HeadlessError = LaunchNote = null;
    }

    /// <summary>Launch defaults (-llmrender / -llmtimescale, scripts/instance.sh) apply on the first world entered.</summary>
    public override void OnWorldLoad()
    {
        if (_launchChecked) return;
        _launchChecked = true;
        var o = FastDev.ParseLaunch(Environment.GetCommandLineArgs());
        if (o.Warning != null) LlmPlayerMod.Log("[fastdev] " + o.Warning);
        if (o.Render == null && o.TimeScale == null) return;
        var g = Gate();
        if (!g.Allowed)
        {
            LaunchNote = "launch options ignored: " + g.Reason;
            LlmPlayerMod.Log("[fastdev] " + LaunchNote);
            return;
        }
        if (o.Render is { } r) Render = r;
        if (o.TimeScale is { } t) Pacer.SetFactor(t);
        LaunchNote = $"from the command line: render {FastDev.RenderName(Render)}, timescale {FastDev.TimeScaleName(Pacer.Factor)}";
        LlmPlayerMod.Log("[fastdev] " + LaunchNote);
    }

    public static GateResult Gate()
    {
        string? marker = null;
        try
        {
            var f = Path.Combine(Main.SavePath, FastDev.MarkerFileName);
            if (File.Exists(f)) marker = File.ReadAllText(f);
        }
        catch (Exception)
        {
            // unreadable marker = no marker
        }
        return FastDev.Check(Environment.GetCommandLineArgs(), Main.SavePath, marker, BridgeSystem.ListenPort,
            LlmConfig.Current.DevCommands);
    }

    /// <summary>Throws not_test_instance unless this is a test instance with DevCommands on.</summary>
    public static int Require()
    {
        var g = Gate();
        if (!g.Allowed) throw new CommandError(NotTestInstance, g.Reason + " (test-only dev command)");
        return FastDev.ParseMarker(File.ReadAllText(Path.Combine(Main.SavePath, FastDev.MarkerFileName))) ?? 0;
    }

    public static void SetTimeScale(int factor)
    {
        Require();
        Pacer.SetFactor(factor);
        LlmPlayerMod.Log($"[fastdev] timescale {FastDev.TimeScaleName(factor)}");
    }

    public static void SetRender(RenderMode mode)
    {
        Require();
        Render = mode;
        LlmPlayerMod.Log($"[fastdev] render {FastDev.RenderName(mode)}");
    }

    /// <summary>For dev_status.</summary>
    public static object Status()
    {
        var g = Gate();
        return new
        {
            TestInstance = g.Allowed,
            TestInstanceReason = g.Reason,
            TimeScale = FastDev.TimeScaleName(Pacer.Factor),
            Render = FastDev.RenderName(Render),
            Ups = Math.Round(Meter.Ups, 1),
            Fps = Math.Round(Meter.Fps, 1),
            EngineFramesPerSecond = Math.Round(Meter.FramesPerSecond, 1),
            GameUpdateCount = Main.GameUpdateCount,
            // Extra Main.Update calls in a frame that really advanced the world (should be 1.0; if not, the engine
            // gated them and the time scale has no effect: see docs/FAST-DEV.md).
            ExtraUpdateEffect = _extraCalls == 0 ? (double?)null : Math.Round((double)_extraAdvanced / _extraCalls, 3),
            HeadlessPasses = _headless,
            HeadlessError,
            LaunchNote,
        };
    }

    private static void CountDraw(On_Main.orig_DoDraw orig, Main self, GameTime gameTime)
    {
        _draws++;
        orig(self, gameTime);
    }

    private static void UpdateHook(On_Main.orig_Update orig, Main self, GameTime gameTime)
    {
        double now = Clock.Elapsed.TotalSeconds;
        double elapsed = now - _lastFrame;
        _lastFrame = now;
        _frames++;
        Meter.Sample(now, Main.GameUpdateCount, _draws, _frames);

        bool active = (Pacer.Factor != 1 || Render != RenderMode.Full) && !Main.gameMenu && Main.netMode == NetmodeID.SinglePlayer;
        if (!active)
        {
            orig(self, gameTime);
            return;
        }

        bool draw = Render switch
        {
            RenderMode.Full => true,
            RenderMode.Minimal => _frames % FastDev.MinimalDrawEvery == 0,
            _ => false,
        };
        bool max = Pacer.Factor == FastDev.Max;
        int planned = Pacer.Plan(elapsed); // also at factor 1 (render off): drawing off can make the engine spin faster than 60 Hz
        double budgetMs = draw ? 10.0 : 15.0;
        double start = Clock.Elapsed.TotalMilliseconds;
        int done = 0;
        while (max ? TimeScalePacer.ContinueMax(done, Clock.Elapsed.TotalMilliseconds - start, budgetMs) : done < planned)
        {
            if (done > 0) HeadlessPass(); // camera + light for the state the previous update left (no draw in between)
            // While active the pacer decides how many updates run. Vanilla's own frame pacing (Main.UpdateTimeAccumulator,
            // used when frame skip is off) must not skip one of them: give it a full 1/60 s each time.
            if (Main.UpdateTimeAccumulator < 1.0 / 60.0) Main.UpdateTimeAccumulator = 1.0 / 60.0;
            uint before = Main.GameUpdateCount;
            orig(self, gameTime);
            if (done > 0)
            {
                _extraCalls++;
                if (Main.GameUpdateCount != before) _extraAdvanced++;
            }
            done++;
            if (Main.gameMenu) return; // left the world (save & exit) mid-batch: let the menu draw
        }
        if (done == 0 || !draw)
        {
            if (done > 0) HeadlessPass();
            self.SuppressDraw();
        }
    }

    /// <summary>What Main.DoDraw does that game logic depends on: move the camera, light the screen area (+ map reveal).</summary>
    private static void HeadlessPass()
    {
        try
        {
            UpdateCamera();
            var sp = Main.screenPosition;
            int x1 = (int)Math.Floor(sp.X / 16f) - 1, x2 = (int)Math.Floor((sp.X + Main.screenWidth) / 16f) + 2;
            int y1 = (int)Math.Floor(sp.Y / 16f) - 1, y2 = (int)Math.Floor((sp.Y + Main.screenHeight) / 16f) + 2;
            x1 = Math.Clamp(x1, 0, Main.maxTilesX - 1);
            x2 = Math.Clamp(x2, 0, Main.maxTilesX - 1);
            y1 = Math.Clamp(y1, 0, Main.maxTilesY - 1);
            y2 = Math.Clamp(y2, 0, Main.maxTilesY - 1);
            Lighting.LightTiles(x1, x2, y1, y2);
            _headless++;
        }
        catch (Exception e)
        {
            if (HeadlessError == null) LlmPlayerMod.Log($"[fastdev] headless pass failed: {e}");
            HeadlessError = $"{e.GetType().Name}: {e.Message}";
        }
    }

    private static void UpdateCamera()
    {
        if (!_cameraResolved)
        {
            _cameraResolved = true;
            var mi = typeof(Main).GetMethod("DoDraw_UpdateCameraPosition",
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes);
            if (mi != null)
                _cameraUpdate = mi.IsStatic
                    ? (Action)Delegate.CreateDelegate(typeof(Action), mi)
                    : (Action)Delegate.CreateDelegate(typeof(Action), Main.instance, mi);
            else
                LlmPlayerMod.Log("[fastdev] Main.DoDraw_UpdateCameraPosition not found: centring the camera on the player instead");
        }
        if (_cameraUpdate != null)
        {
            _cameraUpdate();
            return;
        }
        var p = Main.LocalPlayer;
        Main.screenPosition = new Vector2(p.Center.X - Main.screenWidth / 2f, p.Center.Y + p.gfxOffY - Main.screenHeight / 2f);
        float maxX = Main.maxTilesX * 16f - Main.screenWidth - 640f, maxY = Main.maxTilesY * 16f - Main.screenHeight - 640f;
        Main.screenPosition.X = Math.Clamp(Main.screenPosition.X, 640f, Math.Max(640f, maxX));
        Main.screenPosition.Y = Math.Clamp(Main.screenPosition.Y, 640f, Math.Max(640f, maxY));
    }
}
