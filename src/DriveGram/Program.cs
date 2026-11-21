using DriveGram;
using DriveGram.Crypto;
using DriveGram.Host;
using DriveGram.Index;
using DriveGram.Sync;

Console.OutputEncoding = System.Text.Encoding.UTF8;
L.Override("en"); // a console shows Hebrew reversed: the CLI speaks English
var argv = args.ToList();

string? Opt(string name)
{
    int i = argv.IndexOf(name);
    if (i < 0 || i + 1 >= argv.Count) return null;
    var v = argv[i + 1];
    argv.RemoveRange(i, 2);
    return v;
}

bool Flag(string name) => argv.Remove(name);

// options first, so positional arguments are what is left
var folder = Opt("--folder");
var envFile = Opt("--env");
bool verbose = Flag("--verbose");
bool checkOnly = Flag("--check");
bool takeOver = Flag("--take-over");
bool usePhone = Flag("--phone");
var pickArg = Opt("--pick");
var nameArg = Opt("--name");
DriveGram.Sync.DriveIdentity? newIdentity = null; // set when `init` creates a brand-new drive

var driveSel = Opt("--drive");
Log.Open(Path.Combine(AppState.Root, "logs"));
var command0 = argv.Count > 0 ? argv[0].ToLowerInvariant() : "help";
if (folder is null && command0 is "setup" or "login" or "init" or "mount" or "status" or "uninstall-cleanup"
    && System.Threading.Mutex.TryOpenExisting("DriveGramTrayRunning", out var trayRunning))
{
    trayRunning.Dispose();
    Console.Error.WriteLine("DriveGram is running in the notification area. Exit it first (tray icon > Exit): two programs must not use the same Telegram sign-in at once.");
    return 3;
}

AppState state;
try { state = folder is not null ? AppState.ForFolder(folder) : SelectDrive(driveSel, command0); }
catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }

