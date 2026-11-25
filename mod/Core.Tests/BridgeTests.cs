using System.Text.Json;
using LlmPlayer.Core.Bridge;

namespace LlmPlayer.Core.Tests;

public class WireProtocolTests
{
    [Fact]
    public void Result_SerializesEnumsAsLowercaseStrings()
    {
        var line = WireProtocol.Result(1, new LlmPlayer.Core.Tasks.TaskReport("move_to", LlmPlayer.Core.Tasks.GameTaskStatus.Running, null, null));
        Assert.Contains("\"status\":\"running\"", line);
    }

    [Fact]
    public void ParsesRequest()
    {
        var r = WireProtocol.ParseRequest("{\"id\":3,\"method\":\"get_map\",\"params\":{\"radius\":5}}\r");
        Assert.True(r.Ok);
        Assert.Equal(3, r.Id);
        Assert.Equal("get_map", r.Method);
        Assert.Equal(5, r.Params.GetProperty("radius").GetInt32());
    }

    [Fact]
    public void MissingParams_BecomesEmptyObject()
    {
        var r = WireProtocol.ParseRequest("{\"id\":1,\"method\":\"ping\"}");
        Assert.True(r.Ok);
        Assert.Equal(JsonValueKind.Object, r.Params.ValueKind);
    }

    [Fact]
    public void ParamsSurviveDocumentDisposal()
    {
        var r = WireProtocol.ParseRequest("{\"id\":1,\"method\":\"x\",\"params\":{\"a\":\"b\"}}");
        GC.Collect();
        Assert.Equal("b", r.Params.GetProperty("a").GetString());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"method\":\"ping\"}")]
    public void Malformed_GivesErrorWithoutId(string line)
    {
        var r = WireProtocol.ParseRequest(line);
        Assert.False(r.Ok);
        Assert.Null(r.Id);
        Assert.NotNull(r.ErrorLine);
        using var doc = JsonDocument.Parse(r.ErrorLine!);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("id").ValueKind);
        Assert.Equal("bad_params", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("{\"id\":7}")]
    [InlineData("{\"id\":7,\"method\":5}")]
    [InlineData("{\"id\":7,\"method\":\"x\",\"params\":3}")]
    public void BadShapeWithId_ErrorKeepsId(string line)
    {
        var r = WireProtocol.ParseRequest(line);
        Assert.False(r.Ok);
        Assert.Equal(7, r.Id);
        using var doc = JsonDocument.Parse(r.ErrorLine!);
        Assert.Equal(7, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("bad_params", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Result_IsCamelCaseSingleLine()
    {
        var line = WireProtocol.Result(4, new { LifeMax = 100, InWorld = true });
        Assert.Equal("{\"id\":4,\"result\":{\"lifeMax\":100,\"inWorld\":true}}", line);
    }

    [Fact]
    public void NullResult_IsEmptyObject()
    {
        Assert.Equal("{\"id\":1,\"result\":{}}", WireProtocol.Result(1, null));
    }

    [Fact]
    public void Error_Shape()
    {
        Assert.Equal("{\"id\":2,\"error\":{\"code\":\"no_path\",\"message\":\"blocked\"}}",
            WireProtocol.Error(2, "no_path", "blocked"));
    }

    [Fact]
    public void Event_FlattensPayloadAfterEventName()
    {
        var line = WireProtocol.Event("control_changed", new { Driver = "user", Reason = "user_input" });
        Assert.Equal("{\"event\":\"control_changed\",\"driver\":\"user\",\"reason\":\"user_input\"}", line);
    }

    [Fact]
    public void Event_NullPayload()
    {
        Assert.Equal("{\"event\":\"x\"}", WireProtocol.Event("x", null));
    }
}

public class CommandRegistryTests
{
    private static JsonElement P(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Dispatch_CallsHandler()
    {
        var reg = new CommandRegistry();
        reg.Register("ping", _ => new { Pong = true }, isAction: false);
        Assert.Equal("{\"id\":1,\"result\":{\"pong\":true}}", reg.Handle(1, "ping", P("{}")));
    }

    [Fact]
    public void UnknownMethod()
    {
        var reg = new CommandRegistry();
        var ex = Assert.Throws<CommandError>(() => reg.Dispatch("nope", P("{}")));
        Assert.Equal("unknown_method", ex.Code);
        var line = reg.Handle(9, "nope", P("{}"));
        Assert.Contains("\"code\":\"unknown_method\"", line);
        Assert.Contains("\"id\":9", line);
    }

    [Fact]
    public void ParamHelpers_ThrowBadParams()
    {
        var reg = new CommandRegistry();
        reg.Register("m", p => new { X = Params.Int(p, "x") }, isAction: true);
        Assert.Contains("\"code\":\"bad_params\"", reg.Handle(1, "m", P("{}")));
        Assert.Contains("\"code\":\"bad_params\"", reg.Handle(1, "m", P("{\"x\":\"a\"}")));
        Assert.Contains("\"code\":\"bad_params\"", reg.Handle(1, "m", P("{\"x\":1.5}")));
        Assert.Equal("{\"id\":1,\"result\":{\"x\":3}}", reg.Handle(1, "m", P("{\"x\":3}")));
    }

    [Fact]
    public void OptionalParams_UseDefaults()
    {
        var p = P("{\"b\":false,\"s\":\"hi\",\"f\":0.5}");
        Assert.Equal(20, Params.Int(p, "radius", 20));
        Assert.False(Params.Bool(p, "b", true));
        Assert.True(Params.Bool(p, "missing", true));
        Assert.Equal("hi", Params.String(p, "s"));
        Assert.Null(Params.StringOrNull(p, "missing"));
        Assert.Equal(0.5f, Params.Float(p, "f", 1f));
        Assert.True(Params.Has(p, "s"));
        Assert.False(Params.Has(p, "zzz"));
    }

    [Fact]
    public void NullParamValue_TreatedAsMissing()
    {
        var p = P("{\"radius\":null}");
        Assert.Equal(20, Params.Int(p, "radius", 20));
        Assert.False(Params.Has(p, "radius"));
    }

    [Fact]
    public void Guard_SeesSpecAndCanReject()
    {
        var reg = new CommandRegistry();
        reg.Register("act", _ => null, isAction: true);
        reg.Register("obs", _ => null, isAction: false, requiresWorld: false);
        CommandSpec? seen = null;
        reg.Guard = spec =>
        {
            seen = spec;
            if (spec.IsAction) throw new CommandError("user_has_control", "user is driving");
        };
        Assert.Contains("\"code\":\"user_has_control\"", reg.Handle(1, "act", P("{}")));
        Assert.Equal("{\"id\":2,\"result\":{}}", reg.Handle(2, "obs", P("{}")));
        Assert.False(seen!.RequiresWorld);
    }

    [Fact]
    public void HandlerCrash_IsInternal()
    {
        var reg = new CommandRegistry();
        reg.Register("boom", _ => throw new InvalidOperationException("kaboom"), isAction: false);
        var line = reg.Handle(1, "boom", P("{}"));
        Assert.Contains("\"code\":\"internal\"", line);
        Assert.Contains("kaboom", line);
    }

    [Fact]
    public void DuplicateRegistration_Throws()
    {
        var reg = new CommandRegistry();
        reg.Register("a", _ => null, false);
        Assert.Throws<ArgumentException>(() => reg.Register("a", _ => null, false));
    }

    [Fact]
    public void Clear_RemovesCommands()
    {
        var reg = new CommandRegistry();
        reg.Register("a", _ => null, false);
        reg.Clear();
        Assert.Contains("unknown_method", reg.Handle(1, "a", P("{}")));
    }
}
