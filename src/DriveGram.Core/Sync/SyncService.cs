using System.Text.Json;
using DriveGram.Crypto;
using DriveGram.Engine;
using DriveGram.Index;
using DriveGram.Remote;

namespace DriveGram.Sync;

public sealed record LeaseInfo(string Dev, string Name, long Until);
public sealed record SnapInfo(int Msg, int Upto);

/// <summary>
/// Who a drive is: a unique id, a display name, and — the binding that matters — the id of the
/// Telegram channel holding its data. With it, one account can hold (or be an admin of) several
/// drives and each journal channel says exactly which data channel is its partner.
/// Drives created before 0.7 have none; they get one the first time they mount writable.
/// </summary>
public sealed record DriveIdentity(string Id, string Name, long DataChannel);

/// <summary>The one editable record of a drive (Telegram: the pinned text message of the journal channel).</summary>
/// <param name="MinReader">/<param name="MinWriter">oldest StorageFormat that may read / write this drive (see StorageFormat).</param>
public sealed record ControlState(int V, VaultHeader Vault, LeaseInfo? Lease, SnapInfo? Snap, DriveIdentity? Drive = null,
    int MinReader = 1, int MinWriter = 1)
{
    /// <summary>Fields written by a newer version, kept verbatim on every rewrite.</summary>
    [System.Text.Json.Serialization.JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, OpJson.Options);
    public static ControlState? FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ControlState>(json, OpJson.Options);
}

