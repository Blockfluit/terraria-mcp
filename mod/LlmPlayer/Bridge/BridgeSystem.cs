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
    private static (string Bind, int Port) _bound;
    private static Action<string> _log = _ => { };

    /// <summary>Raised once per game update (PostUpdateEverything, in-world frames), after requests are handled.
    /// Subscribers run on the main thread; exceptions are logged and don't affect other subscribers.
    /// Cleared on unload, so subscribe from a Load/OnModLoad hook.</summary>
    public static event Action? Tick;

    public override void OnModLoad()
    {
        _log = msg => Mod.Logger.Info(msg);
        CommandRouter.RegisterAll(_log);
        EnsureServer();
    }

    public override void OnWorldLoad() => EnsureServer();

    public override void Unload()
    {
        _server?.Stop();
        _server = null;
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
        _log("bridge config changed; restarting server");
        _server.Stop();
        _server = null;
        EnsureServer();
    }

    private static void EnsureServer()
    {
        if (_server != null) return;
        var cfg = LlmConfig.Current;
        var server = new JsonLineServer(msg => _log("[bridge] " + msg));
        try
        {
            server.Start(cfg.BindAddress, cfg.Port);
            _server = server;
            _bound = (cfg.BindAddress, cfg.Port);
        }
        catch (SocketException e)
        {
            _log($"[bridge] could not listen on {cfg.BindAddress}:{cfg.Port}: {e.Message}");
        }
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
