using System.Globalization;
using System.Text.Json.Nodes;

namespace LlmPlayer.Core.Bridge;

/// <summary>Where the requested bridge port came from.</summary>
public enum PortSource { Config, Environment, CommandLine }

/// <summary>The requested bridge port. <see cref="Warning"/> is set when an override was present but unusable.</summary>
public readonly record struct PortChoice(int Port, PortSource Source, string? Warning = null);

/// <summary>What a running game wrote to <c>&lt;Main.SavePath&gt;/LlmPlayerInstance.json</c> once the bridge listened.</summary>
public sealed record InstanceStatus(int Port, int RequestedPort, PortSource Source, int Pid, string SavePath, DateTime StartedUtc);

/// <summary>
/// Multi-instance support (scripts/instance.sh): several tModLoader clients on one PC, each with its own save directory
/// (<c>-tmlsavedirectory</c>) and its own bridge port. Everything per instance that the mod reads or writes (the
/// autoload request, the status file, backups) lives under <c>Main.SavePath</c>, so a separate save directory already
/// keeps instances from taking each other's files; only the TCP port is global, and it is chosen here.
/// Port order: <c>-llmport N</c> (or <c>-llmport=N</c>) on the command line, else the <c>LLMPLAYER_PORT</c> environment
/// variable, else the config's Port (default 7878). If that port is taken the bridge tries the next ones
/// (<see cref="MaxPortTries"/> in all) and writes the port it got to the status file.
/// Pure logic only: the mod does the sockets and the file IO.
/// </summary>
public static class BridgeInstance
{
    public const string PortArg = "-llmport";
    public const string PortEnv = "LLMPLAYER_PORT";
    public const string StatusFileName = "LlmPlayerInstance.json";
    public const int MaxPortTries = 10;
    public const int MinPort = 1024;
    public const int MaxPort = 65535;

    /// <summary>The requested port: command line, then environment, then config. Bad overrides are skipped with a warning.</summary>
    public static PortChoice ChoosePort(IReadOnlyList<string> args, string? env, int configPort)
    {
        var warnings = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string? value = null;
            if (string.Equals(a, PortArg, StringComparison.OrdinalIgnoreCase))
                value = i + 1 < args.Count ? args[i + 1] : "";
            else if (a.StartsWith(PortArg + "=", StringComparison.OrdinalIgnoreCase))
                value = a[(PortArg.Length + 1)..];
            if (value == null) continue;
            if (TryPort(value, out var p)) return new PortChoice(p, PortSource.CommandLine, Join(warnings));
            warnings.Add($"ignoring {PortArg} '{value}': expected a port {MinPort}-{MaxPort}");
            break; // a later -llmport would be ambiguous; fall back to env/config
        }

        if (!string.IsNullOrWhiteSpace(env))
        {
            if (TryPort(env, out var p)) return new PortChoice(p, PortSource.Environment, Join(warnings));
            warnings.Add($"ignoring {PortEnv}='{env.Trim()}': expected a port {MinPort}-{MaxPort}");
        }

        var cfg = configPort is >= MinPort and <= MaxPort ? configPort : 7878;
        return new PortChoice(cfg, PortSource.Config, Join(warnings));
    }

    /// <summary>The ports to try, in order: <paramref name="start"/> and the next ones, never past <see cref="MaxPort"/>.</summary>
    public static IEnumerable<int> Candidates(int start, int maxTries = MaxPortTries)
    {
        for (int i = 0; i < maxTries && start + i <= MaxPort; i++)
            yield return start + i;
    }

    /// <summary>The first candidate <paramref name="tryBind"/> accepts (true = now listening), else null.</summary>
    public static int? BindFirstFree(int start, Func<int, bool> tryBind, int maxTries = MaxPortTries)
    {
        foreach (var p in Candidates(start, maxTries))
            if (tryBind(p)) return p;
        return null;
    }

    public static string SerializeStatus(InstanceStatus s) => new JsonObject
    {
        ["port"] = s.Port,
        ["requestedPort"] = s.RequestedPort,
        ["source"] = s.Source.ToString().ToLowerInvariant(),
        ["pid"] = s.Pid,
        ["savePath"] = s.SavePath,
        ["startedUtc"] = s.StartedUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
    }.ToJsonString();

    private static bool TryPort(string s, out int port) =>
        int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= MinPort and <= MaxPort;

    private static string? Join(List<string> w) => w.Count == 0 ? null : string.Join("; ", w);
}
