namespace DriveGram.Remote;

public sealed record JournalItem(int Id, string Name, long Size = 0);

/// <summary>This account / folder holds no DriveGram drive yet (first run: `drivegram setup`).</summary>
public sealed class NoDriveException() : Exception("No DriveGram drive yet. Run `drivegram setup`.");

/// <summary>This PC knew a drive on this account, but its channels are gone from Telegram (deleted).
/// Never silently replaced by a new empty drive: the user must confirm.</summary>
public sealed class DriveVanishedException() : StopRetryingException("The drive's Telegram channels no longer exist (they were deleted).");

/// <summary>This PC has no signed-in Telegram session (or it was revoked from another device).</summary>
public sealed class NotSignedInException() : StopRetryingException("Telegram is not signed in on this PC. Open DriveGram setup (or run `drivegram login`).");

/// <summary>
/// Where the bytes live. Two logical areas:
///  - Data: encrypted chunks (one stored message per chunk), addressed by message id, read in
///    fixed 1 MiB windows (Telegram's upload.getFile alignment rule).
///  - Journal: small encrypted records (op batches, snapshots) in id order, plus one editable
///    "control" text (vault header, lease, snapshot pointer).
/// Implementations: TelegramRemote (real), FolderRemote (tests / offline trial).
/// </summary>
public interface IRemote : IAsyncDisposable
{
    const int WindowSize = 1024 * 1024;

    Task<int> UploadChunkAsync(string name, byte[] payload, CancellationToken ct);

    /// <summary>Bytes handed to the server so far, including parts of uploads still in progress.</summary>
    long BytesSent => 0;

    /// <summary>When the control record was last changed, by the SERVER's clock (from the last read); null if unknown.</summary>
    DateTime? ControlEditedAtUtc => null;

    /// <summary>When the server asked this account to wait (rate limit), until when; null if not.</summary>
    DateTime? WaitingUntil => null;

    /// <summary>Bytes [window*1MiB, (window+1)*1MiB) of a stored chunk message (shorter at the end).</summary>
    Task<byte[]> ReadWindowAsync(int msg, int window, CancellationToken ct);

    Task DeleteChunksAsync(IReadOnlyList<int> msgs, CancellationToken ct);

    Task<int> AppendJournalAsync(string name, byte[] payload, CancellationToken ct);

    /// <summary>Journal items with id &gt; afterId, ascending.</summary>
    Task<IReadOnlyList<JournalItem>> ListJournalAsync(int afterId, CancellationToken ct);

    Task<byte[]> ReadJournalAsync(int id, CancellationToken ct);

    Task<string?> ReadControlAsync(CancellationToken ct);

    Task WriteControlAsync(string text, CancellationToken ct);
}
