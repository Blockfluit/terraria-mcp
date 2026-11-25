using LlmPlayer.Core.Control;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class BackupRotationTests
{
    [Fact]
    public void FolderName_IsSortableTimestamp()
        => Assert.Equal("20261125-090507", BackupRotation.FolderName(new DateTime(2026, 11, 25, 9, 5, 7)));

    [Fact]
    public void KeepsNewest_DeletesOlder()
    {
        var names = new[] { "20261125-100000", "20261120-080000", "20261125-090000", "20261101-000000" };

        var delete = BackupRotation.FoldersToDelete(names, keep: 2);

        Assert.Equal(new[] { "20261120-080000", "20261101-000000" }, delete);
    }

    [Fact]
    public void FewerThanKeep_DeletesNothing()
        => Assert.Empty(BackupRotation.FoldersToDelete(new[] { "20261125-100000" }, keep: 5));

    [Fact]
    public void IgnoresFoldersNotMatchingTimestamp()
    {
        var names = new[] { "my-notes", "20261125-100000", "20261125-0900", "20261124-100000", "x20261123-100000" };

        var delete = BackupRotation.FoldersToDelete(names, keep: 1);

        Assert.Equal(new[] { "20261124-100000" }, delete);
    }

    [Fact]
    public void KeepBelowOne_StillKeepsNewest()
        => Assert.Equal(new[] { "20261124-100000" },
            BackupRotation.FoldersToDelete(new[] { "20261124-100000", "20261125-100000" }, keep: 0));

    [Fact]
    public void SideFiles_ForPlayerAndWorld()
    {
        Assert.Equal(new[] { "/s/P.plr", "/s/P.plr.bak", "/s/P.tplr", "/s/P.tplr.bak" },
            BackupRotation.SaveFileSet("/s/P.plr", ".tplr"));
        Assert.Equal(new[] { "/s/W.wld", "/s/W.wld.bak", "/s/W.twld", "/s/W.twld.bak" },
            BackupRotation.SaveFileSet("/s/W.wld", ".twld"));
    }
}
