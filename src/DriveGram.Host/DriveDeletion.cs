using DriveGram.Index;
using DriveGram.Remote;
using DriveGram.Sync;

namespace DriveGram.Host;

/// <summary>What deleting a drive would destroy, shown to the user before they confirm.</summary>
public sealed record DeletionPreview(
    long Files, long Folders, long Bytes, int Trashed, int PendingUploads,
    bool CanDeleteRemote,      // this account owns the channels (or it is a local trial folder)
    bool RemoteExists,         // the channels / folder are still there
    string? OpenElsewhere);    // another PC currently holds the write lease

/// <summary>
/// Permanent deletion of a whole drive: its storage (the two Telegram channels, or a trial folder),
/// and everything this PC keeps for it. Irreversible — the UI must show Preview and get an explicit
/// confirmation first. A drive this account does not own (e.g. a backup admin) cannot be deleted,
/// only removed from this PC.
/// </summary>
public static class DriveDeletion
{
    /// <param name="db">the mounted drive's index, or null to open the saved one</param>
    public static async Task<DeletionPreview> PreviewAsync(AppState state, IndexDb? db, CancellationToken ct = default)
    {
        long files = 0, folders = 0, bytes = 0;
        int trashed = 0, pending = 0;
        string device = "";
        IndexDb? opened = null;
        try
        {
            if (db is null && File.Exists(state.IndexPath)) db = opened = IndexDb.Open(state.IndexPath);
            if (db is not null)
            {
                (files, folders) = db.Counts();
                bytes = db.UsedBytes();
                trashed = db.Trashed().Count;
                pending = db.PendingNodes().Count;
                device = db.DeviceId;
            }
        }
        finally { opened?.Dispose(); }

        if (state.Folder is not null)
        {
            bool exists = File.Exists(Path.Combine(state.Folder, "control.json"));
            return new DeletionPreview(files, folders, bytes, trashed, pending, true, exists, null);
        }
        if (state.Config.Channels is not { } ch)
            return new DeletionPreview(files, folders, bytes, trashed, pending, false, false, null);

        using var lease = TelegramAccounts.Acquire(state);
        var (remoteExists, owner) = await TelegramRemote.ChannelsStatusAsync(lease.Client, ch);
        string? elsewhere = null;
        if (remoteExists)
        {
            try
            {
                await using var remote = await TelegramRemote.OpenAsync(lease.Client, ch, lease.Up, lease.Down, ownsClient: false);
                var lease2 = (await SyncService.ReadControlAsync(remote, ct))?.Lease;
                if (lease2 is not null && lease2.Dev != device && lease2.Until > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                    elsewhere = lease2.Name;
            }
            catch (Exception ex) { Log.Warn($"delete preview: {ex.Message}"); }
        }
        return new DeletionPreview(files, folders, bytes, trashed, pending, owner, remoteExists, elsewhere);
    }

    /// <summary>Files not yet uploaded and changes not yet sent, from the saved index (drive unmounted).
    /// Removing the profile deletes these for good — they exist nowhere else.</summary>
    public static (int PendingFiles, int UnsentChanges) LocalOnly(AppState state)
    {
        if (!File.Exists(state.IndexPath)) return (0, 0);
        try
        {
            using var db = IndexDb.Open(state.IndexPath);
            return (db.PendingNodes().Count, db.OutboxCount());
        }
        catch (Exception ex)
        {
            Log.Warn($"cannot read the index of {state.Id}: {ex.Message}");
            return (-1, -1); // unknown: callers warn as if there were local changes
        }
    }

    /// <summary>Delete the drive's storage for good. The drive must already be unmounted.</summary>
    public static async Task DeleteStorageAsync(AppState state)
    {
        if (state.Folder is not null)
        {
            // only ever delete a folder that is recognisably a DriveGram trial drive
            bool ours = File.Exists(Path.Combine(state.Folder, "control.json"))
                        && Directory.Exists(Path.Combine(state.Folder, "journal"))
                        && Directory.Exists(Path.Combine(state.Folder, "data"));
            if (!ours) return;
            // only DriveGram's own entries: the folder may be the user's (Desktop, a disk root…)
            var ownFile = new System.Text.RegularExpressions.Regex(@"^\d{10}_.+$");
            foreach (var f in Directory.GetFiles(Path.Combine(state.Folder, "journal")).Where(f => ownFile.IsMatch(Path.GetFileName(f))))
                File.Delete(f);
            foreach (var f in Directory.GetFiles(Path.Combine(state.Folder, "data"), "*.bin"))
                if (int.TryParse(Path.GetFileNameWithoutExtension(f), out _)) File.Delete(f);
            foreach (var dir in new[] { "journal", "data" })
                try { Directory.Delete(Path.Combine(state.Folder, dir)); } catch (IOException) { } // only if empty now
            foreach (var name in new[] { "control.json", "next-id", "control.json.tmp", "next-id.tmp" })
                File.Delete(Path.Combine(state.Folder, name));
            try { if (!Directory.EnumerateFileSystemEntries(state.Folder).Any()) Directory.Delete(state.Folder); } catch (IOException) { }
            return;
        }
        if (state.Config.Channels is not { } ch) return;
        using var lease = TelegramAccounts.Acquire(state);
        await TelegramRemote.DeleteChannelsAsync(lease.Client, ch);
        Log.Info($"drive '{state.Config.VolumeLabel}' ({state.Config.DriveId}) deleted permanently");
    }
}
