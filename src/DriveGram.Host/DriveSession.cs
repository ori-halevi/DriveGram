using DriveGram.Crypto;
using DriveGram.Engine;
using DriveGram.Index;
using DriveGram.Remote;
using DriveGram.Sync;
using Fsp;
using Fsp.Interop;

namespace DriveGram.Host;

public sealed class DrivePairingException() : Exception(L.T(
    "ערוץ הקבצים לא שייך ליומן של הכונן הזה (אולי ערוץ של כונן אחר עם אותו שם). פתח את 'הגדרות והתחברות' ובחר את הכונן מחדש.",
    "The data channel does not belong to this drive's journal (perhaps another drive's channel with the same name). Open 'Settings and sign-in' and choose the drive again."));
public sealed class WinFspMissingException() : Exception(L.T(
    "WinFsp לא מותקן, ובלעדיו הכונן לא יכול לעבוד. הפעל שוב את התקנת DriveGram (או התקן מ-https://winfsp.dev/rel), ואז לחץ 'חבר'.",
    "WinFsp is not installed, and the drive cannot work without it. Run the DriveGram installer again (or install it from https://winfsp.dev/rel), then click Connect."));
/// <summary>Windows refused the drive letter / mount: retrying the same thing will not help by itself.</summary>
public sealed class MountFailedException(string message) : Exception(message);
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

    /// <summary>Telegram asked the account to wait (rate limit) until this time; null if not.</summary>
    public DateTime? WaitingUntil => _remote.WaitingUntil;
    readonly FileSystemHost _host;
    readonly Semaphore _instance;
    readonly EventWaitHandle _stopEvent;
    bool _mounted;

    /// <summary>Raised when another process asked us to stop (`drivegram unmount`).</summary>
    public event Action? StopRequested;
    /// <summary>The Telegram session was revoked while mounted: the drive needs a new sign-in.</summary>
    public event Action? SignedOut;
    /// <summary>Set when the configured letter was taken and another one was used ("T: → U:").</summary>
    public string? LetterChanged { get; private set; }

    /// <summary>
    /// Prove the data channel really belongs to this journal: a chunk the journal says is stored at
    /// message M must be there under the name derived from its (password-keyed) chunk id. A data
    /// channel of another drive — even one with the same title — can never pass.
    /// </summary>
    static async Task VerifyPairingAsync(IndexDb db, TelegramRemote remote, CancellationToken ct)
    {
        var sample = db.SampleChunks(3);
        if (sample.Count == 0) return; // empty drive: nothing to prove yet
        foreach (var c in sample)
            if (await remote.HoldsChunkAsync(c.Msg, c.ChunkId, ct)) return;
        throw new DrivePairingException();
    }

    static bool LetterInUse(char letter) =>
        DriveInfo.GetDrives().Any(d => char.ToUpperInvariant(d.Name[0]) == char.ToUpperInvariant(letter));

    /// <summary>The unlocked drive keys (for recovery-key / password changes made while mounted).</summary>
    public Keys Keys { get; }

    DriveSession(AppState state, string mountPoint, IRemote remote, IndexDb db, ContentEngine engine, SyncService sync, Keys keys,
        Semaphore instance, EventWaitHandle stopEvent)
    {
        State = state;
        MountPoint = mountPoint;
        _remote = remote;
        Db = db;
        Engine = engine;
        Sync = sync;
        Keys = keys;
        _instance = instance;
        _stopEvent = stopEvent;
        _host = new FileSystemHost(new DriveFileSystem(db, engine, sync, state.Config.VolumeLabel));
        sync.RemoteOpsApplying += NoteOldPaths;
        sync.RemoteOpsApplied += NotifyExplorer;
        new Thread(() =>
        {
            try { while (_stopEvent.WaitOne()) StopRequested?.Invoke(); }
            catch (ObjectDisposedException) { }
        }) { IsBackground = true, Name = "drivegram-stop-listener" }.Start();
    }

    /// <summary>Signalled by `drivegram quit` (and the installer): the tray app finishes uploads and exits.</summary>
    public const string QuitEventName = @"Local\DriveGram-quit";

    public static string StopEventName(AppState state) => @"Local\DriveGram-stop-" + Path.GetFileName(state.Dir);
    static string InstanceName(AppState state) => @"Local\DriveGram-instance-" + Path.GetFileName(state.Dir);

    public static bool WinFspInstalled()
    {
        try
        {
            using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32)
                .OpenSubKey(@"SOFTWARE\WinFsp");
            return key?.GetValue("InstallDir") is string dir && File.Exists(Path.Combine(dir, "bin", "winfsp-x64.dll"));
        }
        catch { return false; }
    }

    /// <summary>
    /// Create the Telegram client for this PC's saved session. Interactive sign-in is done by the
    /// setup wizard / `drivegram login` through client.Login(...) or LoginWithQRCode; here "phone_number"
    /// is always null, so resuming accepts the saved account without asking (the bug that made the
    /// tray think it was signed out). `askPassword` answers a 2FA prompt during a QR sign-in.
    /// </summary>
    public static WTelegram.Client NewTelegramClient(AppState state, Func<string?>? askPassword = null) =>
        NewTelegramClient(state, askPassword, out _);

    public static WTelegram.Client NewTelegramClient(AppState state, Func<string?>? askPassword, out DpapiSessionStream stream)
    {
        var cfg = state.Config;
        if (cfg.ApiId == 0 || string.IsNullOrEmpty(cfg.ApiHash)) throw new NotSignedInException();
        string? Config(string what) => what switch
        {
            "api_id" => cfg.ApiId.ToString(),
            "api_hash" => cfg.ApiHash,
            "device_model" => "DriveGram on " + Environment.MachineName,
            "system_version" => Environment.OSVersion.VersionString,
            "app_version" => "DriveGram 1.0",
            "password" => askPassword is null ? throw new NotSignedInException() : askPassword(),
            "verification_code" or "first_name" or "last_name" or "email" or "email_verification_code" =>
                throw new NotSignedInException(),
            _ => null, // includes "phone_number": accept the account the session belongs to
        };
        stream = new DpapiSessionStream(state.SessionPath);
        return new WTelegram.Client(Config, stream);
    }

    /// <summary>Open the drive this profile is bound to (its channels are saved in the profile).</summary>
    public static async Task<IRemote> OpenRemoteAsync(AppState state)
    {
        if (state.Folder is not null) return new FolderRemote(state.Folder);
        if (state.Config.Channels is not { } known) throw new NoDriveException();
        // the account's shared connection: one sign-in, one rate-limit governor for all its drives
        TelegramAccounts.MigrateLegacy(state);
        var lease = TelegramAccounts.Acquire(state);
        TelegramRemote remote;
        try { remote = await TelegramRemote.OpenAsync(lease.Client, known, lease.Up, lease.Down, ownsClient: false, release: lease); }
        catch { lease.Dispose(); throw; }
        bool changed = state.Config.Channels != remote.Channels;
        state.Config.Channels = remote.Channels;
        if (remote.Account is { } u)
        {
            var name = string.IsNullOrEmpty(u.username) ? $"{u.first_name} {u.last_name}".Trim() : "@" + u.username;
            changed |= state.Config.AccountId != u.id || state.Config.AccountName != name;
            state.Config.AccountId = u.id;
            state.Config.AccountName = name;
        }
        if (changed) state.Save();
        return remote;
    }

    /// <summary>Open everything and mount. `unlock` turns the vault header into keys (remembered key or a prompt).</summary>
    public static async Task<DriveSession> StartAsync(AppState state,
        Func<ControlState, Task<Keys>> unlock, string? mountPoint, bool takeOver, CancellationToken ct = default)
    {
        if (!WinFspInstalled()) throw new WinFspMissingException();
        mountPoint ??= state.Config.MountPoint;
        mountPoint = mountPoint.TrimEnd('\\');
        if (!mountPoint.EndsWith(':')) mountPoint += ":";
        Semaphore? instance = new Semaphore(1, 1, InstanceName(state));
        if (!instance.WaitOne(0)) { instance.Dispose(); throw new AlreadyMountedException(); }

        string? letterNote = null;
        try
        {
            if (LetterInUse(mountPoint[0]))
            {
                // e.g. a USB stick took the letter: use the next free one, and remember it
                var taken = AppState.All().Where(a => a.Dir != state.Dir).Select(a => char.ToUpperInvariant(a.Config.MountPoint[0])).ToHashSet();
                var free = "TUVWXYZSRQPONMLKJIHGFED".FirstOrDefault(c => !LetterInUse(c) && !taken.Contains(c));
                if (free == default) throw new MountFailedException(L.T("אין אות כונן פנויה.", "No free drive letter."));
                letterNote = $"{mountPoint} → {free}:";
                try { ShellIntegration.ClearDriveIcon(mountPoint); } catch { } // the other device must not wear our icon
                mountPoint = $"{free}:";
                state.Config.MountPoint = mountPoint;
                state.Save();
            }
        }
        catch
        {
            instance.Release(); // never leave the drive "already open" for the rest of this run
            instance.Dispose();
            throw;
        }

        IRemote? remote = null;
        IndexDb? db = null;
        ContentEngine? engine = null;
        SyncService? sync = null;
        try
        {
            remote = await OpenRemoteAsync(state);
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
            var engineRef = engine;
            sync.CanCollectGarbage = () => engineRef.Idle;
            sync.WritableChanged += () => Log.Warn(sync.Writable ? "the drive is now writable" : $"the drive is now READ-ONLY (open on {sync.LeaseHolder})");
            await sync.StartAsync(takeOver, ct);
            if (remote is TelegramRemote telegram)
            {
                await VerifyPairingAsync(db, telegram, ct);
                // drives from before 0.7: record which data channel belongs to this journal, so other
                // accounts (e.g. a backup admin) and other drives on the account can tell them apart
                var identity = await sync.EnsureIdentityAsync(() => new DriveIdentity(
                    Guid.NewGuid().ToString("N"), state.Config.VolumeLabel, telegram.Channels.DataId), ct);
                if (identity is not null && state.Config.DriveId != identity.Id)
                {
                    state.Config.DriveId = identity.Id;
                    state.Save();
                }
            }
            engine.Start();

            var stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, StopEventName(state));
            var session = new DriveSession(state, mountPoint, remote, db, engine, sync, keys, instance!, stopEvent);
            int status = session._host.Mount(mountPoint, null, false, 0);
            if (status < 0)
            {
                await session.StopAsync(waitForUploads: false);
                await session.DisposeAsync();            // host, stop event, engine, sync, index, remote, instance
                sync = null; engine = null; db = null; remote = null; instance = null;
                throw new MountFailedException(L.T($"לא ניתן לחבר את {mountPoint} (NTSTATUS 0x{status:X8}). אולי האות תפוסה? אפשר לבחור אות אחרת ב'הגדרות והתחברות'.",
                                          $"Could not mount {mountPoint} (NTSTATUS 0x{status:X8}). Is the drive letter already in use? Another letter can be chosen in 'Settings and sign-in'."));
            }
            session._mounted = true;
            session.LetterChanged = letterNote;
            if (remote is TelegramRemote tr) tr.SignedOut += () => session.SignedOut?.Invoke();
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
            if (instance is not null)
            {
                try { instance.Release(); } catch (SemaphoreFullException) { }
                instance.Dispose();
            }
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
        try { ShellIntegration.ClearDriveIcon(MountPoint); } catch { }
        Engine.ReleaseAllHandles(); // lazily-closed files would otherwise never commit
        if (waitForUploads && Db.PendingNodes().Count > 0)
        {
            // waits while uploads make progress; gives up after 3 minutes without any (offline / signed
            // out) — the files stay on this PC and upload the next time the drive starts here
            try { await Engine.DrainAsync(progress, ct, stall: TimeSpan.FromMinutes(3)); }
            catch (OperationCanceledException) { }
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try { await Sync.StopAsync(releaseLease: true, deadline.Token); }
        catch (Exception ex) { Log.Warn($"stop: could not reach the journal ({ex.Message}); changes stay queued"); }
    }

    /// <summary>Fast stop for Windows shutdown: unmount and push the journal within a few seconds.</summary>
    public void StopForShutdown()
    {
        try
        {
            if (_mounted) { _host.Unmount(); _mounted = false; }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // the caller is the UI thread: run the async work on the thread pool, or its continuations
            // would queue behind this very wait and nothing would be sent
            Task.Run(() => Sync.QuickReleaseAsync(cts.Token)).Wait(cts.Token);
        }
        catch (Exception ex) { Log.Warn($"shutdown stop: {ex.Message}"); }
        // if the shutdown is cancelled, "Connect" must work at once (not "already open") —
        // released exactly once (ReleaseInstance)
        ReleaseInstance();
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

    public List<TrashItem> Trash() => Db.Trashed().Where(n => n.Parent is null || Db.Get(n.Parent)?.Trashed is null or 0).Select(n =>
    {
        var folder = n.Parent is null ? @"\" : PathOf(n.Parent);
        return new TrashItem(n.Id, n.Name, MountPoint + folder, n.Dir, n.Size, DateTime.FromFileTimeUtc(n.Trashed).ToLocalTime());
    }).ToList();

    /// <summary>Put an item back where it was (or in the root if that folder is gone); returns where it went.</summary>
    public string Restore(string id)
    {
        if (!Writable) throw new InvalidOperationException(L.T("הכונן במצב קריאה בלבד במחשב הזה כרגע.", "The drive is read-only on this PC right now."));
        var n = Db.Get(id) ?? throw new FileNotFoundException(L.T("הפריט כבר נמחק לצמיתות (אולי במחשב אחר).", "The item was already deleted for good (perhaps on another PC)."));
        var parent = n.Parent is not null && IsLive(n.Parent) ? n.Parent : IndexDb.RootId;
        var ops = new List<Op> { new RestoreOp(n.Id, parent, n.Name) };
        if (n.Dir)
        {
            // Explorer deletes a folder bottom-up: its content was trashed item by item just before it.
            // Bring back what went with it (not what was deleted separately, long before).
            long window = TimeSpan.FromMinutes(10).Ticks;
            var stack = new Stack<string>([n.Id]);
            while (stack.Count > 0)
            {
                var folder = stack.Pop();
                var taken = Db.Children(folder).Select(x => IndexDb.NameKey(x.Name)).ToHashSet();
                // newest-trashed first: an older version replaced shortly before must not take the name
                foreach (var c in Db.ChildrenAll(folder).OrderByDescending(x => x.Trashed))
                {
                    if (c.Trashed != 0)
                    {
                        if (c.Trashed > n.Trashed || n.Trashed - c.Trashed > window) continue; // deleted on its own
                        if (!taken.Add(IndexDb.NameKey(c.Name))) continue; // an older version of a restored file
                        ops.Add(new RestoreOp(c.Id, c.Parent!, c.Name));
                    }
                    if (c.Dir) stack.Push(c.Id);
                }
            }
        }
        Db.ApplyLocal(ops);
        var path = PathOf(n.Id);
        Notify(path, NotifyAction.Added, n.Dir);
        return MountPoint + path;
    }

    public void DeleteForever(string id)
    {
        if (!Writable) throw new InvalidOperationException(L.T("הכונן במצב קריאה בלבד במחשב הזה כרגע.", "The drive is read-only on this PC right now."));
        Db.ApplyLocal(new PurgeOp(id));
    }

    // ── Explorer change notifications ────────────────────────────────────────
    readonly Dictionary<string, string> _oldPaths = new();

    void NoteOldPaths(IReadOnlyList<Op> ops)
    {
        _oldPaths.Clear();
        foreach (var op in ops)
            if (op is UpsertOp or RestoreOp && Db.Get(op.Id) is { Trashed: 0 }) _oldPaths[op.Id] = PathOf(op.Id);
    }

    void Notify(string path, NotifyAction action, bool dir, NotifyFilter? filter = null)
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
                    Filter = filter ?? (dir ? NotifyFilter.ChangeDirName : NotifyFilter.ChangeFileName),
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
            var path = PathOf(n.Id);
            if (_oldPaths.TryGetValue(op.Id, out var old) && !string.Equals(old, path, StringComparison.Ordinal))
                Notify(old, NotifyAction.Removed, n.Dir); // renamed / moved: the old name must go away
            if (op is ContentOp)
                Notify(path, NotifyAction.Modified, false, NotifyFilter.ChangeSize | NotifyFilter.ChangeLastWrite);
            else
                Notify(path, op is TrashOp or PurgeOp ? NotifyAction.Removed : NotifyAction.Added, n.Dir);
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
        ReleaseInstance();
        _instance.Dispose();
    }

    int _instanceReleased;

    /// <summary>Exactly once: a second release could free the lock a NEW session of this drive holds.</summary>
    void ReleaseInstance()
    {
        if (Interlocked.Exchange(ref _instanceReleased, 1) == 1) return;
        try { _instance.Release(); } catch (SemaphoreFullException) { }
    }
}