public sealed class SyncOptions
{
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(1.5);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>A lease not renewed for this long is considered abandoned (PC crashed / offline).</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(15);
    public int SnapshotEveryOps { get; init; } = 2000;
    public TimeSpan SnapshotEvery { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan TrashRetention { get; init; } = TimeSpan.FromDays(30);
    /// <summary>Unreferenced chunks are deleted only after this (a resumed upload dedups against them).</summary>
    public TimeSpan GarbageGrace { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan GcEvery { get; init; } = TimeSpan.FromHours(6);
    public string DeviceName { get; init; } = Environment.MachineName;
}

/// <summary>
/// Keeps the local index and the remote journal in step.
///  - Every local op goes to the outbox; the flusher sends batches as encrypted journal entries.
///  - At start (and periodically when read-only) entries from other PCs are applied in journal order.
///  - A lease in the control record says which PC may write. Without it the drive mounts read-only.
///  - Snapshots of the whole index bound how much journal a new PC must replay.
/// Rule kept by the engine: content ops are created only after their chunks are stored, so the
/// journal never references bytes that exist only on one PC.
/// </summary>
public sealed class SyncService : IAsyncDisposable
{
    readonly IndexDb _db;
    readonly IRemote _remote;
    readonly Keys _keys;
    readonly SyncOptions _o;
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _io = new(1);
    Task? _loop;
    int _opsSinceSnapshot;
    DateTime _lastSnapshot = DateTime.UtcNow, _lastGc = DateTime.MinValue;

    public bool Writable { get; private set; }
    /// <summary>A newer DriveGram changed this drive's format: this version only reads, and asks to be updated.</summary>
    public bool NeedsUpdate { get; private set; }
    /// <summary>False while the content engine is uploading (set by the host); garbage collection waits.</summary>
    public Func<bool>? CanCollectGarbage { get; set; }
    public string? LeaseHolder { get; private set; }
    public event Action? WritableChanged;
    /// <summary>Ops from another PC were applied to the local index (so open folder views can refresh).</summary>
    public event Action<IReadOnlyList<Op>>? RemoteOpsApplied;

    public SyncService(IndexDb db, IRemote remote, Keys keys, SyncOptions? options = null)
    {
        _db = db;
        _remote = remote;
        _keys = keys;
        _o = options ?? new SyncOptions();
    }

    public static async Task<ControlState?> ReadControlAsync(IRemote remote, CancellationToken ct) =>
        ControlState.FromJson(await remote.ReadControlAsync(ct));

    /// <summary>Create a new drive (vault) on an empty remote.</summary>
    public static async Task<Keys> CreateDriveAsync(IRemote remote, string password, CancellationToken ct,
        int chunkSize = Keys.DefaultChunkSize, int frameSize = Keys.DefaultFrameSize, int iterations = Keys.DefaultIterations,
        DriveIdentity? identity = null)
    {
        if (await ReadControlAsync(remote, ct) is not null) throw new InvalidOperationException("This remote already holds a drive.");
        var (header, keys) = Keys.Create(password, chunkSize, frameSize, iterations);
        await remote.WriteControlAsync(new ControlState(1, header, null, null, identity).ToJson(), ct);
        return keys;
    }

    readonly SemaphoreSlim _ctrlLock = new(1, 1);

    /// <summary>This drive's control record, as stored now.</summary>
    public Task<ControlState?> ReadControlAsync(CancellationToken ct) => ReadControlAsync(_remote, ct);

    /// <summary>Read-modify-write of the control record, serialized within this PC (the lease heartbeat
    /// rewrites it every minute: an unserialized edit could be overwritten by a stale copy).</summary>
    async Task<ControlState> UpdateControlAsync(Func<ControlState, ControlState> change, CancellationToken ct)
    {
        await _ctrlLock.WaitAsync(ct);
        try
        {
            var next = change((await ReadControlAsync(_remote, ct))!);
            await _remote.WriteControlAsync(next.ToJson(), ct);
            return next;
        }
        finally { _ctrlLock.Release(); }
    }

    /// <summary>Create (or replace) the drive's recovery key; returns its text, to show the user ONCE.
    /// Only while this PC holds the lease. Backward compatible: older versions ignore the extra field.</summary>
    public async Task<string> CreateRecoveryKeyAsync(Keys keys, CancellationToken ct)
    {
        if (!Writable) throw new InvalidOperationException("The drive is read-only on this PC.");
        var (proposed, text) = keys.WithNewRecoveryKey((await ReadControlAsync(_remote, ct))!.Vault);
        var wrap = proposed.Recovery!;
        await ChangeVaultAsync(v => v with { Recovery = wrap }, v => v.Recovery?.Wrapped == wrap.Wrapped, ct);
        Log.Info("recovery key created (a previous one, if any, no longer works)");
        return text;
    }

    /// <summary>
    /// Change the vault safely: only while this PC really holds the lease (checked inside the locked
    /// read-modify-write), raising minWriter to 2 so no older version can rewrite the record and drop the
    /// new fields, and then READ BACK: success only if the change is really stored. If the write itself
    /// failed with an unknown outcome (e.g. a timeout after the server applied it), the read-back decides.
    /// </summary>
    async Task ChangeVaultAsync(Func<VaultHeader, VaultHeader> change, Func<VaultHeader, bool> applied, CancellationToken ct)
    {
        try
        {
            await UpdateControlAsync(c =>
            {
                if (c.Lease?.Dev != _db.DeviceId) throw new LeaseLostException();
                return c with { Vault = change(c.Vault), MinWriter = Math.Max(c.MinWriter, 2) };
            }, ct);
        }
        catch (LeaseLostException) { SetWritable(false, null); throw; }
        catch (Exception ex) { Log.Warn($"vault change: write reported {ex.Message}; checking what was stored"); }
        var stored = await ReadControlAsync(_remote, ct);
        if (stored is null || !applied(stored.Vault))
            throw new IOException("The change could not be saved to the drive. Nothing was changed; try again.");
    }

    /// <summary>Change the drive's password: the same master key, re-wrapped — no file is touched. PCs
    /// that remember the key keep working. Raises minReader to 2: versions before 1.0 cannot read the new
    /// vault and stop safely instead of misreading it.</summary>
    public async Task ChangePasswordAsync(Keys keys, string newPassword, CancellationToken ct)
    {
        if (!Writable) throw new InvalidOperationException("The drive is read-only on this PC.");
        var wrap = keys.WithNewPassword((await ReadControlAsync(_remote, ct))!.Vault, newPassword).Password!;
        await ChangeVaultAsync(v => v with { V = 2, Salt = "", Iter = 0, Password = wrap }, v => v.Password?.Wrapped == wrap.Wrapped, ct);
        await UpdateControlAsync(c => c with { MinReader = Math.Max(c.MinReader, 2) }, ct);
        Log.Info("drive password changed");
    }

    /// <summary>Give a pre-0.7 drive its identity (only the PC holding the lease writes the control record).</summary>
    public async Task<DriveIdentity?> EnsureIdentityAsync(Func<DriveIdentity> make, CancellationToken ct)
    {
        var ctrl = (await ReadControlAsync(_remote, ct))!;
        if (ctrl.Drive is not null || !Writable) return ctrl.Drive;
        var id = make();
        ctrl = await UpdateControlAsync(c => c.Drive is null ? c with { Drive = id } : c, ct);
        id = ctrl.Drive!;
        Log.Info($"drive identity recorded: {id.Name} ({id.Id}), data channel {id.DataChannel}");
        return id;
    }

    long NowUnix => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public async Task StartAsync(bool takeOver, CancellationToken ct)
    {
        var ctrl = await ReadControlAsync(_remote, ct) ?? throw new InvalidOperationException("No drive on this remote.");
        if (ctrl.MinReader > StorageFormat.Current) throw new NeedsUpdateException($"needs format {ctrl.MinReader}");
        if (ctrl.MinWriter > StorageFormat.Current) MarkNeedsUpdate($"writing needs format {ctrl.MinWriter}");
        // A snapshot replaces the whole index: only for an index that never held anything of its own
        // (a PC that wrote but never received an entry still counted as "fresh", and loading another PC's
        // snapshot then wiped its files waiting to upload and its unsent changes).
        bool fresh = _db.IsFresh && _db.OutboxCount() == 0 && _db.PendingNodes().Count == 0 && _db.AllDirty().Count == 0;
        if (fresh && ctrl.Snap is { } snap)
        {
            Log.Info($"loading snapshot (journal up to #{snap.Upto})");
            byte[] data;
            try { data = Envelope.Open(_keys.JournalKey, await _remote.ReadJournalAsync(snap.Msg, ct)); }
            catch (InvalidDataException ex) { throw new NeedsUpdateException($"snapshot #{snap.Msg}: {ex.Message}"); }
            _db.ImportSnapshot(data);
            _db.Applied = snap.Upto;
        }
        await CatchUpAsync(ct);
        if (_db.IsFresh) _db.Applied = 0; // caught up with an empty journal: this index is in use from now on
        await TryAcquireLeaseAsync(takeOver, ct);
        _loop = Task.Run(RunAsync);
    }

    async Task TryAcquireLeaseAsync(bool force, CancellationToken ct)
    {
        var ctrl = (await ReadControlAsync(_remote, ct))!;
        if (NeedsUpdate || ctrl.MinWriter > StorageFormat.Current)
        {
            SetWritable(false, null); // never write a format we do not fully understand
            return;
        }
        var lease = ctrl.Lease;
        bool free = lease is null || lease.Dev == _db.DeviceId || lease.Until < NowUnix;
        if (!free && !force)
        {
            SetWritable(false, lease!.Name);
            Log.Warn($"drive is open for writing on '{lease.Name}' (until {DateTimeOffset.FromUnixTimeSeconds(lease.Until).ToLocalTime():t}); mounting read-only");
            return;
        }
        // Another PC may have written and released the lease (null) since we last looked: take in
        // everything it journaled BEFORE writing on top (held-back local changes are rebased there).
        await CatchUpAsync(ct);
        await WriteLeaseAsync(ct);
        await Task.Delay(1500, ct);
        var check = (await ReadControlAsync(_remote, ct))!.Lease;
        if (check?.Dev == _db.DeviceId) await CatchUpAsync(ct); // anything appended just before we took over
        SetWritable(check?.Dev == _db.DeviceId, check?.Name);
        if (Writable) _leaseConfirmedAt = DateTime.UtcNow;
    }

    async Task WriteLeaseAsync(CancellationToken ct)
    {
        var lease = new LeaseInfo(_db.DeviceId, _o.DeviceName, NowUnix + (long)_o.LeaseDuration.TotalSeconds);
        await UpdateControlAsync(c => c with { Lease = lease }, ct);
    }

    void MarkNeedsUpdate(string why)
    {
        if (!NeedsUpdate) Log.Error($"this drive was written by a newer DriveGram ({why}); read-only until DriveGram is updated");
        NeedsUpdate = true;
        SetWritable(false, null);
    }

    void SetWritable(bool writable, string? holder)
    {
        bool changed = Writable != writable;
        Writable = writable;
        LeaseHolder = writable ? null : holder;
        if (changed) WritableChanged?.Invoke();
    }

    /// <summary>Apply journal entries we have not seen (skipping our own, already applied locally).</summary>
    public async Task<int> CatchUpAsync(CancellationToken ct)
    {
        await _io.WaitAsync(ct);
        try
        {
            int applied = 0;
            foreach (var item in await _remote.ListJournalAsync(_db.Applied, ct))
            {
                if (_ownAppends.Contains(item.Id)) { _db.Applied = item.Id; continue; } // ours, already applied
                if (!item.Name.StartsWith("j-"))
                {
                    if (!item.Name.StartsWith("s-") && OurNaming.IsMatch(item.Name))
                    {
                        // a record kind from a newer version: stop HERE (skip nothing, advance nothing)
                        MarkNeedsUpdate($"journal #{item.Id}: unknown record '{item.Name}'");
                        break;
                    }
                    if (!item.Name.StartsWith("s-")) Log.Warn($"journal #{item.Id}: '{item.Name}' is not a DriveGram record; ignored");
                }
                else
                {
                    JournalEntry entry;
                    try { entry = OpJson.DeserializeEntry(Envelope.Open(_keys.JournalKey, await _remote.ReadJournalAsync(item.Id, ct))); }
                    catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidDataException)
                    {
                        // an op type / shape from a newer version: stop HERE (skip nothing, advance nothing)
                        MarkNeedsUpdate($"journal #{item.Id}: {ex.Message}");
                        break;
                    }
                    if (entry.V > StorageFormat.Current)
                    {
                        MarkNeedsUpdate($"journal #{item.Id} is format {entry.V}");
                        break;
                    }
                    if (entry.Dev != _db.DeviceId)
                    {
                        _db.InTransaction(() =>
                        {
                            foreach (var op in entry.Ops) _db.ApplyRemote(op);
                            _db.Applied = item.Id;
                            // changes held back on this PC will be journaled AFTER this entry: mark that the
                            // local index must replay them on top of it (same order as every other PC)
                            if (_db.OutboxCount() > 0) _db.SetMeta("rebase", "1");
                        });
                        applied += entry.Ops.Length;
                        try { RemoteOpsApplied?.Invoke(entry.Ops); }
                        catch (Exception ex) { Log.Warn($"remote-ops listener failed: {ex.Message}"); }
                        continue;
                    }
                }
                _db.Applied = item.Id;
            }
            if (applied > 0) Log.Info($"applied {applied} ops from other PCs");
            if (_db.GetMeta("rebase") is not null)
            {
                int n = _db.ReapplyOutbox();
                Log.Warn($"{n} local changes held back on this PC were replayed after the other PC's changes; they are sent once this PC can write");
            }
            return applied;
        }
        finally { _io.Release(); }
    }

    static readonly System.Text.RegularExpressions.Regex OurNaming = new(@"^[a-z]{1,8}-[^\\/]*\.bin$");

    DateTime _leaseConfirmedAt = DateTime.MinValue;
    readonly HashSet<int> _ownAppends = []; // journal messages this session appended (touched under _io)
    volatile bool _beatNow;

    /// <summary>The PC woke up: renew the lease / catch up now rather than at the next heartbeat.</summary>
    public void Wake() => _beatNow = true;

    /// <summary>Do we (still) hold the write lease? Re-read from the control record when the last
    /// confirmation is older than one heartbeat; on loss, stop writing and keep the outbox.</summary>
    async Task<bool> StillHoldLeaseAsync(CancellationToken ct)
    {
        if (!Writable) return false;
        if (DateTime.UtcNow - _leaseConfirmedAt < _o.HeartbeatInterval) return true;
        var lease = (await ReadControlAsync(_remote, ct))?.Lease;
        if (lease is not null && lease.Dev == _db.DeviceId && lease.Until > NowUnix)
        {
            _leaseConfirmedAt = DateTime.UtcNow;
            return true;
        }
        if (lease is not null && lease.Dev == _db.DeviceId)
        {
            // Still ours, but it expired (the PC slept / was offline): nobody took it. Do not append on an
            // expired lease; re-take it properly (catch-up, write, confirm) on the loop's next turn. The
            // drive stays writable meanwhile: changes simply wait in the outbox for a few seconds.
            _beatNow = true;
            return false;
        }
        SetWritable(false, lease?.Name);
        Log.Error($"lease lost to '{lease?.Name}'; {_db.OutboxCount()} local changes are held back (not sent)");
        return false;
    }

    /// <summary>Changes made on this PC that cannot be sent because another PC holds the drive.</summary>
    public int HeldBackChanges => Writable ? 0 : _db.OutboxCount();

    /// <summary>Send queued local ops as journal entries.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        if (!Writable) return;
        await _io.WaitAsync(ct);
        try
        {
            while (true)
            {
                var batch = _db.PeekOutbox(500);
                if (batch.Count == 0) return;
                var entry = new JournalEntry(1, _db.DeviceId, NowUnix, batch.Select(b => b.Op).ToArray());
                var payload = Envelope.Seal(_keys.JournalKey, OpJson.SerializeEntry(entry));
                // Checked before EVERY attempt: after a long outage another PC may have taken the lease,
                // and appending then would put two writers' changes into the journal (diverged indexes).
                int msgId = await Retry.ForeverAsync(async () =>
                {
                    if (!await StillHoldLeaseAsync(ct)) throw new LeaseLostException();
                    return await _remote.AppendJournalAsync($"j-{_db.DeviceId[..8]}.bin", payload, ct);
                }, "journal append", ct);
                _ownAppends.Add(msgId);
                _db.DeleteOutboxUpTo(batch[^1].Seq);
                _opsSinceSnapshot += batch.Count;
            }
        }
        catch (LeaseLostException) { /* outbox kept; reported via HeldBackChanges */ }
        finally { _io.Release(); }
    }