// Which drive a command acts on: --drive <letter|id>, else the only one, else the first one.
static AppState SelectDrive(string? sel, string command)
{
    var all = AppState.All().Where(a => a.Folder is null).ToList();
    if (sel is not null)
    {
        var key = sel.TrimEnd(':', '\\').ToUpperInvariant();
        return all.FirstOrDefault(a => a.Config.MountPoint.TrimEnd(':').Equals(key, StringComparison.OrdinalIgnoreCase)
                                       || a.Id.Equals(sel, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"No drive '{sel}'. See: drivegram drives");
    }
    if (all.Count == 0) return AppState.Open("telegram");                 // first drive on this PC
    if (command is "setup" or "login" or "init" && all.Count > 0 && all.All(a => a.Config.Channels is not null))
        return command == "setup" ? AppState.CreateNew() : all[0];         // `setup` with drives already = add one
    return all[0];
}
Log.Console = true;
Log.Verbose = verbose;
WTelegram.Helpers.Log = (level, msg) => { if (level >= 3) Log.Warn("[tg] " + msg); else Log.Debug("[tg] " + msg); };

var command = argv.Count > 0 ? argv[0].ToLowerInvariant() : "help";
string? arg1 = argv.Count > 1 ? argv[1] : null;
try
{
    return command switch
    {
        "setup" => await Setup(),
        "login" => await Login(),
        "init" => await Init(),
        "mount" => await Mount(arg1),
        "unmount" => Unmount(),
        "status" => await Status(),
        "drives" => Drives(),
        "export" => await Export(arg1),
        "quit" => Quit(),
        "autostart" => Autostart(arg1),
        "forget-password" => ForgetPassword(),
        "uninstall-cleanup" => await UninstallCleanup(checkOnly),
        _ => Help(),
    };
}
catch (WrongPasswordException ex) { Console.Error.WriteLine(ex.Message); return 2; }
catch (Exception ex)
{
    Log.Error("fatal", ex);
    Console.Error.WriteLine($"\nError: {ex.Message}");
    return 1;
}

// Used by the uninstaller (after the user agreed): sign every account out of Telegram on this PC and
// delete everything DriveGram keeps here. Files in Telegram are not touched. With --check: exit code 4
// when something exists only on this PC (files not uploaded yet / changes not sent).
async Task<int> UninstallCleanup(bool check)
{
    int localOnly = 0;
    foreach (var s in AppState.All())
    {
        var (pending, unsent) = DriveDeletion.LocalOnly(s);
        localOnly += pending < 0 ? 1 : pending + unsent;
    }
    if (check) return localOnly > 0 ? 4 : 0;
    await TelegramAccounts.SignOutAllAsync();
    Log.Close(); // the log file lives in the folder being deleted
    for (int attempt = 0; attempt < 5 && Directory.Exists(AppState.Root); attempt++)
    {
        try { Directory.Delete(AppState.Root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(1000); }
    }
    return Directory.Exists(AppState.Root) ? 1 : 0;
}

int Help()
{
    Console.WriteLine("""
        DriveGram — a Windows drive whose files live (encrypted) in your Telegram.

          drivegram setup                  First run: sign in to Telegram + create/connect the drive.
          drivegram login [--env <file>] [--phone]
                                           Sign in to Telegram on this PC (once), by scanning a QR
                                           code with your phone. --phone: use a login code instead.
                                           --env reads API_ID / API_HASH from a .env file.
          drivegram init                   Create the drive (new password), or connect this PC
                                           to an existing drive (its password).
          drivegram-tray                   Run the drive from the notification area (normal use).
          drivegram autostart on|off       Start drivegram-tray with Windows.
          drivegram mount [T:] [--take-over]
                                           Mount in this console (Ctrl+C unmounts after uploads finish).
          drivegram unmount                Ask the running drive (console or tray) to unmount.
          drivegram status                 Show what is stored and what is waiting to upload.
          drivegram drives                 List the drives on this PC.
          drivegram export <folder>        Copy every file of a mounted drive to a folder (resumable).
          drivegram quit                   Close the tray app (it finishes uploads first).

        Several drives: add --drive <letter> (e.g. --drive U:) to choose one; default is the first.
          drivegram forget-password        Stop remembering the drive password on this PC.

        Try it without Telegram: add --folder <path> to init / mount / status / unmount.
        """);
    return 0;
}

static string Ask(string prompt, bool secret = false)
{
    Console.Write(prompt);
    if (!secret || Console.IsInputRedirected) // piped input (scripts, tests): plain lines
    {
        var line = Console.ReadLine()?.Trim() ?? "";
        if (secret) Console.WriteLine();
        return line;
    }
    var chars = new List<char>();
    while (true)
    {
        var k = Console.ReadKey(intercept: true);
        if (k.Key == ConsoleKey.Enter) break;
        if (k.Key == ConsoleKey.Backspace) { if (chars.Count > 0) { chars.RemoveAt(chars.Count - 1); Console.Write("\b \b"); } continue; }
        if (!char.IsControl(k.KeyChar)) { chars.Add(k.KeyChar); Console.Write('*'); }
    }
    Console.WriteLine();
    return new string(chars.ToArray());
}

static string TwoFactor() => Ask("Telegram two-step verification password: ", secret: true);

/// <summary>Everything a new PC needs, in one guided run (the tray opens this on first use).</summary>
async Task<int> Setup()
{
    Console.WriteLine("""
        === DriveGram setup ===
        Step 1 of 2: sign in to Telegram (scan a QR code with your phone).

        """);
    await Login();
    Console.WriteLine("""

        Step 2 of 2: the drive itself.

        """);
    await Init();
    Console.WriteLine("\nAll set. Now start DriveGram from the Start menu: it mounts the drive.");
    if (!Console.IsInputRedirected) Console.ReadLine();
    return 0;
}

async Task<int> Login()
{
    if (state.Folder is not null) { Console.WriteLine("--folder needs no login."); return 0; }
    if (envFile is not null)
    {
        foreach (var line in File.ReadAllLines(envFile))
        {
            var kv = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) continue;
            if (kv[0] == "API_ID" && int.TryParse(kv[1], out var id)) state.Config.ApiId = id;
            if (kv[0] == "API_HASH") state.Config.ApiHash = kv[1].Trim('"');
        }
    }
    if (state.Config.ApiId == 0)
    {
        Console.WriteLine("Telegram API credentials (from https://my.telegram.org -> API development tools).");
        state.Config.ApiId = int.Parse(Ask("api_id: "));
        state.Config.ApiHash = Ask("api_hash: ");
    }
    state.Save();
    Console.WriteLine("Tip: use a dedicated Telegram account for DriveGram, not your personal one.");
    DriveGram.Host.TelegramAccounts.MigrateLegacy(state);
    var lease = DriveGram.Host.TelegramAccounts.Acquire(state, TwoFactor);
    using var _ = lease;
    var client = lease.Client;
    TL.User user;
    try
    {
        user = await DriveGram.Remote.TelegramRemote.ResumeAsync(client);
        Console.WriteLine($"Already signed in as {user.first_name} {user.last_name} (@{user.username}).");
        return 0;
    }
    catch (DriveGram.Remote.NotSignedInException) { }

    if (usePhone)
    {
        Console.WriteLine("The code arrives INSIDE the Telegram app (chat \"Telegram\" with the blue tick), usually not by SMS.");
        string? next = await client.Login(Ask("Phone number (international format, e.g. +972...): "));
        while (next is not null)
        {
            next = next switch
            {
                "verification_code" => await client.Login(Ask("Login code: ")),
                "password" => await client.Login(TwoFactor()),
                _ => throw new InvalidOperationException($"Telegram asked for '{next}' (no account for this number?)."),
            };
        }
        user = client.User!;
    }
    else user = await client.LoginWithQRCode(ShowQr, logoutFirst: false);
    using (DriveGram.Host.TelegramAccounts.Adopt(state, lease, user)) { }  // the account's shared sign-in
    state.Save();
    Console.WriteLine($"Signed in as {user.first_name} {user.last_name} (@{user.username}). The session is stored encrypted for this Windows user only.");
    return 0;
}

// Called again with a fresh token every ~30 s until the phone scans it.
static void ShowQr(string url)
{
    using var gen = new QRCoder.QRCodeGenerator();
    using var data = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.L);
    Console.WriteLine();
    Console.WriteLine(new QRCoder.AsciiQRCode(data).GetGraphicSmall());
    Console.WriteLine("Scan with your phone: Telegram -> Settings -> Devices -> Link Desktop Device. (Refreshes every ~30 s.)");
    Console.WriteLine("Tip: the DriveGram tray app has a setup window that shows this as a proper picture.");
}

