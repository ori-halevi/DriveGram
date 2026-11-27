using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DriveGram.Remote;

namespace DriveGram.Host;

/// <summary>Settings of one drive on this PC, in %LOCALAPPDATA%\DriveGram\&lt;profile&gt;\config.json.</summary>
public sealed class AppConfig
{
    public int ApiId { get; set; }
    public string ApiHash { get; set; } = "";
    public TelegramChannels? Channels { get; set; }
    /// <summary>The drive's unique id (from its control record; empty for drives not upgraded yet).</summary>
    public string DriveId { get; set; } = "";
    /// <summary>Telegram user the drive lives on (0 = not known yet), and a display name for menus.</summary>
    public long AccountId { get; set; }
    /// <summary>Folder under accounts\ holding the account's shared sign-in ("" = the profile's own).</summary>
    public string AccountKey { get; set; } = "";
    public string AccountName { get; set; } = "";
    /// <summary>Local folder instead of Telegram (offline trial drives).</summary>
    public string? Folder { get; set; }
    public string MountPoint { get; set; } = "T:";
    public string VolumeLabel { get; set; } = "DriveGram";
    /// <summary>Shown to Windows as a network drive (backup/antivirus/indexing tools skip it) instead of a
    /// regular disk. Off by default; chosen in the tray menu ("Drive type…").</summary>
    public bool NetworkDrive { get; set; }
    /// <summary>The user paused this drive's uploads (kept until resumed, also after a restart).</summary>
    public bool UploadsPaused { get; set; }
    public double StagingQuotaGb { get; set; } = 8;
    public double CacheQuotaGb { get; set; } = 10;
    public int UploadConcurrency { get; set; } = 2;
}

