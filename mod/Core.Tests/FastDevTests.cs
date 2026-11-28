using System.Text.Json;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Control;

namespace LlmPlayer.Core.Tests;

public class FastDevTests
{
    private const string Dir = @"C:\Users\u\AppData\Local\LlmPlayerInstances\2";
    private static readonly string[] TestArgs = { "tModLoader.dll", "-tmlsavedirectory", Dir, "-llmport", "7880" };
    private static readonly string Marker = FastDev.MarkerText(2);

    [Fact]
    public void Gate_AllowsAProperTestInstance()
    {
        var g = FastDev.Check(TestArgs, Dir, Marker, 7880, devCommands: true);
        Assert.True(g.Allowed, g.Reason);
        Assert.Equal("test instance 2", g.Reason);
    }

    [Fact]
    public void Gate_RefusesTheMainGame()
    {
        // Instance 0: launched by Steam (no -tmlsavedirectory), default save folder, port 7878, no marker.
        var g = FastDev.Check(new[] { "tModLoader.dll" }, @"C:\Users\u\Documents\My Games\Terraria\tModLoader", null, 7878, true);
        Assert.False(g.Allowed);
        Assert.Contains("instance 0", g.Reason);
    }

    [Fact]
    public void Gate_EveryConditionIsRequired()
    {
        Assert.False(FastDev.Check(TestArgs, Dir, Marker, 7880, devCommands: false).Allowed);
        Assert.False(FastDev.Check(TestArgs, @"C:\Other", Marker, 7880, true).Allowed);           // save path not from the arg
        Assert.False(FastDev.Check(TestArgs, Dir, null, 7880, true).Allowed);                     // no marker
        Assert.False(FastDev.Check(TestArgs, Dir, "{\"instance\":0}", 7880, true).Allowed);       // marker says instance 0
        Assert.False(FastDev.Check(TestArgs, Dir, "not json", 7880, true).Allowed);
        Assert.False(FastDev.Check(TestArgs, Dir, Marker, 7878, true).Allowed);                   // main port
        Assert.False(FastDev.Check(TestArgs, Dir, Marker, 0, true).Allowed);                      // bridge off
        Assert.False(FastDev.Check(new[] { "x", "-tmlsavedirectory" }, Dir, Marker, 7880, true).Allowed); // empty value
    }

    [Fact]
    public void Gate_PathCompareIgnoresCaseSlashesAndQuotes()
    {
        var args = new[] { "x", "-tmlsavedirectory=\"c:/users/u/appdata/local/llmplayerinstances/2/\"" };
        Assert.True(FastDev.Check(args, Dir, Marker, 7880, true).Allowed);
    }

    [Fact]
    public void Marker_RoundTrips()
    {
        Assert.Equal(3, FastDev.ParseMarker(FastDev.MarkerText(3)));
        Assert.Null(FastDev.ParseMarker("{\"instance\":\"3\"}"));
        Assert.Null(FastDev.ParseMarker("[]"));
    }

    [Fact]
    public void Launch_ParsesRenderAndTimeScale()
    {
        var o = FastDev.ParseLaunch(new[] { "x", "-llmrender", "off", "-llmtimescale=max" });
        Assert.Equal(RenderMode.Off, o.Render);
        Assert.Equal(FastDev.Max, o.TimeScale);
        Assert.Null(o.Warning);

        o = FastDev.ParseLaunch(new[] { "x", "-LLMRENDER=Minimal", "-llmtimescale", "4" });
        Assert.Equal(RenderMode.Minimal, o.Render);
        Assert.Equal(4, o.TimeScale);

        o = FastDev.ParseLaunch(new[] { "x", "-llmrender", "none", "-llmtimescale", "9" });
        Assert.Null(o.Render);
        Assert.Null(o.TimeScale);
        Assert.Contains("-llmrender", o.Warning);
        Assert.Contains("-llmtimescale", o.Warning);

        Assert.Equal(new DevLaunchOptions(null, null), FastDev.ParseLaunch(new[] { "x" }));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("8", 8)]
    [InlineData("MAX", FastDev.Max)]
    public void TimeScale_Accepts(string s, int want)
    {
        Assert.True(FastDev.TryTimeScale(s, out var f));
        Assert.Equal(want, f);
        Assert.True(FastDev.TryTimeScale(JsonDocument.Parse(want == FastDev.Max ? "\"max\"" : s).RootElement, out f));
        Assert.Equal(want, f);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("9")]
    [InlineData("-1")]
    [InlineData("2.5")]
    [InlineData("")]
    public void TimeScale_Rejects(string s) => Assert.False(FastDev.TryTimeScale(s, out _));

    [Fact]
    public void Pacer_RunsFactorUpdatesPerSixtiethOfASecond()
    {
        var p = new TimeScalePacer();
        p.SetFactor(4);
        int total = 0;
        for (int i = 0; i < 60; i++) total += p.Plan(1 / 60.0); // fixed 60 Hz engine
        Assert.InRange(total, 239, 240);

        p.SetFactor(2);
        total = 0;
        for (int i = 0; i < 1000; i++) total += p.Plan(0.001); // engine spinning at 1 kHz (drawing off)
        Assert.InRange(total, 119, 120);
    }

