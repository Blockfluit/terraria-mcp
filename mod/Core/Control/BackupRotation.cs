using System.Globalization;

namespace LlmPlayer.Core.Control;

/// <summary>Naming and rotation of timestamped save-backup folders (pure; the mod does the file IO).</summary>
public static class BackupRotation
{
    public const string Format = "yyyyMMdd-HHmmss";

    public static string FolderName(DateTime now) => now.ToString(Format, CultureInfo.InvariantCulture);

    public static bool IsBackupFolder(string name) =>
        DateTime.TryParseExact(name, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>
    /// Backup folders to delete so only the newest <paramref name="keep"/> remain (at least 1 is always kept).
    /// Folders whose names aren't backup timestamps are never returned. Newest-first order.
    /// </summary>
    public static IReadOnlyList<string> FoldersToDelete(IEnumerable<string> folderNames, int keep) =>
        folderNames.Where(IsBackupFolder)
            .OrderByDescending(n => n, StringComparer.Ordinal)
            .Skip(Math.Max(1, keep))
            .ToList();

    /// <summary>The save file plus its vanilla .bak and tModLoader side file (and its .bak).</summary>
    public static IReadOnlyList<string> SaveFileSet(string path, string modExtension)
    {
        var side = Path.ChangeExtension(path, modExtension);
        return new[] { path, path + ".bak", side, side + ".bak" };
    }
}
