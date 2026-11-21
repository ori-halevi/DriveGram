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
    /// <summary>Telegram user the drive lives on (0 = not known yet), and a display name for menus.</summary>
    public long AccountId { get; set; }
    public string AccountName { get; set; } = "";
    /// <summary>Local folder instead of Telegram (offline trial drives).</summary>
    public string? Folder { get; set; }
    public string MountPoint { get; set; } = "T:";
    public string VolumeLabel { get; set; } = "DriveGram";
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

    public string Dir { get; }
    public string Id => Path.GetFileName(Dir);
    public string? Folder => Config.Folder;   // FolderRemote path (offline trial), or null for Telegram
    public AppConfig Config { get; private set; } = new();

    public string ConfigPath => Path.Combine(Dir, "config.json");
    public string SessionPath => Path.Combine(Dir, "telegram.session");
    public string MasterKeyPath => Path.Combine(Dir, "unlock.key");
    public string IndexPath => Path.Combine(Dir, "index.db");
    public string LogDir => Path.Combine(Dir, "logs");
    public bool Exists => File.Exists(ConfigPath);

    AppState(string dir)
    {
        Dir = dir;
        Directory.CreateDirectory(Dir);
        if (File.Exists(ConfigPath))
            Config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), Json) ?? new AppConfig();
    }

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
        return s;
    }

    /// <summary>Every configured drive on this PC (Telegram drives and saved trial drives), oldest first.</summary>
    public static List<AppState> All()
    {
        if (!Directory.Exists(Root)) return [];
        return Directory.GetDirectories(Root)
            .Where(d => File.Exists(Path.Combine(d, "config.json")))
            .Select(d => new AppState(d))
            .Where(s => s.Folder is not null || s.Config.ApiId != 0)
            .OrderBy(s => s.Id == "telegram" ? "" : s.Id)
            .ThenBy(s => Directory.GetCreationTimeUtc(s.Dir))
            .ToList();
    }

    public void Save() => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config, Json));

    /// <summary>Remove this profile and everything this PC stored for it (not the files in Telegram).</summary>
    public void Delete()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Forget the local index/cache (after the drive's channels vanished): the next mount starts empty.</summary>
    public void ResetLocalData()
    {
        foreach (var f in new[] { "index.db", "index.db-wal", "index.db-shm" })
            try { File.Delete(Path.Combine(Dir, f)); } catch (IOException) { }
        foreach (var d in new[] { "staging", "cache" })
            try { Directory.Delete(Path.Combine(Dir, d), true); } catch (IOException) { }
        ForgetMaster();
    }

    public string DisplayName => string.IsNullOrEmpty(Config.AccountName)
        ? $"{Config.MountPoint} {Config.VolumeLabel}"
        : $"{Config.MountPoint} {Config.VolumeLabel} · {Config.AccountName}";

    // DPAPI (CurrentUser): readable only by this Windows user on this PC.
    public static byte[] Protect(byte[] data) => ProtectedData.Protect(data, "DriveGram"u8.ToArray(), DataProtectionScope.CurrentUser);
    public static byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, "DriveGram"u8.ToArray(), DataProtectionScope.CurrentUser);

    public void RememberMaster(byte[] master) => File.WriteAllBytes(MasterKeyPath, Protect(master));
    public byte[]? RememberedMaster() => File.Exists(MasterKeyPath) ? Unprotect(File.ReadAllBytes(MasterKeyPath)) : null;
    public void ForgetMaster() { if (File.Exists(MasterKeyPath)) File.Delete(MasterKeyPath); }
}

/// <summary>
/// Telegram session storage for WTelegramClient, encrypted with DPAPI. The session file is a
/// full login to the Telegram account, so a copy of it must be useless on another PC/user.
/// </summary>
public sealed class DpapiSessionStream : Stream
{
    readonly string _path;
    readonly MemoryStream _mem = new();

    public DpapiSessionStream(string path)
    {
        _path = path;
        if (File.Exists(path))
        {
            var plain = AppState.Unprotect(File.ReadAllBytes(path));
            _mem.Write(plain);
            _mem.Position = 0;
        }
    }

    void Persist()
    {
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, AppState.Protect(_mem.ToArray()));
        File.Move(tmp, _path, overwrite: true);
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
