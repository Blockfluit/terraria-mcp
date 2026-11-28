using System.Net.Sockets;
using LlmPlayer.Config;
using LlmPlayer.Core.Bridge;
using Terraria.ModLoader;

namespace LlmPlayer.Bridge;

/// <summary>
/// Owns the TCP server and runs everything bridge-related on the main thread:
/// drain inbound requests → dispatch → send responses, then run <see cref="Tick"/> subscribers.
/// </summary>
public sealed class BridgeSystem : ModSystem
{
    private const int MaxRequestsPerFrame = 64;

    private static JsonLineServer? _server;
    private static (string Bind, int Port) _bound;   // config values the server was started for
    private static PortChoice _requested;

    /// <summary>The port the bridge listens on (0 when it isn't listening). May differ from the requested port when
    /// that was taken: see <see cref="BridgeInstance"/>.</summary>
    public static int ListenPort { get; private set; }

    public static string StatusFilePath => Path.Combine(Terraria.Main.SavePath, BridgeInstance.StatusFileName);
    private static Action<string> _log = _ => { };

    /// <summary>Raised once per game update (PostUpdateEverything, in-world frames), after requests are handled.
    /// Subscribers run on the main thread; exceptions are logged and don't affect other subscribers.
    /// Cleared on unload, so subscribe from a Load/OnModLoad hook.</summary>
    public static event Action? Tick;

    public override void OnModLoad()
    {
        // Single player skips most of Main.DoUpdate while the window is unfocused (unless the AI drives), so
        // PostUpdateInput never runs: drain here too, so requests are answered while the user plays in another window.
        Terraria.On_Main.DoUpdate += DrainThenUpdate;
        _log = msg => Mod.Logger.Info(msg);
        CommandRouter.RegisterAll(_log);
        EnsureServer();
    }

    public override void OnWorldLoad() => EnsureServer();

    public override void Unload()
    {
        Terraria.On_Main.DoUpdate -= DrainThenUpdate;
        _server?.Stop();
        _server = null;
        ListenPort = 0;
        DeleteStatusFile();
        Tick = null;
        CommandRouter.Clear();
        _log = _ => { };
    }

    // PostUpdateInput also runs in the main menu, so not_in_world replies still go out there;
    // PostUpdateEverything is the in-world end-of-update hook. Draining twice per frame is harmless.
    public override void PostUpdateInput() => Drain();

    public override void PostUpdateEverything()
    {
        Drain();
        RunTicks();
    }

    /// <summary>Send an unsolicited event: {"event":evt, ...payload's fields (camelCase)}. Dropped if no client.</summary>
    public static void Emit(string evt, object? payload = null) => _server?.SendLine(WireProtocol.Event(evt, payload));

    internal static void OnConfigChanged(LlmConfig config)
    {
        if (_server == null || (_bound.Bind == config.BindAddress && _bound.Port == config.Port)) return;
        if (_bound.Bind == config.BindAddress && _requested.Source != PortSource.Config)
        {
            _log($"[bridge] config Port {config.Port} ignored: the port was set by {_requested.Source} ({_requested.Port})");
            _bound = (config.BindAddress, config.Port);
            return;
        }
        _log("bridge config changed; restarting server");
        _server.Stop();
        _server = null;
        ListenPort = 0;
        EnsureServer();
    }

    private static void EnsureServer()
    {
        if (_server != null) return;
        var cfg = LlmConfig.Current;
        _requested = BridgeInstance.ChoosePort(Environment.GetCommandLineArgs(),
            Environment.GetEnvironmentVariable(BridgeInstance.PortEnv), cfg.Port);
        if (_requested.Warning != null) _log("[bridge] " + _requested.Warning);
        JsonLineServer? started = null;
        var port = BridgeInstance.BindFirstFree(_requested.Port, p =>
        {
            var server = new JsonLineServer(msg => _log("[bridge] " + msg));
            try
            {
                server.Start(cfg.BindAddress, p);
                started = server;
                return true;
            }
            catch (SocketException e)
            {
                server.Stop();
                _log($"[bridge] could not listen on {cfg.BindAddress}:{p}: {e.Message}");
                return false;
            }
        });
        if (port is not { } bound || started == null)
        {
            _log($"[bridge] no free port in {_requested.Port}..{_requested.Port + BridgeInstance.MaxPortTries - 1}; bridge is off");
            return;
        }
        _server = started;
        _bound = (cfg.BindAddress, cfg.Port);
        ListenPort = bound;
        _log($"[bridge] port {bound} (requested {_requested.Port} from {_requested.Source}), save path {Terraria.Main.SavePath}");
        WriteStatusFile();
    }

    /// <summary>Tells scripts (scripts/instance.sh) which port this game process got.</summary>
    private static void WriteStatusFile()
    {
        try
        {
            File.WriteAllText(StatusFilePath, BridgeInstance.SerializeStatus(new InstanceStatus(ListenPort, _requested.Port,
                _requested.Source, Environment.ProcessId, Terraria.Main.SavePath, DateTime.UtcNow)));
        }
        catch (Exception e)
        {
            _log($"[bridge] could not write {StatusFilePath}: {e.Message}");
        }
    }

    private static void DeleteStatusFile()
    {
        try
        {
            if (File.Exists(StatusFilePath)) File.Delete(StatusFilePath);
        }
        catch (Exception)
        {
            // best effort: scripts also check the port and the pid
        }
    }

    private static void DrainThenUpdate(Terraria.On_Main.orig_DoUpdate orig, Terraria.Main self, ref Microsoft.Xna.Framework.GameTime gameTime)
    {
        Drain();
        orig(self, ref gameTime);
    }

    private static void Drain()
    {
        var server = _server;
        if (server == null) return;
        for (var i = 0; i < MaxRequestsPerFrame && server.Inbound.TryDequeue(out var req); i++)
            server.SendLine(CommandRouter.Handle(req.Id, req.Method, req.Params));
    }

    private static void RunTicks()
    {
        var tick = Tick;
        if (tick == null) return;
        foreach (var d in tick.GetInvocationList())
        {
            try
            {
                ((Action)d)();
            }
            catch (Exception e)
            {
                _log($"[bridge] tick hook {d.Method.DeclaringType?.Name}.{d.Method.Name} failed: {e}");
            }
        }
    }
}