    public async Task SnapshotAsync(CancellationToken ct)
    {
        await FlushAsync(ct);
        await CatchUpAsync(ct);
        // a pending file's size/mtime in the index are local and uncommitted: a snapshot taken now would
        // hand other PCs a size that does not match the file's chunks
        if (_db.OutboxCount() > 0 || _db.PendingNodes().Count > 0) return;
        await _io.WaitAsync(ct);
        try
        {
            int upto = _db.Applied;
            var payload = Envelope.Seal(_keys.JournalKey, _db.ExportSnapshot());
            int msg = await _remote.AppendJournalAsync($"s-{upto}.bin", payload, ct);
            await UpdateControlAsync(c => c with { Snap = new SnapInfo(msg, upto) }, ct);
            _opsSinceSnapshot = 0;
            _lastSnapshot = DateTime.UtcNow;
            Log.Info($"snapshot written (#{msg}, up to #{upto}, {payload.Length:N0} bytes)");
        }
        finally { _io.Release(); }
    }

    /// <summary>Empty old trash and delete chunks nothing references any more.</summary>
    public async Task CollectGarbageAsync(CancellationToken ct)
    {
        if (!Writable) return;
        long cutoff = DateTime.UtcNow.Add(-_o.TrashRetention).ToFileTimeUtc();
        var expired = _db.Trashed().Where(n => n.Trashed < cutoff).ToList();
        if (expired.Count > 0)
        {
            _db.ApplyLocal(expired.Select(n => (Op)new PurgeOp(n.Id)).ToList());
            Log.Info($"trash: purged {expired.Count} items older than {_o.TrashRetention.TotalDays:0} days");
        }
        // rail (from InfiDrive's gc_orphan_blobs): never sweep while uploads are in flight
        if (CanCollectGarbage?.Invoke() == false)
        {
            Log.Debug("gc: skipped, uploads in progress");
            return;
        }
        int claimed = _db.ClaimGarbage(_o.GarbageGrace);
        while (true)
        {
            var msgs = _db.PendingDeletions();
            if (msgs.Count == 0) break;
            await _remote.DeleteChunksAsync(msgs, ct);
            _db.DeletionsDone(msgs);
            Log.Info($"gc: deleted {msgs.Count} unreferenced chunk messages");
        }
        if (claimed > 0) _db.Maintain();
        _lastGc = DateTime.UtcNow;
    }

