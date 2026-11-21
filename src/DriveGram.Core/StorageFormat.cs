namespace DriveGram;

/// <summary>
/// The version of DriveGram's stored formats (control record, journal entries, ops, snapshots).
///
/// FORWARD COMPATIBILITY — the one thing that cannot be added later: an OLD client must recognise
/// that a NEWER one changed the format, and stop safely instead of crashing in a loop or, worse,
/// writing data the newer format does not expect. So every client, from this version on, checks:
///   - ControlState.MinReader  > Current  → refuses to open the drive ("update DriveGram");
///   - ControlState.MinWriter  > Current  → opens it read-only;
///   - a journal entry / snapshot with V > Current, or an op it cannot parse → stops applying the
///     journal at that point (nothing skipped, nothing advanced) and goes read-only.
/// A future version that changes a format raises Current and, when old clients must not write
/// (or read), raises MinWriter (or MinReader) in the control record.
/// </summary>
public static class StorageFormat
{
    public const int Current = 1;
}

/// <summary>The drive was written by a newer DriveGram whose format this version does not understand.</summary>
public sealed class NeedsUpdateException(string? detail = null)
    : Exception("This drive was written by a newer version of DriveGram. Update DriveGram to open it." + (detail is null ? "" : $" ({detail})"));

/// <summary>An error that retrying cannot fix: Retry.ForeverAsync rethrows it at once.</summary>
public class StopRetryingException(string message) : Exception(message);

/// <summary>Another PC took the drive's write lease (e.g. this one was offline longer than the lease).</summary>
public sealed class LeaseLostException() : StopRetryingException("The write lease was taken by another PC.");
