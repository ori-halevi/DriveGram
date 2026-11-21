using DriveGram.Crypto;
using DriveGram.Engine;
using DriveGram.Index;
using DriveGram.Remote;
using DriveGram.Sync;
using Fsp;
using Fsp.Interop;

namespace DriveGram.Host;

public sealed class AlreadyMountedException() : Exception("DriveGram is already running for this drive on this PC.");

/// <summary>An item in the drive's own trash.</summary>
public sealed record TrashItem(string Id, string Name, string OriginalFolder, bool Dir, long Size, DateTime DeletedAt);

/// <summary>
/// One mounted drive: remote + index + engine + sync + WinFsp host, with an orderly start and stop.
/// Used by both the console (`drivegram mount`) and the tray app, so they behave the same.
/// </summary>
public sealed class DriveSession : IAsyncDisposable
{
    public AppState State { get; }
    public string MountPoint { get; }
    public IndexDb Db { get; }
    public ContentEngine Engine { get; }
    public SyncService Sync { get; }

    readonly IRemote _remote;
    readonly FileSystemHost _host;
    readonly Semaphore _instance;
    readonly EventWaitHandle _stopEvent;
    bool _mounted;

    /// <summary>Raised when another process asked us to stop (`drivegram unmount`).</summary>
    public event Action? StopRequested;

    DriveSession(AppState state, string mountPoint, IRemote remote, IndexDb db, ContentEngine engine, SyncService sync,
        Semaphore instance, EventWaitHandle stopEvent)
    {
        State = state;
        MountPoint = mountPoint;
        _remote = remote;
        Db = db;
        Engine = engine;
        Sync = sync;
        _instance = instance;
        _stopEvent = stopEvent;
        _host = new FileSystemHost(new DriveFileSystem(db, engine, sync, state.Config.VolumeLabel));
        sync.RemoteOpsApplied += NotifyExplorer;
        new Thread(() =>
        {
            try { while (_stopEvent.WaitOne()) StopRequested?.Invoke(); }
            catch (ObjectDisposedException) { }
        }) { IsBackground = true, Name = "drivegram-stop-listener" }.Start();
    }

    public static string StopEventName(AppState state) => @"Local\DriveGram-stop-" + Path.GetFileName(state.Dir);
    static string InstanceName(AppState state) => @"Local\DriveGram-instance-" + Path.GetFileName(state.Dir);