Task<Keys> Unlock(ControlState ctrl)
{
    if (state.RememberedMaster() is { } master)
    {
        try { return Task.FromResult(Keys.FromMaster(ctrl.Vault, master)); }
        catch (WrongPasswordException) { } // keep it; ask for the password instead
    }
    for (int attempt = 1; ; attempt++)
    {
        var pw = Ask("Drive password (or recovery key): ", secret: true);
        try
        {
            var keys = Keys.UnlockAny(ctrl.Vault, pw); // the password, or the recovery key
            OfferRemember(keys);
            return Task.FromResult(keys);
        }
        catch (WrongPasswordException) when (attempt < 3) { Console.WriteLine("Wrong password, try again."); }
    }
}

void OfferRemember(Keys keys)
{
    if (Ask("Remember the password on this PC (needed for starting with Windows)? [y/N] ")
            .StartsWith("y", StringComparison.OrdinalIgnoreCase))
        state.RememberMaster(keys.ExportMaster());
}

async Task<int> Init()
{
    await using var remote = await OpenOrPickDriveAsync();
    var ctrl = await SyncService.ReadControlAsync(remote, default);
    if (ctrl is null)
    {
        Console.WriteLine("""
            Creating a new drive. Choose its password: it encrypts everything, and it CANNOT be recovered.
            Without it nobody (including you) can read the files, even with full access to the Telegram account.
            """);
        string pw;
        while (true)
        {
            pw = Ask("New drive password (at least 10 characters): ", secret: true);
            if (pw.Length < 10) { Console.WriteLine("Too short."); continue; }
            if (Ask("Repeat it: ", secret: true) == pw) break;
            Console.WriteLine("They do not match.");
        }
        var keys = await SyncService.CreateDriveAsync(remote, pw, default, identity: newIdentity);
        state.Save();
        OfferRemember(keys);
        Console.WriteLine("Drive created. Next: drivegram-tray (or: drivegram mount)");
    }
    else
    {
        if (state.RememberedMaster() is { } master)
        {
            try
            {
                Keys.FromMaster(ctrl.Vault, master);
                Console.WriteLine("This PC is already connected to the drive.");
                return 0;
            }
            catch (WrongPasswordException) { state.ForgetMaster(); }
        }
        Console.WriteLine("This account already has a DriveGram drive. Enter its password to connect this PC.");
        await Unlock(ctrl);
        Console.WriteLine("Connected. Next: drivegram-tray (or: drivegram mount)");
    }
    return 0;
}

async Task<int> Status()
{
    await using var remote = await DriveSession.OpenRemoteAsync(state);
    var ctrl = await SyncService.ReadControlAsync(remote, default) ?? throw new DriveGram.Remote.NoDriveException();
    using var db = IndexDb.Open(state.IndexPath);
    var (files, dirs) = db.Counts();
    Console.WriteLine($"Files: {files:N0}   Folders: {dirs:N0}   Size: {db.UsedBytes() / 1048576.0:N1} MiB (as of this PC's last sync)");
    Console.WriteLine($"Waiting to upload: {db.PendingNodes().Count} files;  unsent changes: {db.OutboxCount()}");
    Console.WriteLine($"In trash: {db.Trashed().Count}");
    var lease = ctrl.Lease;
    Console.WriteLine(lease is null || lease.Until < DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        ? "Not open for writing on any PC."
        : $"Open for writing on: {lease.Name}{(lease.Dev == db.DeviceId ? " (this PC)" : "")}");
    Console.WriteLine($"Start with Windows: {(ShellIntegration.AutostartEnabled ? "on" : "off")}");
    return 0;
}

