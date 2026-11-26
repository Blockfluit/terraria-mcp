using LlmPlayer.Core.Control;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class DevAutoloadTests
{
    [Fact]
    public void Parse_MinimalRequest()
    {
        var r = DevAutoload.Parse("{\"player\":\"Hero\",\"world\":\"Big World\"}", out var error);

        Assert.Equal("", error);
        Assert.Equal(new AutoloadRequest("Hero", "Big World"), r);
    }

    [Fact]
    public void Parse_AllFields_TrimsNames()
    {
        var r = DevAutoload.Parse(
            "{\"player\":\" Hero \",\"world\":\"W\",\"playerFile\":\"Hero.plr\",\"worldFile\":\"W.wld\",\"createdUtc\":\"2026-11-26T10:00:00Z\"}",
            out var error);

        Assert.Equal("", error);
        Assert.Equal("Hero", r!.Player);
        Assert.Equal("Hero.plr", r.PlayerFile);
        Assert.Equal("W.wld", r.WorldFile);
        Assert.Equal(new DateTime(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc), r.CreatedUtc);
        Assert.Equal(DateTimeKind.Utc, r.CreatedUtc!.Value.Kind);
    }

    [Theory]
    [InlineData("", "not valid JSON")]
    [InlineData("[1]", "expected a JSON object")]
    [InlineData("{\"world\":\"W\"}", "missing \"player\"")]
    [InlineData("{\"player\":\"P\"}", "missing \"world\"")]
    [InlineData("{\"player\":\"  \",\"world\":\"W\"}", "\"player\" is empty")]
    [InlineData("{\"player\":3,\"world\":\"W\"}", "\"player\" must be a string")]
    [InlineData("{\"player\":\"P\",\"world\":\"W\",\"createdUtc\":\"yesterday\"}", "not a date")]
    public void Parse_Rejects(string json, string expected)
    {
        Assert.Null(DevAutoload.Parse(json, out var error));
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var r = new AutoloadRequest("Hé \"ro\"", "W", "x.plr", "y.wld", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        Assert.Equal(r, DevAutoload.Parse(DevAutoload.Serialize(r), out _));
    }

    [Fact]
    public void Serialize_OmitsMissingOptionals()
        => Assert.Equal("{\"player\":\"P\",\"world\":\"W\"}", DevAutoload.Serialize(new AutoloadRequest("P", "W")));

    [Fact]
    public void IsFresh_ByAge()
    {
        var now = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
        AutoloadRequest At(DateTime? t) => new("P", "W", CreatedUtc: t);

        Assert.True(DevAutoload.IsFresh(At(null), now));
        Assert.True(DevAutoload.IsFresh(At(now.AddMinutes(-9)), now));
        Assert.False(DevAutoload.IsFresh(At(now.AddMinutes(-11)), now));
        Assert.True(DevAutoload.IsFresh(At(now.AddSeconds(30)), now));
        Assert.False(DevAutoload.IsFresh(At(now.AddMinutes(5)), now));
    }

    private static readonly SaveEntry[] Saves =
    {
        new("Hero", "Hero.plr"),
        new("hero", "hero_2.plr"),
        new("Twin", "Twin.plr"),
        new("Twin", "Twin2.plr"),
        new("Mage", "Mage.plr"),
    };

    [Fact]
    public void Pick_ExactNameWinsOverCaseInsensitive()
    {
        Assert.Equal(0, DevAutoload.Pick(Saves, "Hero", null, "player", out _));
        Assert.Equal(1, DevAutoload.Pick(Saves, "hero", null, "player", out _));
    }

    [Fact]
    public void Pick_CaseInsensitiveWhenNoExact()
        => Assert.Equal(4, DevAutoload.Pick(Saves, "MAGE", null, "player", out _));

    [Fact]
    public void Pick_DuplicateName_NeedsFile()
    {
        Assert.Equal(-1, DevAutoload.Pick(Saves, "Twin", null, "player", out var error));
        Assert.Contains("2 players are named \"Twin\"", error);
        Assert.Equal(3, DevAutoload.Pick(Saves, "Twin", "twin2.PLR", "player", out _));
    }

    [Fact]
    public void Pick_FileNameWinsEvenIfNameChanged()
        => Assert.Equal(2, DevAutoload.Pick(Saves, "Renamed", "Twin.plr", "player", out _));

    [Fact]
    public void Pick_UnknownFileFallsBackToName()
        => Assert.Equal(4, DevAutoload.Pick(Saves, "Mage", "Gone.plr", "player", out _));

    [Fact]
    public void Pick_NotFound_ListsNames()
    {
        Assert.Equal(-1, DevAutoload.Pick(Saves, "Nobody", null, "world", out var error));
        Assert.StartsWith("no world named \"Nobody\" (have: Hero, hero", error);
        Assert.Equal(-1, DevAutoload.Pick(Array.Empty<SaveEntry>(), "X", null, "world", out error));
        Assert.Equal("no world named \"X\"", error);
    }

    [Fact]
    public void GameModeMismatch()
    {
        Assert.Null(DevAutoload.GameModeMismatch(false, false));
        Assert.Null(DevAutoload.GameModeMismatch(true, true));
        Assert.NotNull(DevAutoload.GameModeMismatch(true, false));
        Assert.NotNull(DevAutoload.GameModeMismatch(false, true));
    }

    [Theory]
    [InlineData("The_Weird_Fjord")]
    [InlineData("The_Weird_Fjord.wld")]
    [InlineData("the_weird_fjord")]
    public void Pick_AcceptsTheFileNameAsName(string given)
    {
        // Seen on the first real run: --world The_Weird_Fjord (the file name) didn't match "The Weird Fjord".
        var saves = new[] { new SaveEntry("The Weird Fjord", "The_Weird_Fjord.wld"), new SaveEntry("Other", "Other.wld") };
        Assert.Equal(0, DevAutoload.Pick(saves, given, null, "world", out var err));
        Assert.Equal("", err);
    }
}
