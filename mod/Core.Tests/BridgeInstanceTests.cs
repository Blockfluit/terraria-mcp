using System.Text.Json.Nodes;
using LlmPlayer.Core.Bridge;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class BridgeInstanceTests
{
    private static readonly string[] NoArgs = { "tModLoader.dll" };

    [Fact]
    public void ChoosePort_DefaultsToConfig()
    {
        Assert.Equal(new PortChoice(7878, PortSource.Config), BridgeInstance.ChoosePort(NoArgs, null, 7878));
        Assert.Equal(new PortChoice(9000, PortSource.Config), BridgeInstance.ChoosePort(NoArgs, "  ", 9000));
    }

    [Theory]
    [InlineData("-llmport", "7879")]
    [InlineData("-LLMPORT", "7879")]
    public void ChoosePort_CommandLineSeparateValue(string flag, string value)
    {
        var args = new[] { "tModLoader.dll", "-tmlsavedirectory", @"C:\x\1", flag, value };
        Assert.Equal(new PortChoice(7879, PortSource.CommandLine), BridgeInstance.ChoosePort(args, "7900", 7878));
    }

    [Fact]
    public void ChoosePort_CommandLineEqualsForm()
    {
        var c = BridgeInstance.ChoosePort(new[] { "x", "-llmport=7881" }, null, 7878);
        Assert.Equal(new PortChoice(7881, PortSource.CommandLine), c);
    }

    [Fact]
    public void ChoosePort_EnvironmentWhenNoArg()
    {
        Assert.Equal(new PortChoice(7890, PortSource.Environment), BridgeInstance.ChoosePort(NoArgs, " 7890 ", 7878));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("80")]
    [InlineData("70000")]
    [InlineData("-5")]
    [InlineData("")]
    public void ChoosePort_BadArgFallsBackWithWarning(string value)
    {
        var c = BridgeInstance.ChoosePort(new[] { "x", "-llmport", value }, "7885", 7878);
        Assert.Equal(7885, c.Port);
        Assert.Equal(PortSource.Environment, c.Source);
        Assert.Contains("ignoring -llmport", c.Warning);
    }

    [Fact]
    public void ChoosePort_MissingValueAtEnd()
    {
        var c = BridgeInstance.ChoosePort(new[] { "x", "-llmport" }, null, 7878);
        Assert.Equal(PortSource.Config, c.Source);
        Assert.NotNull(c.Warning);
    }

    [Fact]
    public void ChoosePort_BadEnvAndBadConfig()
    {
        var c = BridgeInstance.ChoosePort(NoArgs, "nope", 5);
        Assert.Equal(new PortChoice(7878, PortSource.Config, c.Warning), c);
        Assert.Contains("LLMPLAYER_PORT", c.Warning);
    }

    [Fact]
    public void Candidates_StopAtMaxPort()
    {
        Assert.Equal(new[] { 7878, 7879, 7880 }, BridgeInstance.Candidates(7878, 3));
        Assert.Equal(new[] { 65534, 65535 }, BridgeInstance.Candidates(65534, 10));
        Assert.Equal(10, BridgeInstance.Candidates(7878).Count());
    }

    [Fact]
    public void BindFirstFree_SkipsTakenPorts()
    {
        var taken = new HashSet<int> { 7879, 7880 };
        var tried = new List<int>();
        var p = BridgeInstance.BindFirstFree(7879, port => { tried.Add(port); return !taken.Contains(port); });
        Assert.Equal(7881, p);
        Assert.Equal(new[] { 7879, 7880, 7881 }, tried);
    }

    [Fact]
    public void BindFirstFree_NullWhenAllTaken()
    {
        Assert.Null(BridgeInstance.BindFirstFree(7878, _ => false, 3));
    }

    [Fact]
    public void SerializeStatus_FieldsForScripts()
    {
        var json = BridgeInstance.SerializeStatus(new InstanceStatus(7880, 7879, PortSource.CommandLine, 1234,
            @"C:\Users\u\AppData\Local\LlmPlayerInstances\1", new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc)));
        var o = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(7880, (int)o["port"]!);
        Assert.Equal(7879, (int)o["requestedPort"]!);
        Assert.Equal("commandline", (string)o["source"]!);
        Assert.Equal(1234, (int)o["pid"]!);
        Assert.Equal(@"C:\Users\u\AppData\Local\LlmPlayerInstances\1", (string)o["savePath"]!);
        Assert.Equal("2026-11-28T12:00:00Z", (string)o["startedUtc"]!);
    }

    [Fact]
    public void FileNames_AreDistinct()
    {
        // Both live under Main.SavePath, which -tmlsavedirectory makes per instance.
        Assert.NotEqual(BridgeInstance.StatusFileName, LlmPlayer.Core.Control.DevAutoload.FileName);
    }
}