// `unmount` without --drive unmounts every drive (the installer relies on this).
// Which drive `init` sets up: the one this profile knows; else one found on the account (--pick N);
// else a new one (--name). Drives are told apart by their identity, never by channel names.
async Task<DriveGram.Remote.IRemote> OpenOrPickDriveAsync()
{
    if (state.Folder is not null) return new DriveGram.Remote.FolderRemote(state.Folder);
    var client = DriveSession.NewTelegramClient(state, TwoFactor);
    if (state.Config.Channels is null)
    {
        var onPc = AppState.All().Where(a => a.Dir != state.Dir && a.Config.Channels is not null).Select(a => a.Config.Channels!.JournalId).ToHashSet();
        var found = (await DriveGram.Remote.TelegramRemote.DiscoverAsync(client))
            .Where(c => c.Channels is not null && c.HasVault && !onPc.Contains(c.Channels.JournalId)).ToList();
        int pick = int.TryParse(pickArg, out var p) ? p : (found.Count == 1 ? 1 : 0);
        if (found.Count > 1 && pickArg is null)
        {
            Console.WriteLine("This account can open these drives:");
            for (int i = 0; i < found.Count; i++)
                Console.WriteLine($"  {i + 1}. {found[i].Name}{(found[i].Owned ? "" : " (you are an admin)")}");
            Console.WriteLine("Run again with --pick <number>, or --pick 0 for a new drive (--name <name>).");
            throw new OperationCanceledException("no drive chosen");
        }
        if (pick >= 1 && pick <= found.Count)
        {
            state.Config.Channels = found[pick - 1].Channels;
            state.Config.DriveId = found[pick - 1].DriveId ?? "";
            state.Config.VolumeLabel = found[pick - 1].Name;
        }
        else
        {
            var name = nameArg ?? "DriveGram";
            var id = Guid.NewGuid().ToString("N");
            state.Config.Channels = await DriveGram.Remote.TelegramRemote.CreateChannelsAsync(client, name, id);
            state.Config.DriveId = id;
            state.Config.VolumeLabel = name;
            newIdentity = new(id, name, state.Config.Channels.DataId);
        }
        state.Save();
    }
    return await DriveGram.Remote.TelegramRemote.OpenAsync(client, state.Config.Channels!);
}

int Unmount()
{
    var targets = driveSel is not null || folder is not null ? [state] : AppState.All();
    int n = 0;
    foreach (var t in targets)
    {
        if (!EventWaitHandle.TryOpenExisting(DriveSession.StopEventName(t), out var ev)) continue;
        ev.Set();
        n++;
        Console.WriteLine($"Asked {t.Config.MountPoint} to unmount (it finishes pending uploads first).");
    }
    if (n == 0) Console.WriteLine("No DriveGram drive is mounted.");
    return 0;
}

async Task<int> Export(string? target)
{
    if (string.IsNullOrWhiteSpace(target)) { Console.Error.WriteLine("Usage: drivegram export <folder> [--drive T:]"); return 1; }
    var root = state.Config.MountPoint.TrimEnd('\\') + "\\";
    if (!EventWaitHandle.TryOpenExisting(DriveSession.StopEventName(state), out _) || !Directory.Exists(root))
    {
        Console.Error.WriteLine($"{state.Config.MountPoint} is not mounted. Start DriveGram (or `drivegram mount`) first.");
        return 1;
    }
    var full = Path.GetFullPath(target);
    if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine("Choose a folder outside the drive."); return 1; }
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    var last = DateTime.MinValue;
    var progress = new ConsoleProgress(p =>
    {
        if ((DateTime.UtcNow - last).TotalSeconds < 1 && p.Current.Length > 0) return;
        last = DateTime.UtcNow;
        Console.Write($"\r  {p.Files:N0} copied, {p.Skipped:N0} already there, {p.Bytes / 1048576.0:N0} MiB      ");
    });
    try
    {
        var errors = await Exporter.CopyAsync(root, full, progress, cts.Token);
        Console.WriteLine();
        foreach (var e in errors) Console.Error.WriteLine("  failed: " + e);
        Console.WriteLine(errors.Count == 0 ? "Export complete." : $"Done; {errors.Count} files failed (run again to retry them).");
        return errors.Count == 0 ? 0 : 1;
    }
    catch (OperationCanceledException) { Console.WriteLine("\nStopped. Run the same command again to continue."); return 1; }
}