    public static bool WinFspInstalled()
    {
        try { _ = FileSystemHost.Version(); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Create the Telegram client for this PC's saved session. Interactive sign-in is done by the
    /// setup wizard / `drivegram login` through client.Login(...) or LoginWithQRCode; here "phone_number"
    /// is always null, so resuming accepts the saved account without asking (the bug that made the
    /// tray think it was signed out). `askPassword` answers a 2FA prompt during a QR sign-in.
    /// </summary>
    public static WTelegram.Client NewTelegramClient(AppState state, Func<string?>? askPassword = null)
    {
        var cfg = state.Config;
        if (cfg.ApiId == 0 || string.IsNullOrEmpty(cfg.ApiHash)) throw new NotSignedInException();
        string? Config(string what) => what switch
        {
            "api_id" => cfg.ApiId.ToString(),
            "api_hash" => cfg.ApiHash,
            "device_model" => "DriveGram on " + Environment.MachineName,
            "system_version" => Environment.OSVersion.VersionString,
            "app_version" => "DriveGram 0.4",
            "password" => askPassword is null ? throw new NotSignedInException() : askPassword(),
            "verification_code" or "first_name" or "last_name" or "email" or "email_verification_code" =>
                throw new NotSignedInException(),
            _ => null, // includes "phone_number": accept the account the session belongs to
        };
        return new WTelegram.Client(Config, new DpapiSessionStream(state.SessionPath));
    }

    public static async Task<IRemote> OpenRemoteAsync(AppState state, bool create)
    {
        if (state.Folder is not null) return new FolderRemote(state.Folder);
        var client = NewTelegramClient(state);
        TelegramRemote remote;
        try { remote = await TelegramRemote.OpenAsync(client, state.Config.Channels, create); }
        catch { client.Dispose(); throw; }
        if (state.Config.Channels != remote.Channels)
        {
            state.Config.Channels = remote.Channels;
            state.Save();
        }
        return remote;
    }

    /// <summary>Open everything and mount. `unlock` turns the vault header into keys (remembered key or a prompt).</summary>
    public static async Task<DriveSession> StartAsync(AppState state,
        Func<ControlState, Task<Keys>> unlock, string? mountPoint, bool takeOver, CancellationToken ct = default)
    {
        if (!WinFspInstalled()) throw new InvalidOperationException("WinFsp is not installed (https://winfsp.dev/rel/, version 2.1).");
        mountPoint ??= state.Config.MountPoint;
        mountPoint = mountPoint.TrimEnd('\\');
        if (!mountPoint.EndsWith(':')) mountPoint += ":";

        var instance = new Semaphore(1, 1, InstanceName(state));
        if (!instance.WaitOne(0)) { instance.Dispose(); throw new AlreadyMountedException(); }

        IRemote? remote = null;
        IndexDb? db = null;
        ContentEngine? engine = null;
        SyncService? sync = null;
        try
        {
            remote = await OpenRemoteAsync(state, create: false);
            var ctrl = await SyncService.ReadControlAsync(remote, ct)
                       ?? throw new NoDriveException();
            var keys = await unlock(ctrl);

            db = IndexDb.Open(state.IndexPath);
            var cfg = state.Config;
            engine = new ContentEngine(db, remote, keys, state.Dir, new EngineOptions
            {
                StagingQuota = (long)(cfg.StagingQuotaGb * (1L << 30)),
                CacheQuota = (long)(cfg.CacheQuotaGb * (1L << 30)),
                UploadConcurrency = cfg.UploadConcurrency,
            });
            sync = new SyncService(db, remote, keys);
            sync.WritableChanged += () => Log.Warn(sync.Writable ? "the drive is now writable" : $"the drive is now READ-ONLY (open on {sync.LeaseHolder})");
            await sync.StartAsync(takeOver, ct);
            engine.Start();

            var stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, StopEventName(state));
            var session = new DriveSession(state, mountPoint, remote, db, engine, sync, instance, stopEvent);
            int status = session._host.Mount(mountPoint, null, false, 0);
            if (status < 0)
            {
                await session.StopAsync(waitForUploads: false);
                throw new IOException($"Could not mount {mountPoint} (NTSTATUS 0x{status:X8}). Is the drive letter already in use?");
            }
            session._mounted = true;
            try { ShellIntegration.SetDriveIcon(mountPoint, cfg.VolumeLabel); }
            catch (Exception ex) { Log.Warn($"drive icon: {ex.Message}"); }
            Log.Info($"mounted at {mountPoint}{(sync.Writable ? "" : " (read-only)")}");
            return session;
        }
        catch
        {
            if (sync is not null) await sync.DisposeAsync();
            if (engine is not null) await engine.DisposeAsync();
            db?.Dispose();
            if (remote is not null) await remote.DisposeAsync();
            instance.Release();
            instance.Dispose();
            throw;
        }
    }

    public EngineStatus Status => Engine.Status;
    public bool Writable => Sync.Writable;

    /// <summary>Unmount; optionally wait until all uploads are done; give the lease back; close everything.</summary>
    public async Task StopAsync(bool waitForUploads, Action<EngineStatus>? progress = null, CancellationToken ct = default)
    {
        if (_mounted)
        {
            _host.Unmount();
            _mounted = false;
            Log.Info($"unmounted {MountPoint}");
        }
        if (waitForUploads && Db.PendingNodes().Count > 0) await Engine.DrainAsync(progress, ct);
        try { await Sync.StopAsync(releaseLease: true, ct); }
        catch (Exception ex) { Log.Warn($"stop: could not reach the journal ({ex.Message}); changes stay queued"); }
    }

    /// <summary>Fast stop for Windows shutdown: unmount and push the journal within a few seconds.</summary>
    public void StopForShutdown()
    {
        try
        {
            if (_mounted) { _host.Unmount(); _mounted = false; }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            Sync.FlushAsync(cts.Token).Wait(cts.Token);
        }
        catch (Exception ex) { Log.Warn($"shutdown stop: {ex.Message}"); }
    }

    // ── trash ────────────────────────────────────────────────────────────────
    string PathOf(string id)
    {
        var parts = new List<string>();
        var n = Db.Get(id);
        for (int guard = 0; n is not null && !n.IsRoot && guard < 4096; guard++)
        {
            parts.Add(n.Name);
            n = n.Parent is null ? null : Db.Get(n.Parent);
        }
        parts.Reverse();
        return @"\" + string.Join('\\', parts);
    }

    /// <summary>Is the node reachable from the root without passing through trashed folders?</summary>
    bool IsLive(string id)
    {
        var n = Db.Get(id);
        for (int guard = 0; n is not null && guard < 4096; guard++)
        {
            if (n.IsRoot) return true;
            if (n.Trashed != 0) return false;
            n = n.Parent is null ? null : Db.Get(n.Parent);
        }
        return false;
    }

    public List<TrashItem> Trash() => Db.Trashed().Select(n =>
    {
        var folder = n.Parent is null ? @"\" : PathOf(n.Parent);
        return new TrashItem(n.Id, n.Name, MountPoint + folder, n.Dir, n.Size, DateTime.FromFileTimeUtc(n.Trashed).ToLocalTime());
    }).ToList();

    /// <summary>Put an item back where it was (or in the root if that folder is gone); returns where it went.</summary>
    public string Restore(string id)
    {
        if (!Writable) throw new InvalidOperationException("The drive is read-only on this PC right now.");
        var n = Db.Get(id) ?? throw new FileNotFoundException(id);
        var parent = n.Parent is not null && IsLive(n.Parent) ? n.Parent : IndexDb.RootId;
        Db.ApplyLocal(new RestoreOp(n.Id, parent, n.Name));
        var path = PathOf(n.Id);
        Notify(path, NotifyAction.Added, n.Dir);
        return MountPoint + path;
    }

    public void DeleteForever(string id)
    {
        if (!Writable) throw new InvalidOperationException("The drive is read-only on this PC right now.");
        Db.ApplyLocal(new PurgeOp(id));
    }

    // ── Explorer change notifications ────────────────────────────────────────
    void Notify(string path, NotifyAction action, bool dir)
    {
        if (!_mounted) return;
        try
        {
            if (_host.NotifyBegin(500) < 0) return;
            try
            {
                _host.Notify([new NotifyInfo
                {
                    FileName = path,
                    Action = action,
                    Filter = dir ? NotifyFilter.ChangeDirName : NotifyFilter.ChangeFileName,
                }]);
            }
            finally { _host.NotifyEnd(); }
        }
        catch (Exception ex) { Log.Debug($"notify {path}: {ex.Message}"); }
    }

    /// <summary>Changes made on another PC: tell Explorer so open folders refresh by themselves.</summary>
    void NotifyExplorer(IReadOnlyList<Op> ops)
    {
        foreach (var op in ops)
        {
            var n = Db.Get(op.Id);
            if (n is null) continue;
            var action = op switch
            {
                TrashOp or PurgeOp => NotifyAction.Removed,
                ContentOp => NotifyAction.Modified,
                _ => NotifyAction.Added,
            };
            Notify(PathOf(n.Id), action, n.Dir);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_mounted) { _host.Unmount(); _mounted = false; }
        _host.Dispose();
        _stopEvent.Dispose();
        await Engine.DisposeAsync();
        await Sync.DisposeAsync();
        Db.Dispose();
        await _remote.DisposeAsync();
        try { _instance.Release(); } catch (SemaphoreFullException) { }
        _instance.Dispose();
    }
}
