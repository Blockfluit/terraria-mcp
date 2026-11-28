using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlmPlayer.Core.Control;

/// <summary>How much a test instance draws. <see cref="Full"/> is vanilla (the only mode instance 0 ever has).</summary>
public enum RenderMode
{
    /// <summary>Vanilla drawing every frame.</summary>
    Full,
    /// <summary>Draw one frame in <see cref="FastDev.MinimalDrawEvery"/> (≈5 fps at 60 Hz): enough to watch, cheap.</summary>
    Minimal,
    /// <summary>Never draw: the mod moves the camera and lights the screen area itself (see FastDevSystem).</summary>
    Off,
}

/// <summary>Result of the test-instance gate: <see cref="Allowed"/> or the reason it was refused.</summary>
public readonly record struct GateResult(bool Allowed, string Reason)
{
    public static GateResult Ok(int instance) => new(true, $"test instance {instance}");
    public static GateResult No(string why) => new(false, why);
}

/// <summary>Launch-time defaults for a test instance (<c>-llmrender off</c>, <c>-llmtimescale max</c>).</summary>
public readonly record struct DevLaunchOptions(RenderMode? Render, int? TimeScale, string? Warning = null);

/// <summary>
/// Pure logic of the fast-development tools for TEST instances (docs/FAST-DEV.md): the gate that keeps them away from
/// the user's real game (instance 0), launch options, the time-scale pacer and the rate meter. The mod does the hooks.
/// </summary>
public static class FastDev
{
    /// <summary>Written by scripts/instance.sh into a test instance's save folder only: <c>{"instance": N}</c>, N ≥ 1.</summary>
    public const string MarkerFileName = "LlmPlayerTestInstance.json";
    public const string SaveDirArg = "-tmlsavedirectory";
    public const string RenderArg = "-llmrender";
    public const string TimeScaleArg = "-llmtimescale";
    /// <summary>The main game's bridge port. A test instance never listens there.</summary>
    public const int MainPort = 7878;
    /// <summary>Highest numeric factor; <see cref="Max"/> means "as fast as the machine allows".</summary>
    public const int MaxFactor = 8;
    /// <summary>The "max" time scale: run updates until the frame's time budget is used up.</summary>
    public const int Max = 0;
    /// <summary>Hard cap of updates in one frame (max mode, or catching up).</summary>
    public const int MaxUpdatesPerFrame = 40;
    /// <summary>Minimal render: draw one frame in this many.</summary>
    public const int MinimalDrawEvery = 12;

    /// <summary>
    /// May this process use the test-only dev tools (time scale, render off, dev_setup)? Every condition must hold:
    /// DevCommands on; the save folder came from <c>-tmlsavedirectory</c> (instance.sh passes it, Steam never does) and
    /// is the folder the game uses; that folder holds the test-instance marker for an instance ≥ 1; and the bridge is
    /// not on the main game's port. Instance 0 (Steam launch, default save folder, port 7878) fails several of them.
    /// </summary>
    public static GateResult Check(IReadOnlyList<string> args, string savePath, string? markerText, int listenPort, bool devCommands)
    {
        if (!devCommands) return GateResult.No("dev commands are off (DevCommands in the mod config)");
        var dir = ArgValue(args, SaveDirArg);
        if (string.IsNullOrWhiteSpace(dir))
            return GateResult.No($"this is the main game (instance 0): no {SaveDirArg} on the command line; test-only dev tools work in test instances (scripts/instance.sh) only");
        if (!SamePath(dir, savePath))
            return GateResult.No($"the save folder ({savePath}) is not the {SaveDirArg} folder ({dir})");
        if (markerText == null)
            return GateResult.No($"no {MarkerFileName} in the save folder: not a test instance made by scripts/instance.sh");
        var n = ParseMarker(markerText);
        if (n is not { } instance || instance < 1)
            return GateResult.No($"{MarkerFileName} does not name a test instance (expected {{\"instance\": N}} with N >= 1)");
        if (listenPort == MainPort || listenPort <= 0)
            return GateResult.No(listenPort <= 0 ? "the bridge is not listening" : $"the bridge is on the main game's port {MainPort}");
        return GateResult.Ok(instance);
    }