int Quit()
{
    if (!EventWaitHandle.TryOpenExisting(DriveSession.QuitEventName, out var ev))
    {
        Console.WriteLine("The DriveGram tray app is not running.");
        return 0;
    }
    ev.Set();
    Console.WriteLine("Asked the DriveGram tray app to close (it finishes pending uploads first).");
    return 0;
}

int Drives()
{
    var all = AppState.All();
    if (all.Count == 0) { Console.WriteLine("No drives yet. Run: drivegram setup (or open DriveGram from the Start menu)."); return 0; }
    foreach (var a in all)
    {
        bool mounted = EventWaitHandle.TryOpenExisting(DriveSession.StopEventName(a), out _);
        Console.WriteLine($"{a.Config.MountPoint,-3} {a.Config.VolumeLabel,-16} {(a.Folder ?? a.Config.AccountName),-28} {(mounted ? "mounted" : "")}  [{a.Id}]");
    }
    return 0;
}

int Autostart(string? onOff)
{
    if (onOff is "on" or "off") ShellIntegration.SetAutostart(onOff == "on");
    Console.WriteLine($"Start with Windows: {(ShellIntegration.AutostartEnabled ? "on" : "off")}");
    if (ShellIntegration.AutostartEnabled && state.RememberedMaster() is null)
        Console.WriteLine("Note: the drive password is not remembered on this PC, so the tray will ask for it at sign-in.");
    return 0;
}

int ForgetPassword()
{
    state.ForgetMaster();
    Console.WriteLine("The drive password is no longer stored on this PC.");
    return 0;
}

async Task<int> Mount(string? mountPoint)
{
    if (!DriveSession.WinFspInstalled())
    {
        Console.Error.WriteLine("WinFsp is not installed. Install it from https://winfsp.dev/rel/ (version 2.1) and try again.");
        return 3;
    }
    Console.WriteLine("Syncing the folder tree...");
    await using var session = await DriveSession.StartAsync(state, Unlock, mountPoint, takeOver);
    if (!session.Writable)
        Console.WriteLine($"Read-only: the drive is open for writing on '{session.Sync.LeaseHolder}'. Close it there (or use --take-over if that PC is gone).");
    Console.WriteLine($"DriveGram is mounted at {session.MountPoint}  — press Ctrl+C to unmount.");

    var stop = new TaskCompletionSource();
    session.StopRequested += () => stop.TrySetResult();
    int ctrlC = 0;
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        if (Interlocked.Increment(ref ctrlC) == 1) stop.TrySetResult();
        else
        {
            Console.WriteLine("\nQuitting now. Unfinished uploads continue the next time you mount (on this PC).");
            Environment.Exit(4);
        }
    };

    string lastLine = "";
    while (!stop.Task.IsCompleted)
    {
        await Task.WhenAny(stop.Task, Task.Delay(2000));
        var s = session.Status;
        var line = $"pending {s.PendingFiles} files | uploading {s.UploadsInFlight} | staged {s.StagedBytes / 1048576.0:N0} MiB | " +
                   $"up {s.BytesUploaded / 1048576.0:N0} MiB | down {s.BytesDownloaded / 1048576.0:N0} MiB | cache {s.CacheBytes / 1048576.0:N0} MiB" +
                   (session.Writable ? "" : " | READ-ONLY");
        if (line != lastLine) { Console.WriteLine(line); lastLine = line; }
    }

    Console.WriteLine("Unmounting...");
    if (session.Db.PendingNodes().Count > 0) Console.WriteLine("Finishing uploads before releasing the drive (Ctrl+C again to quit now)...");
    await session.StopAsync(waitForUploads: true,
        st => Console.Write($"\r  {st.PendingFiles} files left, {st.StagedBytes / 1048576.0:N0} MiB staged      "));
    Console.WriteLine("\nDone. The drive can now be opened for writing on another PC.");
    return 0;
}

/// <summary>Reports synchronously on the copying thread, one line at a time (Progress&lt;T&gt; would post to the
/// thread pool in a console app and could print after the final summary).</summary>
sealed class ConsoleProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
{
    readonly object _gate = new();
    public void Report(ExportProgress value) { lock (_gate) report(value); }
}