    async Task RunAsync()
    {
        var ct = _cts.Token;
        var nextBeat = DateTime.UtcNow + _o.HeartbeatInterval;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_o.FlushInterval, ct);
                if (Writable) await FlushAsync(ct);
                if (DateTime.UtcNow < nextBeat && !_beatNow) continue;
                _beatNow = false;
                nextBeat = DateTime.UtcNow + _o.HeartbeatInterval;

                if (Writable)
                {
                    var lease = (await ReadControlAsync(_remote, ct))!.Lease;
                    if (lease?.Dev != _db.DeviceId)
                    {
                        // Another PC took over (we were offline longer than the lease), or took over AND
                        // released it (null): either way it may have written. Stop writing; re-acquiring goes
                        // through a catch-up, which replays our held-back changes after its entries.
                        SetWritable(false, lease?.Name);
                        Log.Error($"lease lost{(lease is null ? "" : $" to '{lease.Name}'")}; {_db.OutboxCount()} local changes are held back");
                        if (lease is null) await TryAcquireLeaseAsync(false, ct);
                        continue;
                    }
                    if (lease.Until <= NowUnix)
                    {
                        Log.Info("the write lease expired while this PC slept or was offline; taking it again");
                        await TryAcquireLeaseAsync(false, ct);
                        continue;
                    }
                    await WriteLeaseAsync(ct);
                    _leaseConfirmedAt = DateTime.UtcNow;
                    if (_opsSinceSnapshot >= _o.SnapshotEveryOps ||
                        (_opsSinceSnapshot > 0 && DateTime.UtcNow - _lastSnapshot > _o.SnapshotEvery))
                        await SnapshotAsync(ct);
                    if (DateTime.UtcNow - _lastGc > _o.GcEvery) await CollectGarbageAsync(ct);
                }
                else
                {
                    await CatchUpAsync(ct);
                    if (!NeedsUpdate) await TryAcquireLeaseAsync(false, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { Log.Error("sync loop", ex); }
        }
    }