    /// <summary>The instance number in the marker file, or null when the text isn't <c>{"instance": N}</c>.</summary>
    public static int? ParseMarker(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is JsonObject o && o["instance"] is JsonValue v && v.TryGetValue<int>(out var n))
                return n;
        }
        catch (JsonException)
        {
        }
        return null;
    }

    public static string MarkerText(int instance) => new JsonObject { ["instance"] = instance }.ToJsonString();

    /// <summary><c>-llmrender off|minimal|full</c> and <c>-llmtimescale 1..8|max</c> (also <c>=value</c>). Bad values are skipped with a warning.</summary>
    public static DevLaunchOptions ParseLaunch(IReadOnlyList<string> args)
    {
        var warnings = new List<string>();
        RenderMode? render = null;
        int? scale = null;
        var r = ArgValue(args, RenderArg);
        if (r != null)
        {
            if (TryRender(r, out var m)) render = m;
            else warnings.Add($"ignoring {RenderArg} '{r}': expected full, minimal or off");
        }
        var t = ArgValue(args, TimeScaleArg);
        if (t != null)
        {
            if (TryTimeScale(t, out var f)) scale = f;
            else warnings.Add($"ignoring {TimeScaleArg} '{t}': expected 1..{MaxFactor} or max");
        }
        return new DevLaunchOptions(render, scale, warnings.Count == 0 ? null : string.Join("; ", warnings));
    }

    public static bool TryRender(string s, out RenderMode mode)
    {
        switch (s.Trim().ToLowerInvariant())
        {
            case "full": mode = RenderMode.Full; return true;
            case "minimal": mode = RenderMode.Minimal; return true;
            case "off": mode = RenderMode.Off; return true;
            default: mode = RenderMode.Full; return false;
        }
    }

    public static string RenderName(RenderMode m) => m.ToString().ToLowerInvariant();

    /// <summary>"max" → <see cref="Max"/>, "1".."8" → the factor.</summary>
    public static bool TryTimeScale(string s, out int factor)
    {
        s = s.Trim();
        if (s.Equals("max", StringComparison.OrdinalIgnoreCase))
        {
            factor = Max;
            return true;
        }
        return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out factor) && factor is >= 1 and <= MaxFactor;
    }

    /// <summary>A JSON param: a number 1..8 or the string "max".</summary>
    public static bool TryTimeScale(JsonElement e, out int factor)
    {
        factor = 1;
        if (e.ValueKind == JsonValueKind.Number) return e.TryGetInt32(out factor) && factor is >= 1 and <= MaxFactor;
        return e.ValueKind == JsonValueKind.String && TryTimeScale(e.GetString() ?? "", out factor);
    }

    public static string TimeScaleName(int factor) => factor == Max ? "max" : factor.ToString(CultureInfo.InvariantCulture);

    /// <summary>The value after <paramref name="flag"/> (<c>flag value</c> or <c>flag=value</c>), else null.</summary>
    public static string? ArgValue(IReadOnlyList<string> args, string flag)
    {
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return i + 1 < args.Count ? args[i + 1] : "";
            if (a.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase)) return a[(flag.Length + 1)..];
        }
        return null;
    }

    private static bool SamePath(string a, string b)
    {
        static string Norm(string p) => p.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
        return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// How many game updates to run in this frame for a time scale. A numeric factor k runs k updates per 1/60 s of wall
/// time, whatever rate the engine calls Update at (fixed 60 Hz, or faster with drawing off): wall time is accumulated
/// and spent in whole updates, with a cap so a slow machine can't spiral. <see cref="FastDev.Max"/> runs updates until
/// the frame's time budget is used (<see cref="ContinueMax"/>).
/// </summary>
public sealed class TimeScalePacer
{
    private double _owed;

    public int Factor { get; private set; } = 1;

    public void SetFactor(int factor)
    {
        Factor = factor;
        _owed = 0;
    }

    /// <summary>Updates owed after <paramref name="elapsedSeconds"/> of wall time since the last frame (factor mode).</summary>
    public int Plan(double elapsedSeconds)
    {
        if (Factor == FastDev.Max) return FastDev.MaxUpdatesPerFrame;
        if (!(elapsedSeconds > 0)) elapsedSeconds = 0;
        _owed += Math.Min(elapsedSeconds, 0.25) * 60.0 * Factor;
        int cap = Math.Min(FastDev.MaxUpdatesPerFrame, 4 * Factor);
        if (_owed > cap) _owed = cap; // can't keep up: drop the backlog instead of spiralling
        int n = (int)_owed;
        _owed -= n;
        return n;
    }

    /// <summary>Max mode: run another update? Always at least one; then while the frame has used less than the budget.</summary>
    public static bool ContinueMax(int done, double frameMs, double budgetMs) =>
        done < 1 || (done < FastDev.MaxUpdatesPerFrame && frameMs < budgetMs);
}

/// <summary>Updates, draws and frames per second, over windows of about a second.</summary>
public sealed class RateMeter
{
    private double _t0 = double.NaN;
    private long _u0, _d0, _f0;

    public double Ups { get; private set; }
    public double Fps { get; private set; }
    public double FramesPerSecond { get; private set; }

    /// <summary>Feed the running totals; rates are recomputed once at least <paramref name="window"/> seconds passed.</summary>
    public void Sample(double nowSeconds, long updates, long draws, long frames, double window = 1.0)
    {
        if (double.IsNaN(_t0) || updates < _u0 || draws < _d0 || frames < _f0)
        {
            (_t0, _u0, _d0, _f0) = (nowSeconds, updates, draws, frames);
            return;
        }
        double dt = nowSeconds - _t0;
        if (dt < window) return;
        Ups = (updates - _u0) / dt;
        Fps = (draws - _d0) / dt;
        FramesPerSecond = (frames - _f0) / dt;
        (_t0, _u0, _d0, _f0) = (nowSeconds, updates, draws, frames);
    }
}
