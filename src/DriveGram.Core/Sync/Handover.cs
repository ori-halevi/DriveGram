namespace DriveGram.Sync;

/// <summary>
/// A request, kept in the control record, to move the write lease to another PC.
/// State: "asked" (waiting for the holder) → "seen" (the holder shows its question; both sides count the
/// answer time from here) → "finishing" (approved; the holder uploads what it still has, reporting Pending
/// every few seconds) → "done" (the lease now names the requester), or "refused" / "failed".
/// Older versions keep the field untouched (unknown fields survive rewrites) and simply never answer:
/// the requester then takes the drive when its wait runs out.
/// </summary>
/// <summary>Another PC's request is being served right now.</summary>
public sealed class HandoverBusyException(string other) : Exception($"{other} already asked for the drive.")
{
    public string Other { get; } = other;
}

public sealed record HandoverRequest(string Dev, string Name, long At, string State = "asked", int Pending = 0, long Updated = 0, string Id = "");

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

    string? _surfacedRequest;
    DateTime _nextHandoverCheck;

    async Task CheckHandoverAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow < _nextHandoverCheck || HandingOver) return;
        _nextHandoverCheck = DateTime.UtcNow.AddSeconds(10);
        var r = (await ReadControlAsync(_remote, ct))?.Handover;
        if (r is not { State: "asked" } || r.Dev == _db.DeviceId || r.Id == _surfacedRequest) return;
        _surfacedRequest = r.Id;
        Log.Info($"handover: '{r.Name}' asks for the drive");
        // tell the asking PC its question is on screen: from now on both count the same answer time
        await UpdateControlAsync(c => c.Handover?.Id == r.Id && c.Handover.State == "asked"
            ? c with { Handover = c.Handover with { State = "seen", Updated = NowUnix } } : c, ct);
        try { HandoverRequested?.Invoke(r); }
        catch (Exception ex) { Log.Warn($"handover listener failed: {ex.Message}"); }
    }

    /// <summary>The question could not be shown (another one is open): offer it again at the next check.</summary>
    public void ForgetSurfacedRequest() => _surfacedRequest = null;

    /// <summary>Is this request still waiting for this PC's answer (not cancelled, not taken over)?</summary>
    public async Task<bool> RequestStillOpenAsync(HandoverRequest r, CancellationToken ct)
    {
        var c = await ReadControlAsync(_remote, ct);
        return c?.Lease?.Dev == _db.DeviceId && c.Handover?.Id == r.Id && c.Handover.State is "asked" or "seen";
    }

    // ── on the PC that wants the drive ───────────────────────────────────────
    /// <summary>Ask the PC that writes for the drive. Fails if another request is already being served.</summary>
    public async Task<HandoverRequest> RequestHandoverAsync(CancellationToken ct)
    {
        var mine = new HandoverRequest(_db.DeviceId, _o.DeviceName, NowUnix, Updated: NowUnix, Id: Guid.NewGuid().ToString("N"));
        string? busy = null;
        await UpdateControlAsync(c =>
        {
            if (c.Handover is { } other && other.Dev != _db.DeviceId && other.State is "asked" or "seen" or "finishing"
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
        if (c.Handover?.Id != mine.Id)
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
        await UpdateControlAsync(c => c.Handover?.Id == mine.Id ? c with { Handover = null } : c, ct);

    // ── on the PC that writes ────────────────────────────────────────────────
    public async Task RefuseHandoverAsync(HandoverRequest r, CancellationToken ct)
    {
        await UpdateControlAsync(c => c.Handover?.Id == r.Id ? c with { Handover = r with { State = "refused", Updated = NowUnix } } : c, ct);
        Log.Info($"handover: refused '{r.Name}'");
    }

    /// <summary>Approved: from now on the drive takes no new changes here (the file system asks).
    /// False: the request is gone (cancelled, or the drive was taken) — nothing changed.</summary>
    public async Task<bool> BeginHandoverAsync(HandoverRequest r, int pending, CancellationToken ct)
    {
        HandingOver = true;
        WritableChanged?.Invoke();
        try
        {
            if (!await ReportHandoverAsync(r, pending, ct)) { EndHandover(); return false; }
        }
        catch { EndHandover(); throw; }
        Log.Info($"handover: approved for '{r.Name}', {pending} files still uploading");
        return true;
    }

    /// <summary>Progress for the asking PC. False: the request was withdrawn or the drive taken — stop.</summary>
    public async Task<bool> ReportHandoverAsync(HandoverRequest r, int pending, CancellationToken ct)
    {
        bool ours = false;
        await UpdateControlAsync(c =>
        {
            ours = c.Handover?.Id == r.Id && c.Handover.State is "asked" or "seen" or "finishing" && c.Lease?.Dev == _db.DeviceId;
            return ours ? c with { Handover = c.Handover! with { State = "finishing", Pending = pending, Updated = NowUnix } } : c;
        }, ct);
        return ours;
    }

    /// <summary>The handover could not be finished (offline…): tell the asking PC, and work on as before.</summary>
    public async Task FailHandoverAsync(HandoverRequest r, CancellationToken ct)
    {
        EndHandover();
        try { await UpdateControlAsync(c => c.Handover?.Id == r.Id ? c with { Handover = c.Handover with { State = "failed", Updated = NowUnix } } : c, ct); }
        catch (Exception ex) { Log.Warn($"handover: could not report the failure: {ex.Message}"); }
    }

    /// <summary>Everything is uploaded and journaled: write the requester into the lease. False if the
    /// requester (or someone else) already took the drive, or something is still unsent.</summary>
    public async Task<bool> CompleteHandoverAsync(HandoverRequest r, CancellationToken ct)
    {
        await FlushAsync(ct);
        // changes of a NEW file still open in an app stay here (sent when the drive comes back); all the rest
        // must be in the journal before the lease moves
        if (_db.SendableOutboxCount() > 0) return false;
        bool handed = false;
        await UpdateControlAsync(c =>
        {
            handed = c.Lease?.Dev == _db.DeviceId && c.Handover?.Id == r.Id && c.Handover.State == "finishing";
            if (!handed) return c;
            return c with
            {
                Lease = new LeaseInfo(r.Dev, r.Name, NowUnix + (long)_o.LeaseDuration.TotalSeconds),
                Handover = r with { State = "done", Pending = 0, Updated = NowUnix },
            };
        }, ct);
        HandingOver = false;
        if (!handed) { WritableChanged?.Invoke(); return false; }
        SetWritable(false, r.Name);
        Log.Info($"handover: the drive now belongs to '{r.Name}'");
        return true;
    }

    /// <summary>The handover was abandoned (the requester took the drive, or it failed): back to normal.</summary>
    public void EndHandover()
    {
        if (!HandingOver) return;
        HandingOver = false;
        WritableChanged?.Invoke();
    }
}