    /// <summary>Windows is shutting down: push the journal and give the lease back, nothing slower.</summary>
    public async Task QuickReleaseAsync(CancellationToken ct)
    {
        _cts.Cancel();
        if (!Writable) return;
        await FlushAsync(ct);
        if (_db.OutboxCount() == 0 && _db.PendingNodes().Count == 0)
        {
            await UpdateControlAsync(c => c.Lease?.Dev == _db.DeviceId ? c with { Lease = null } : c, ct);
        }
    }

    /// <summary>Flush, then give the lease back so another PC can write.</summary>
    public async Task StopAsync(bool releaseLease, CancellationToken ct)
    {
        _cts.Cancel();
        if (_loop is not null) { try { await _loop; } catch (OperationCanceledException) { } }
        if (!Writable) return;
        await FlushAsync(ct);
        if (_opsSinceSnapshot > 0)
        {
            // a snapshot only speeds up the next start: never let it keep the drive locked for other PCs
            using var snapCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            snapCts.CancelAfter(TimeSpan.FromSeconds(15));
            try { await SnapshotAsync(snapCts.Token); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Warn($"snapshot at stop skipped: {ex.Message}"); }
        }
        if (releaseLease && _db.OutboxCount() == 0 && _db.PendingNodes().Count == 0)
        {
            await UpdateControlAsync(c => c.Lease?.Dev == _db.DeviceId ? c with { Lease = null } : c, ct);
            Log.Info("lease released");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null) { try { await _loop; } catch (OperationCanceledException) { } }
    }
}
