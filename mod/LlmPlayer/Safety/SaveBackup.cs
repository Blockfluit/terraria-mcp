using System.Reflection;
using LlmPlayer.Config;
using LlmPlayer.Core.Control;
using Terraria;
using Terraria.IO;
using Terraria.Utilities;

namespace LlmPlayer.Safety;

/// <summary>
/// Once per world session, before the AI first gets control, copy the last-saved player and world files
/// (.plr/.wld plus .bak and tModLoader's .tplr/.twld side files) to
/// <c>&lt;Main.SavePath&gt;/LlmPlayerBackups/&lt;yyyyMMdd-HHmmss&gt;/</c>, keeping the newest <see cref="LlmConfig.BackupKeep"/>.
///
/// Deviation from the spec (noted in the plan): it copies the files as last saved instead of forcing a save
/// mid-game — safer for save integrity, and the backup is the state before the AI ever acted.
/// Runs synchronously on the main thread (the files are small). It holds vanilla's private WorldFile.IOLock
/// (TryEnter, like vanilla's own SaveWorld) so a background world save can't be copied half-written; if a save
/// is in progress it fails and the caller refuses the handover ("try again").
/// </summary>
public static class SaveBackup
{
    private static bool _doneThisSession;

    private static readonly object? WorldIoLock =
        typeof(WorldFile).GetField("IOLock", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);

    public static string BackupRoot => Path.Combine(Main.SavePath, "LlmPlayerBackups");

    /// <summary>Call when a world is entered or left so the next session backs up again.</summary>
    public static void ResetSession() => _doneThisSession = false;

    /// <summary>True if a backup exists for this session (made now or earlier). On false, <paramref name="error"/> says why.</summary>
    public static bool EnsureForSession(out string error)
    {
        error = "";
        if (_doneThisSession) return true;
        try
        {
            if (WorldIoLock != null && !Monitor.TryEnter(WorldIoLock))
            {
                error = "the world is being saved right now; try again in a moment";
                return false;
            }
            try
            {
                var dir = CopyNow();
                LlmPlayerMod.Log($"save backup written to {dir}");
            }
            finally
            {
                if (WorldIoLock != null) Monitor.Exit(WorldIoLock);
            }
            Rotate();
            _doneThisSession = true;
            return true;
        }
        catch (Exception e)
        {
            LlmPlayerMod.Log($"save backup failed: {e}");
            error = e.Message;
            return false;
        }
    }

    private static string CopyNow()
    {
        var player = Main.ActivePlayerFileData ?? throw new InvalidOperationException("no active player file");
        var world = Main.ActiveWorldFileData ?? throw new InvalidOperationException("no active world file");

        var dir = Path.Combine(BackupRoot, BackupRotation.FolderName(DateTime.Now));
        Directory.CreateDirectory(dir);

        CopySet(player, ".tplr", dir);
        CopySet(world, ".twld", dir);
        return dir;
    }

    private static void CopySet(FileData file, string modExtension, string dir)
    {
        if (string.IsNullOrEmpty(file.Path)) throw new InvalidOperationException($"{file.Name} has no save path");
        var cloud = file.IsCloudSave;
        if (!FileUtilities.Exists(file.Path, cloud))
            throw new FileNotFoundException($"save file not found: {file.Path}");

        foreach (var src in BackupRotation.SaveFileSet(file.Path, modExtension))
        {
            if (!FileUtilities.Exists(src, cloud)) continue;
            // ReadAllBytes handles both local and Steam Cloud saves.
            File.WriteAllBytes(Path.Combine(dir, Path.GetFileName(src)), FileUtilities.ReadAllBytes(src, cloud));
        }
    }

    private static void Rotate()
    {
        var root = BackupRoot;
        var names = Directory.GetDirectories(root).Select(Path.GetFileName).OfType<string>();
        foreach (var name in BackupRotation.FoldersToDelete(names, LlmConfig.Current.BackupKeep))
        {
            try
            {
                Directory.Delete(Path.Combine(root, name), recursive: true);
            }
            catch (Exception e)
            {
                LlmPlayerMod.Log($"could not delete old backup {name}: {e.Message}");
            }
        }
    }
}