    [Fact]
    public void Pacer_DropsBacklogInsteadOfSpiralling()
    {
        var p = new TimeScalePacer();
        p.SetFactor(8);
        Assert.Equal(32, p.Plan(5.0)); // a 5 s hitch: capped at 4 frames' worth
        Assert.Equal(8, p.Plan(1 / 60.0));
        Assert.Equal(0, p.Plan(-1));
    }

    [Fact]
    public void Pacer_MaxModeUsesTheBudget()
    {
        var p = new TimeScalePacer();
        p.SetFactor(FastDev.Max);
        Assert.Equal(FastDev.MaxUpdatesPerFrame, p.Plan(1 / 60.0));
        Assert.True(TimeScalePacer.ContinueMax(0, 100, 10));  // always at least one update
        Assert.True(TimeScalePacer.ContinueMax(5, 9.9, 10));
        Assert.False(TimeScalePacer.ContinueMax(5, 10, 10));
        Assert.False(TimeScalePacer.ContinueMax(FastDev.MaxUpdatesPerFrame, 0, 10));
    }

    [Fact]
    public void RateMeter_MeasuresOverWindows()
    {
        var m = new RateMeter();
        m.Sample(0, 0, 0, 0);
        m.Sample(0.5, 100, 2, 30);
        Assert.Equal(0, m.Ups);
        m.Sample(2, 480, 10, 120);
        Assert.Equal(240, m.Ups);
        Assert.Equal(5, m.Fps);
        Assert.Equal(60, m.FramesPerSecond);
        m.Sample(2.5, 10, 0, 0); // counters reset (world reload): restart the window
        m.Sample(3.5, 70, 0, 60);
        Assert.Equal(60, m.Ups);
    }

    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void Setup_ParsesTileOps()
    {
        var ops = DevSetup.ParseTiles(J("""
            {"place_tiles":[{"x1":10,"y1":5,"x2":0,"y2":0,"clear":true},{"x1":1,"y1":2,"tile":"Stone"},{"x1":0,"y1":0,"x2":3,"y2":1,"liquid":"Water"}]}
            """));
        Assert.Equal(new TileOp(TileOpKind.Clear, 0, 0, 10, 5, null), ops[0]);
        Assert.Equal(66, ops[0].Count);
        Assert.Equal(new TileOp(TileOpKind.Tile, 1, 2, 1, 2, "Stone"), ops[1]);
        Assert.Equal(new TileOp(TileOpKind.Liquid, 0, 0, 3, 1, "water"), ops[2]);
        Assert.Empty(DevSetup.ParseTiles(J("{}")));
    }

    [Theory]
    [InlineData("""{"place_tiles":[{"x1":0,"y1":0}]}""")]                                   // no kind
    [InlineData("""{"place_tiles":[{"x1":0,"y1":0,"clear":true,"tile":"Dirt"}]}""")]        // two kinds
    [InlineData("""{"place_tiles":[{"x1":0,"y1":0,"liquid":"milk"}]}""")]
    [InlineData("""{"place_tiles":[{"x1":0,"y1":0,"x2":300,"y2":300,"clear":true}]}""")]    // too big
    [InlineData("""{"place_tiles":{"x1":0}}""")]
    public void Setup_RejectsBadTileOps(string json)
    {
        var e = Assert.Throws<CommandError>(() => DevSetup.ParseTiles(J(json)));
        Assert.Equal("bad_params", e.Code);
    }

    [Fact]
    public void Setup_ParsesItemsAndSpawns()
    {
        var items = DevSetup.ParseItems(J("""{"items":[{"name":" Wooden Bow "},{"name":"Wooden Arrow","count":500}]}"""));
        Assert.Equal(new[] { new ItemSpec("Wooden Bow", 1), new ItemSpec("Wooden Arrow", 500) }, items);
        Assert.Throws<CommandError>(() => DevSetup.ParseItems(J("""{"items":[{"name":"x","count":0}]}""")));
        var sp = DevSetup.ParseSpawns(J("""{"spawn":[{"npc":"King Slime","x":5,"y":-3}]}"""));
        Assert.Equal(new SpawnSpec("King Slime", 5, -3), Assert.Single(sp));
    }

    [Theory]
    [InlineData("dawn", true, 0)]
    [InlineData("noon", true, 27000)]
    [InlineData("dusk", false, 0)]
    [InlineData("midnight", false, 16200)]
    [InlineData("4:00", false, 30600)]
    [InlineData("13:15", true, 31500)]
    public void Setup_Time(string s, bool day, double time)
    {
        Assert.True(DevSetup.TryParseTime(s, out var d, out var t));
        Assert.Equal(day, d);
        Assert.Equal(time, t);
        Assert.Equal(s switch { "dawn" => "day 4:30 AM", "noon" => "day 12:00 PM", "dusk" => "night 7:30 PM",
            "midnight" => "night 12:00 AM", "4:00" => "night 4:00 AM", _ => "day 1:15 PM" }, LlmPlayer.Core.Observe.GameClock.Format(d, t));
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("noonish")]
    [InlineData("12")]
    public void Setup_BadTime(string s) => Assert.False(DevSetup.TryParseTime(s, out _, out _));
}
