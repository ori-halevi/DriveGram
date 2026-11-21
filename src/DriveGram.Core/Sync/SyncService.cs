using System.Text.Json;
using DriveGram.Crypto;
using DriveGram.Engine;
using DriveGram.Index;
using DriveGram.Remote;

namespace DriveGram.Sync;

public sealed record LeaseInfo(string Dev, string Name, long Until);
public sealed record SnapInfo(int Msg, int Upto);

/// <summary>The one editable record of a drive (Telegram: the pinned text message of the journal channel).</summary>
public sealed record ControlState(int V, VaultHeader Vault, LeaseInfo? Lease, SnapInfo? Snap)
{
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
        int chunkSize = Keys.DefaultChunkSize, int frameSize = Keys.DefaultFrameSize, int iterations = Keys.DefaultIterations)
    {
        if (await ReadControlAsync(remote, ct) is not null) throw new InvalidOperationException("This remote already holds a drive.");
        var (header, keys) = Keys.Create(password, chunkSize, frameSize, iterations);
        await remote.WriteControlAsync(new ControlState(1, header, null, null).ToJson(), ct);
        return keys;
    }

    long NowUnix => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public async Task StartAsync(bool takeOver, CancellationToken ct)
    {
        var ctrl = await ReadControlAsync(_remote, ct) ?? throw new InvalidOperationException("No drive on this remote.");
        if (_db.IsFresh && ctrl.Snap is { } snap)
        {
            Log.Info($"loading snapshot (journal up to #{snap.Upto})");
            var data = Envelope.Open(_keys.JournalKey, await _remote.ReadJournalAsync(snap.Msg, ct));
            _db.ImportSnapshot(data);
            _db.Applied = snap.Upto;
        }
        await CatchUpAsync(ct);
        await TryAcquireLeaseAsync(takeOver, ct);
        _loop = Task.Run(RunAsync);
    }

    async Task TryAcquireLeaseAsync(bool force, CancellationToken ct)
    {
        var ctrl = (await ReadControlAsync(_remote, ct))!;
        var lease = ctrl.Lease;
        bool free = lease is null || lease.Dev == _db.DeviceId || lease.Until < NowUnix;
        if (!free && !force)
        {
            SetWritable(false, lease!.Name);
            Log.Warn($"drive is open for writing on '{lease.Name}' (until {DateTimeOffset.FromUnixTimeSeconds(lease.Until).ToLocalTime():t}); mounting read-only");
            return;
        }
        if (lease is not null && lease.Dev != _db.DeviceId)
        {
            // Someone else wrote before us: make sure we have everything they journaled.
            await CatchUpAsync(ct);
        }
        await WriteLeaseAsync(ct);
        await Task.Delay(1500, ct);
        var check = (await ReadControlAsync(_remote, ct))!.Lease;
        SetWritable(check?.Dev == _db.DeviceId, check?.Name);
    }

    async Task WriteLeaseAsync(CancellationToken ct)
    {
        var ctrl = (await ReadControlAsync(_remote, ct))!;
        var lease = new LeaseInfo(_db.DeviceId, _o.DeviceName, NowUnix + (long)_o.LeaseDuration.TotalSeconds);
        await _remote.WriteControlAsync((ctrl with { Lease = lease }).ToJson(), ct);
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
                if (item.Name.StartsWith("j-"))
                {
                    var entry = OpJson.DeserializeEntry(Envelope.Open(_keys.JournalKey, await _remote.ReadJournalAsync(item.Id, ct)));
                    if (entry.Dev != _db.DeviceId)
                    {
                        _db.InTransaction(() =>
                        {
                            foreach (var op in entry.Ops) _db.ApplyRemote(op);
                            _db.Applied = item.Id;
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
            return applied;
        }
        finally { _io.Release(); }
    }

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
                await Retry.ForeverAsync(() => _remote.AppendJournalAsync($"j-{_db.DeviceId[..8]}.bin", payload, ct),
                    "journal append", ct);
                _db.DeleteOutboxUpTo(batch[^1].Seq);
                _opsSinceSnapshot += batch.Count;
            }
        }
        finally { _io.Release(); }
    }

    public async Task SnapshotAsync(CancellationToken ct)
    {
        await FlushAsync(ct);
        await CatchUpAsync(ct);
        if (_db.OutboxCount() > 0) return;
        await _io.WaitAsync(ct);
        try
        {
            int upto = _db.Applied;
            var payload = Envelope.Seal(_keys.JournalKey, _db.ExportSnapshot());
            int msg = await _remote.AppendJournalAsync($"s-{upto}.bin", payload, ct);
            var ctrl = (await ReadControlAsync(_remote, ct))!;
            await _remote.WriteControlAsync((ctrl with { Snap = new SnapInfo(msg, upto) }).ToJson(), ct);
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
        while (true)
        {
            var garbage = _db.GarbageChunks(_o.GarbageGrace);
            if (garbage.Count == 0) break;
            await _remote.DeleteChunksAsync(garbage.Select(g => g.Msg).ToList(), ct);
            _db.DeleteChunkRows(garbage.Select(g => g.ChunkId));
            Log.Info($"gc: deleted {garbage.Count} unreferenced chunks");
        }
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
                if (DateTime.UtcNow < nextBeat) continue;
                nextBeat = DateTime.UtcNow + _o.HeartbeatInterval;

                if (Writable)
                {
                    var lease = (await ReadControlAsync(_remote, ct))!.Lease;
                    if (lease is not null && lease.Dev != _db.DeviceId)
                    {
                        // Another PC took over (we were offline longer than the lease). Stop writing;
                        // anything still in the outbox stays queued locally and is reported.
                        SetWritable(false, lease.Name);
                        Log.Error($"lease lost to '{lease.Name}'; {_db.OutboxCount()} local changes are held back");
                        continue;
                    }
                    await WriteLeaseAsync(ct);
                    if (_opsSinceSnapshot >= _o.SnapshotEveryOps ||
                        (_opsSinceSnapshot > 0 && DateTime.UtcNow - _lastSnapshot > _o.SnapshotEvery))
                        await SnapshotAsync(ct);
                    if (DateTime.UtcNow - _lastGc > _o.GcEvery) await CollectGarbageAsync(ct);
                }
                else
                {
                    await CatchUpAsync(ct);
                    if (_db.OutboxCount() == 0) await TryAcquireLeaseAsync(false, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { Log.Error("sync loop", ex); }
        }
    }

    /// <summary>Flush, then give the lease back so another PC can write.</summary>
    public async Task StopAsync(bool releaseLease, CancellationToken ct)
    {
        _cts.Cancel();
        if (_loop is not null) { try { await _loop; } catch (OperationCanceledException) { } }
        if (!Writable) return;
        await FlushAsync(ct);
        if (_opsSinceSnapshot > 0) await SnapshotAsync(ct);
        if (releaseLease && _db.OutboxCount() == 0 && _db.PendingNodes().Count == 0)
        {
            var ctrl = (await ReadControlAsync(_remote, ct))!;
            if (ctrl.Lease?.Dev == _db.DeviceId)
                await _remote.WriteControlAsync((ctrl with { Lease = null }).ToJson(), ct);
            Log.Info("lease released");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null) { try { await _loop; } catch (OperationCanceledException) { } }
    }
}