/// <summary>
/// Where this PC keeps its state for one drive (a "profile"). Every drive — each on its own
/// Telegram account — has its own folder under %LOCALAPPDATA%\DriveGram: "telegram" (the first
/// one, from before multi-drive support) and "drive-xxxxxxxx" for drives added later.
/// </summary>
public sealed class AppState
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>%LOCALAPPDATA%\DriveGram, or DRIVEGRAM_ROOT (tests / a second isolated setup).</summary>
    public static string Root => Environment.GetEnvironmentVariable("DRIVEGRAM_ROOT") is { Length: > 0 } r
        ? r
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveGram");

    /// <summary>
    /// A name shared by everything that belongs to ONE data folder: the "DriveGram is running" mutex and
    /// the events the command line signals. The usual folder keeps the plain names (what every installed
    /// copy, and the installer, already look for); a folder chosen with DRIVEGRAM_ROOT gets its own, so a
    /// copy run from the source for a look at it lives beside the installed one instead of waking it.
    /// </summary>
    public static string Scoped(string name) => IsUsualRoot
        ? name
        : name + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(Root).TrimEnd('\\', '/').ToUpperInvariant())))[..8];

    /// <summary>Is this the real data folder of this Windows user (and not a copy run for a look at it)?
    /// A copy with its own folder leaves everything machine-wide — Explorer's drive icons, for instance —
    /// to the real one.</summary>
    public static bool IsUsualRoot => string.Equals(
        Path.GetFullPath(Root).TrimEnd('\\', '/'),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveGram"),
        StringComparison.OrdinalIgnoreCase);

    public string Dir { get; }
    public string Id => Path.GetFileName(Dir);
    public string? Folder => Config.Folder;   // FolderRemote path (offline trial), or null for Telegram
    public AppConfig Config { get; private set; } = new();

    public string ConfigPath => Path.Combine(Dir, "config.json");
    /// <summary>The Telegram sign-in this drive uses: the account's shared one (accounts\&lt;id&gt;), or —
    /// before the account is known (a sign-in in progress) and for drives from before 0.8 — the profile's own.</summary>
    public string SessionPath => string.IsNullOrEmpty(Config.AccountKey)
        ? Path.Combine(Dir, "telegram.session")
        : Path.Combine(TelegramAccounts.AccountsDir, Config.AccountKey, "telegram.session");
    public string MasterKeyPath => Path.Combine(Dir, "unlock.key");
    public string IndexPath => Path.Combine(Dir, "index.db");
    public string LogDir => Path.Combine(Dir, "logs");
    public bool Exists => File.Exists(ConfigPath);

    bool LoadBackup()
    {
        try
        {
            Config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath + ".bak"), Json) ?? throw new JsonException("empty");
            Save();
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return false; }
    }

    AppState(string dir)
    {
        Dir = dir;
        Directory.CreateDirectory(Dir);
        if (File.Exists(ConfigPath))
        {
            try { Config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), Json) ?? throw new JsonException("empty"); }
            catch (Exception ex) when ((ex is JsonException or IOException) && File.Exists(ConfigPath + ".bak") && LoadBackup())
            {
                Log.Warn($"profile {Path.GetFileName(dir)}: config.json was unreadable ({ex.Message}); restored the previous copy");
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // e.g. a zero-length file after a power cut: never let one bad profile stop all drives
                Broken = true;
                Log.Error($"profile {Path.GetFileName(dir)}: config.json is unreadable ({ex.Message}); skipped");
            }
        }
    }

    /// <summary>The profile's config.json could not be read (skipped by All()).</summary>
    public bool Broken { get; }

    /// <summary>Open the profile in this exact folder (e.g. under the real data folder, whatever Root is).</summary>
    public static AppState At(string dir) => new(dir);

    /// <summary>Open a profile by folder name.</summary>
    public static AppState Open(string id) => new(Path.Combine(Root, id));

    /// <summary>A trial drive stored in a local folder (`--folder`).</summary>
    public static AppState ForFolder(string folder)
    {
        var full = Path.GetFullPath(folder);
        var tag = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())))[..10];
        var s = new AppState(Path.Combine(Root, "folder-" + tag));
        s.Config.Folder = full;
        return s;
    }

    /// <summary>A brand-new profile (nothing is written until Save()).</summary>
    public static AppState CreateNew()
    {
        var s = new AppState(Path.Combine(Root, "drive-" + Guid.NewGuid().ToString("N")[..8]));
        var used = All().Select(a => char.ToUpperInvariant(a.Config.MountPoint[0])).ToHashSet();
        var inUse = DriveInfo.GetDrives().Select(d => d.Name[0]).ToHashSet();
        s.Config.MountPoint = $"{"TUVWXYZSRQPONMLKJIHGFED".FirstOrDefault(c => !used.Contains(c) && !inUse.Contains(c), 'T')}:";
        var withApi = All().FirstOrDefault(a => a.Config.ApiId != 0); // reuse API credentials of an existing drive
        if (withApi is not null) { s.Config.ApiId = withApi.Config.ApiId; s.Config.ApiHash = withApi.Config.ApiHash; }
        else if (TelegramAccounts.Known().FirstOrDefault(a => a.ApiId != 0) is { } account)
        { s.Config.ApiId = account.ApiId; s.Config.ApiHash = account.ApiHash; } // signed in, no drive yet
        return s;
    }

    /// <summary>Every configured drive on this PC (Telegram drives and saved trial drives), oldest first.</summary>
    public static List<AppState> All()
    {
        if (!Directory.Exists(Root)) return [];
        return Directory.GetDirectories(Root)
            .Where(d => File.Exists(Path.Combine(d, "config.json")))
            .Select(d => new AppState(d))
            .Where(s => !s.Broken && (s.Folder is not null || s.Config.ApiId != 0))
            .OrderBy(s => s.Id == "telegram" ? "" : s.Id)
            .ThenBy(s => Directory.GetCreationTimeUtc(s.Dir))
            .ToList();
    }

    /// <summary>Atomic: a power cut leaves the old file or the new one, never half of one.</summary>
    public void Save()
    {
        var tmp = ConfigPath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Config, Json)));
            fs.Flush(flushToDisk: true); // on disk BEFORE the rename: a power cut never leaves an empty config
        }
        if (File.Exists(ConfigPath)) File.Replace(tmp, ConfigPath, ConfigPath + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, ConfigPath);
    }

    /// <summary>Remove this profile and everything this PC stored for it (not the files in Telegram).</summary>
    public void Delete()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Forget the local index/cache (after the drive's channels vanished): the next mount starts empty.</summary>
    /// The index goes first, and if it cannot go (something has it open for a moment), nothing else does:
    /// staging deleted under a kept index would leave records of changes whose pieces are gone.
    public void ResetLocalData()
    {
        var index = new[] { "index.db", "index.db-wal", "index.db-shm" }.Select(f => Path.Combine(Dir, f)).ToList();
        for (int attempt = 0; ; attempt++)
        {
            foreach (var f in index)
                try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (!index.Any(File.Exists)) break;
            if (attempt >= 20)
                throw new IOException(L.T("לא הצלחתי למחוק את הנתונים המקומיים הקודמים של הכונן (קובץ שלהם פתוח כרגע). לא נמחק כלום. נסה שוב בעוד רגע.",
                    "Could not delete the drive's previous local data (one of its files is open right now). Nothing was deleted. Try again in a moment."));
            Thread.Sleep(150);
        }
        foreach (var d in new[] { "staging", "cache" })
            try { Directory.Delete(Path.Combine(Dir, d), true); } catch (IOException) { }
        foreach (var f in new[] { "prepared.txt", "paused.txt" })
            try { File.Delete(Path.Combine(Dir, f)); } catch (IOException) { }
        ForgetMaster();
    }

    public string DisplayName => string.IsNullOrEmpty(Config.AccountName)
        ? $"{Config.MountPoint} {Config.VolumeLabel}"
        : $"{Config.MountPoint} {Config.VolumeLabel} · {Config.AccountName}";

    // DPAPI (CurrentUser): readable only by this Windows user on this PC.
    public static byte[] Protect(byte[] data) => ProtectedData.Protect(data, "DriveGram"u8.ToArray(), DataProtectionScope.CurrentUser);
    public static byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, "DriveGram"u8.ToArray(), DataProtectionScope.CurrentUser);

    public void RememberMaster(byte[] master) => File.WriteAllBytes(MasterKeyPath, Protect(master));
    /// <summary>The remembered drive key, or null. If Windows can no longer decrypt it (e.g. the user's
    /// password was reset by an admin, or the profile was copied), it is forgotten so the password is asked.</summary>
    public byte[]? RememberedMaster()
    {
        if (!File.Exists(MasterKeyPath)) return null;
        try { return Unprotect(File.ReadAllBytes(MasterKeyPath)); }
        catch (CryptographicException ex)
        {
            Log.Warn($"remembered key of {Id} cannot be decrypted ({ex.Message}); asking for the password");
            ForgetMaster();
            return null;
        }
    }
    public void ForgetMaster() { if (File.Exists(MasterKeyPath)) File.Delete(MasterKeyPath); }
    /// <summary>Is the drive key remembered on this PC (without decrypting it)?</summary>
    public bool HasRememberedMaster => File.Exists(MasterKeyPath);
}

