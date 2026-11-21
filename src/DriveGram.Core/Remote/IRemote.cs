namespace DriveGram.Remote;

public sealed record JournalItem(int Id, string Name);

/// <summary>This account / folder holds no DriveGram drive yet (first run: `drivegram setup`).</summary>
public sealed class NoDriveException() : Exception("No DriveGram drive yet. Run `drivegram setup`.");

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
