namespace DriveGram.Sync;

/// <summary>
/// A request, kept in the control record, to move the write lease to another PC.
/// State: "asked" (waiting for the holder) → "finishing" (approved; the holder uploads what it still has,
/// reporting Pending every few seconds) → "done" (the lease now names the requester), or "refused".
/// Older versions keep the field untouched (unknown fields survive rewrites) and simply never answer:
/// the requester then takes the drive when its wait runs out.
/// </summary>
/// <summary>Another PC's request is being served right now.</summary>
public sealed class HandoverBusyException(string other) : Exception($"{other} already asked for the drive.")
{
    public string Other { get; } = other;
}

public sealed record HandoverRequest(string Dev, string Name, long At, string State = "asked", int Pending = 0, long Updated = 0);

public sealed partial class SyncService
{
    /// <summary>How long a request waits for an answer before the requester takes the drive anyway.</summary>
    public static readonly TimeSpan HandoverAnswerTime = TimeSpan.FromSeconds(60);
    /// <summary>An approved handover whose holder stopped reporting progress this long is considered dead.</summary>
    public static readonly TimeSpan HandoverStall = TimeSpan.FromMinutes(2);

    /// <summary>Raised on the writing PC when another PC asks for the drive (once per request).</summary>
    public event Action<HandoverRequest>? HandoverRequested;

    /// <summary>True on the writing PC while it hands the drive over: the drive takes no new changes, but
    /// still uploads and journals what it has.</summary>
    public bool HandingOver { get; private set; }

    long _surfacedRequest;
    DateTime _nextHandoverCheck;

    async Task CheckHandoverAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow < _nextHandoverCheck || HandingOver) return;
        _nextHandoverCheck = DateTime.UtcNow.AddSeconds(10);
        var r = (await ReadControlAsync(_remote, ct))?.Handover;
        if (r is not { State: "asked" } || r.Dev == _db.DeviceId || r.At == _surfacedRequest) return;
        if (NowUnix - r.At > HandoverAnswerTime.TotalSeconds * 2) return; // stale
        _surfacedRequest = r.At;
        Log.Info($"handover: '{r.Name}' asks for the drive");
        try { HandoverRequested?.Invoke(r); }
        catch (Exception ex) { Log.Warn($"handover listener failed: {ex.Message}"); }
    }

    // ── on the PC that wants the drive ───────────────────────────────────────
    /// <summary>Ask the PC that writes for the drive. Fails if another request is already being served.</summary>
    public async Task<HandoverRequest> RequestHandoverAsync(CancellationToken ct)
    {
        var mine = new HandoverRequest(_db.DeviceId, _o.DeviceName, NowUnix, Updated: NowUnix);
        string? busy = null;
        await UpdateControlAsync(c =>
        {
            if (c.Handover is { } other && other.Dev != _db.DeviceId && other.State is "asked" or "finishing"
                && NowUnix - Math.Max(other.At, other.Updated) < HandoverStall.TotalSeconds)
            {
                busy = other.Name;
                return c;
            }
            busy = null;
            return c with { Handover = mine };
        }, ct);
        if (busy is not null) throw new HandoverBusyException(busy);
        Log.Info("handover: asked for the drive");
        return mine;
    }

    /// <summary>Where a request stands (re-asserting it if the holder's heartbeat overwrote it meanwhile).</summary>
    public async Task<(HandoverRequest? Request, LeaseInfo? Lease)> HandoverStatusAsync(HandoverRequest mine, CancellationToken ct)
    {
        var c = (await ReadControlAsync(_remote, ct))!;
        if (c.Lease?.Dev == _db.DeviceId) return (c.Handover, c.Lease);
        if (c.Handover?.At != mine.At || c.Handover.Dev != mine.Dev)
        {
            // a rewrite by the other PC that read the record just before our request: ask again (same request)
            c = await UpdateControlAsync(x => x.Handover is null || x.Handover.State is "done" or "refused" ? x with { Handover = mine } : x, ct);
        }
        return (c.Handover, c.Lease);
    }

    /// <summary>The drive was handed to this PC (or the user decided to take it): become the writer.</summary>
    public async Task TakeDriveAsync(bool force, CancellationToken ct)
    {
        await TryAcquireLeaseAsync(force, ct);
        if (Writable)
        {
            await UpdateControlAsync(c => c.Handover?.Dev == _db.DeviceId ? c with { Handover = null } : c, ct);
            Log.Info(force ? "handover: took the drive" : "handover: received the drive");
        }
    }

    public async Task CancelHandoverAsync(HandoverRequest mine, CancellationToken ct) =>
        await UpdateControlAsync(c => c.Handover?.At == mine.At && c.Handover.Dev == mine.Dev ? c with { Handover = null } : c, ct);

    // ── on the PC that writes ────────────────────────────────────────────────
    public async Task RefuseHandoverAsync(HandoverRequest r, CancellationToken ct)
    {
        await UpdateControlAsync(c => c.Handover?.At == r.At ? c with { Handover = r with { State = "refused", Updated = NowUnix } } : c, ct);
        Log.Info($"handover: refused '{r.Name}'");
    }

    /// <summary>Approved: from now on the drive takes no new changes here (the file system asks).</summary>
    public async Task BeginHandoverAsync(HandoverRequest r, int pending, CancellationToken ct)
    {
        HandingOver = true;
        WritableChanged?.Invoke();
        await ReportHandoverAsync(r, pending, ct);
        Log.Info($"handover: approved for '{r.Name}', {pending} files still uploading");
    }

    public Task ReportHandoverAsync(HandoverRequest r, int pending, CancellationToken ct) =>
        UpdateControlAsync(c => c.Handover?.At == r.At
            ? c with { Handover = r with { State = "finishing", Pending = pending, Updated = NowUnix } } : c, ct);

    /// <summary>Everything is uploaded and journaled: write the requester into the lease. False if the
    /// requester (or someone else) already took the drive, or something is still unsent.</summary>
    public async Task<bool> CompleteHandoverAsync(HandoverRequest r, CancellationToken ct)
    {
        await FlushAsync(ct);
        if (_db.OutboxCount() > 0) return false;
        bool handed = false;
        await UpdateControlAsync(c =>
        {
            handed = c.Lease?.Dev == _db.DeviceId;
            if (!handed) return c;
            return c with
            {
                Lease = new LeaseInfo(r.Dev, r.Name, NowUnix + (long)_o.LeaseDuration.TotalSeconds),
                Handover = r with { State = "done", Pending = 0, Updated = NowUnix },
            };
        }, ct);
        HandingOver = false;
        SetWritable(false, r.Name);
        Log.Info($"handover: the drive now belongs to '{r.Name}'");
        return handed;
    }

    /// <summary>The handover was abandoned (the requester took the drive, or it failed): back to normal.</summary>
    public void EndHandover()
    {
        if (!HandingOver) return;
        HandingOver = false;
        WritableChanged?.Invoke();
    }
}