/// <summary>
/// Telegram session storage for WTelegramClient, encrypted with DPAPI. The session file is a
/// full login to the Telegram account, so a copy of it must be useless on another PC/user.
/// </summary>
public sealed class DpapiSessionStream : Stream
{
    string _path;
    readonly object _gate = new();
    readonly MemoryStream _mem = new();

    public string Path => _path;

    /// <summary>Keep the same live sign-in but store it somewhere else (e.g. into the account folder).</summary>
    public void Relocate(string newPath)
    {
        lock (_gate)
        {
            var old = _path;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(newPath)!);
            _path = newPath;
            Persist();
            try { if (!string.Equals(old, newPath, StringComparison.OrdinalIgnoreCase)) File.Delete(old); } catch (IOException) { }
        }
    }

    public DpapiSessionStream(string path)
    {
        _path = path;
        if (File.Exists(path))
        {
            try
            {
                var plain = AppState.Unprotect(File.ReadAllBytes(path));
                _mem.Write(plain);
                _mem.Position = 0;
            }
            catch (CryptographicException ex)
            {
                // unreadable for this Windows user: start signed out (setup is offered) instead of failing forever
                Log.Warn($"sign-in file {path} cannot be decrypted ({ex.Message}); a new sign-in is needed");
                try { File.Move(path, path + ".unreadable", overwrite: true); } catch (IOException) { }
            }
        }
    }

    void Persist()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, AppState.Protect(_mem.ToArray()));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => _mem.Length;
    public override long Position { get => _mem.Position; set => _mem.Position = value; }
    public override void Flush() => Persist();
    public override int Read(byte[] buffer, int offset, int count) => _mem.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _mem.Seek(offset, origin);
    public override void SetLength(long value) { _mem.SetLength(value); Persist(); }
    public override void Write(byte[] buffer, int offset, int count) { _mem.Write(buffer, offset, count); Persist(); }
}

/// <summary>
/// Device ids this PC used for a drive in profiles that were removed since. A lease such a profile still
/// held (removed while uploads were pending) is this PC's own: the next profile of that drive takes it
/// at once instead of waiting a quarter of an hour for "another PC".
/// </summary>
public static class FormerDevices
{
    static string FilePath => Path.Combine(AppState.Root, "former-devices.txt");

    public static void Remember(AppState state)
    {
        if (string.IsNullOrEmpty(state.Config.DriveId) || !File.Exists(state.IndexPath)) return;
        try
        {
            string device;
            using (var db = DriveGram.Index.IndexDb.Open(state.IndexPath)) device = db.DeviceId;
            File.AppendAllText(FilePath, $"{state.Config.DriveId} {device}\n");
        }
        catch (Exception ex) { Log.Warn($"former device: {ex.Message}"); }
    }

    public static bool Has(string driveId, string device)
    {
        if (string.IsNullOrEmpty(driveId)) return false;
        try { return File.Exists(FilePath) && File.ReadLines(FilePath).Contains($"{driveId} {device}"); }
        catch (IOException) { return false; }
    }
}
