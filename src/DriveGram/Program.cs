using DriveGram;
using DriveGram.App;
using DriveGram.Crypto;
using DriveGram.Engine;
using DriveGram.Index;
using DriveGram.Remote;
using DriveGram.Sync;
using Fsp;

Console.OutputEncoding = System.Text.Encoding.UTF8;
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

var folder = Opt("--folder");
bool verbose = Flag("--verbose");
var state = new AppState(folder);
Log.Open(state.LogDir);
Log.Console = true;
Log.Verbose = verbose;
WTelegram.Helpers.Log = (level, msg) => { if (level >= 3) Log.Warn("[tg] " + msg); else Log.Debug("[tg] " + msg); };

var command = argv.Count > 0 ? argv[0].ToLowerInvariant() : "help";
try
{
    return command switch
    {
        "login" => await Login(),
        "init" => await Init(),
        "mount" => await Mount(argv.Count > 1 ? argv[1] : null, Flag("--take-over")),
        "status" => await Status(),
        "unmount" => Unmount(),
        "forget-password" => ForgetPassword(),
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

int Help()
{
    Console.WriteLine("""
        DriveGram — a Windows drive whose files live (encrypted) in your Telegram.

          drivegram login [--env <file>] [--phone]
                                           Sign in to Telegram on this PC (once), by scanning a QR
                                           code with your phone. --phone: use a login code instead.
                                           --env reads API_ID / API_HASH from a .env file.
          drivegram init                   Create the drive (new password), or connect this PC
                                           to an existing drive (its password).
          drivegram mount [T:] [--take-over]
                                           Mount the drive. Ctrl+C unmounts after uploads finish.
          drivegram unmount                Ask a running `mount` to finish uploads and unmount.
          drivegram status                 Show what is stored and what is waiting to upload.
          drivegram forget-password        Stop remembering the drive password on this PC.

        Try it without Telegram: add --folder <path> to init / mount / status.
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

WTelegram.Client NewClient()
{
    var cfg = state.Config;
    if (cfg.ApiId == 0 || string.IsNullOrEmpty(cfg.ApiHash))
        throw new InvalidOperationException("Telegram API credentials are missing. Run `drivegram login` first.");
    string? Config(string what) => what switch
    {
        "api_id" => cfg.ApiId.ToString(),
        "api_hash" => cfg.ApiHash,
        "phone_number" => Ask("Phone number (international format, e.g. +972...): "),
        "verification_code" => Ask("Login code Telegram just sent you: "),
        "password" => Ask("Telegram two-step verification password: ", secret: true),
        "first_name" or "last_name" => throw new InvalidOperationException("This phone number has no Telegram account."),
        "device_model" => "DriveGram on " + Environment.MachineName,
        "app_version" => "DriveGram 0.1",
        _ => null,
    };
    return new WTelegram.Client(Config, new DpapiSessionStream(state.SessionPath));
}

async Task<IRemote> OpenRemote(bool create)
{
    if (state.Folder is not null) return new FolderRemote(state.Folder);
    var remote = await TelegramRemote.OpenAsync(NewClient(), state.Config.Channels, create);
    if (state.Config.Channels != remote.Channels)
    {
        state.Config.Channels = remote.Channels;
        state.Save();
    }
    return remote;
}

async Task<int> Login()
{
    if (state.Folder is not null) { Console.WriteLine("--folder needs no login."); return 0; }
    bool usePhone = Flag("--phone");
    var env = Opt("--env");
    if (env is not null)
    {
        foreach (var line in File.ReadAllLines(env))
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
    using var client = NewClient();
    TL.User user;
    if (usePhone)
    {
        Console.WriteLine("The code arrives INSIDE the Telegram app (chat \"Telegram\" with the blue tick), usually not by SMS.");
        Console.WriteLine("Each new attempt cancels the previous code — always use the newest one.");
        user = await client.LoginUserIfNeeded();
    }
    else
    {
        await client.ConnectAsync();
        user = client.User ?? await client.LoginWithQRCode(ShowQr, logoutFirst: false);
    }
    Console.WriteLine($"Signed in as {user.first_name} {user.last_name} (@{user.username}). The session is stored encrypted for this Windows user only.");
    return 0;
}

// Called again with a fresh token every ~30 s until the phone scans it.
void ShowQr(string url)
{
    using var gen = new QRCoder.QRCodeGenerator();
    using var data = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.L);
    var png = Path.Combine(state.Dir, "login-qr.png");
    File.WriteAllBytes(png, new QRCoder.PngByteQRCode(data).GetGraphic(10));
    Console.WriteLine();
    Console.WriteLine(new QRCoder.AsciiQRCode(data).GetGraphicSmall());
    Console.WriteLine("""
        Scan with your phone: Telegram -> Settings -> Devices -> Link Desktop Device.
        (The code refreshes every ~30 seconds. If the console picture does not scan, use the image that just opened.)
        """);
    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(png) { UseShellExecute = true }); }
    catch (Exception ex) { Log.Debug($"could not open {png}: {ex.Message}"); }
}

async Task<Keys> Unlock(ControlState ctrl, bool offerRemember)
{
    if (state.RememberedMaster() is { } master)
    {
        try { return Keys.FromMaster(ctrl.Vault, master); }
        catch (WrongPasswordException) { state.ForgetMaster(); }
    }
    for (int attempt = 1; ; attempt++)
    {
        var pw = Ask("Drive password: ", secret: true);
        try
        {
            var keys = Keys.Unlock(ctrl.Vault, pw);
            if (offerRemember && Ask("Remember the password on this PC (so the drive can mount without asking)? [y/N] ")
                    .StartsWith("y", StringComparison.OrdinalIgnoreCase))
                state.RememberMaster(keys.ExportMaster());
            return keys;
        }
        catch (WrongPasswordException) when (attempt < 3) { Console.WriteLine("Wrong password, try again."); }
    }
}

async Task<int> Init()
{
    await using var remote = await OpenRemote(create: true);
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
        var keys = await SyncService.CreateDriveAsync(remote, pw, default);
        if (Ask("Remember the password on this PC (so the drive can mount without asking)? [y/N] ")
                .StartsWith("y", StringComparison.OrdinalIgnoreCase))
            state.RememberMaster(keys.ExportMaster());
        Console.WriteLine("Drive created. Next: drivegram mount" + (state.Folder is null ? "" : $" --folder \"{state.Folder}\""));
    }
    else
    {
        Console.WriteLine("This account already has a DriveGram drive. Enter its password to connect this PC.");
        await Unlock(ctrl, offerRemember: true);
        Console.WriteLine("Connected. Next: drivegram mount");
    }
    return 0;
}

async Task<int> Status()
{
    await using var remote = await OpenRemote(create: false);
    var ctrl = await SyncService.ReadControlAsync(remote, default) ?? throw new InvalidOperationException("No drive yet. Run `drivegram init`.");
    using var db = IndexDb.Open(state.IndexPath);
    var (files, dirs) = db.Counts();
    Console.WriteLine($"Files: {files:N0}   Folders: {dirs:N0}   Size: {db.UsedBytes() / 1048576.0:N1} MiB (as of this PC's last sync)");
    Console.WriteLine($"Waiting to upload: {db.PendingNodes().Count} files;  unsent changes: {db.OutboxCount()}");
    Console.WriteLine($"In trash: {db.Trashed().Count}");
    var lease = ctrl.Lease;
    Console.WriteLine(lease is null || lease.Until < DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        ? "Not open for writing on any PC."
        : $"Open for writing on: {lease.Name}{(lease.Dev == db.DeviceId ? " (this PC)" : "")}");
    return 0;
}

string StopEventName() => @"Local\DriveGram-stop-" + Path.GetFileName(state.Dir);

int Unmount()
{
    if (!EventWaitHandle.TryOpenExisting(StopEventName(), out var ev))
    {
        Console.WriteLine("DriveGram is not mounted.");
        return 0;
    }
    ev.Set();
    Console.WriteLine("Asked the running DriveGram to unmount (it finishes pending uploads first).");
    return 0;
}

int ForgetPassword()
{
    state.ForgetMaster();
    Console.WriteLine("The drive password is no longer stored on this PC.");
    return 0;
}

async Task<int> Mount(string? mountPoint, bool takeOver)
{
    try { _ = FileSystemHost.Version(); }
    catch (Exception)
    {
        Console.Error.WriteLine("WinFsp is not installed. Install it from https://winfsp.dev/rel/ (version 2.1) and try again.");
        return 3;
    }
    mountPoint ??= state.Config.MountPoint;
    if (!mountPoint.EndsWith(':')) mountPoint += ":";

    await using var remote = await OpenRemote(create: false);
    var ctrl = await SyncService.ReadControlAsync(remote, default) ?? throw new InvalidOperationException("No drive yet. Run `drivegram init`.");
    var keys = await Unlock(ctrl, offerRemember: true);

    using var db = IndexDb.Open(state.IndexPath);
    var cfg = state.Config;
    await using var engine = new ContentEngine(db, remote, keys, state.Dir, new EngineOptions
    {
        StagingQuota = (long)(cfg.StagingQuotaGb * (1L << 30)),
        CacheQuota = (long)(cfg.CacheQuotaGb * (1L << 30)),
        UploadConcurrency = cfg.UploadConcurrency,
    });
    var sync = new SyncService(db, remote, keys);
    sync.WritableChanged += () => Log.Warn(sync.Writable ? "the drive is now writable" : $"the drive is now READ-ONLY (open on {sync.LeaseHolder})");

    Console.WriteLine("Syncing the folder tree...");
    await sync.StartAsync(takeOver, default);
    engine.Start();
    if (!sync.Writable)
        Console.WriteLine($"Read-only: the drive is open for writing on '{sync.LeaseHolder}'. Close it there (or use --take-over if that PC is gone).");

    var fs = new DriveFileSystem(db, engine, sync, cfg.VolumeLabel);
    var host = new FileSystemHost(fs);
    int status = host.Mount(mountPoint, null, false, 0);
    if (status < 0)
    {
        await sync.StopAsync(releaseLease: true, default);
        throw new IOException($"Could not mount {mountPoint} (NTSTATUS 0x{status:X8}). Is the drive letter already in use?");
    }
    Console.WriteLine($"DriveGram is mounted at {mountPoint}  — press Ctrl+C to unmount.");

    var stop = new TaskCompletionSource();
    using var stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, StopEventName());
    _ = Task.Run(() => { stopEvent.WaitOne(); stop.TrySetResult(); });
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
        var s = engine.Status;
        var line = $"pending {s.PendingFiles} files | uploading {s.UploadsInFlight} | staged {s.StagedBytes / 1048576.0:N0} MiB | " +
                   $"up {s.BytesUploaded / 1048576.0:N0} MiB | down {s.BytesDownloaded / 1048576.0:N0} MiB | cache {s.CacheBytes / 1048576.0:N0} MiB" +
                   (sync.Writable ? "" : " | READ-ONLY");
        if (line != lastLine) { Console.WriteLine(line); lastLine = line; }
    }

    Console.WriteLine("Unmounting...");
    host.Unmount();
    if (db.PendingNodes().Count > 0)
    {
        Console.WriteLine("Finishing uploads before releasing the drive (Ctrl+C again to quit now)...");
        await engine.DrainAsync(st => Console.Write($"\r  {st.PendingFiles} files left, {st.StagedBytes / 1048576.0:N0} MiB staged      "), default);
        Console.WriteLine();
    }
    await sync.StopAsync(releaseLease: true, default);
    Console.WriteLine("Done. The drive can now be opened for writing on another PC.");
    return 0;
}
