using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DriveGram.Remote;

namespace DriveGram.Host;

/// <summary>Settings of this PC, in %LOCALAPPDATA%\DriveGram\&lt;profile&gt;\config.json.</summary>
public sealed class AppConfig
{
    public int ApiId { get; set; }
    public string ApiHash { get; set; } = "";
    public TelegramChannels? Channels { get; set; }
    public string MountPoint { get; set; } = "T:";
    public string VolumeLabel { get; set; } = "DriveGram";
    public double StagingQuotaGb { get; set; } = 8;
    public double CacheQuotaGb { get; set; } = 10;
    public int UploadConcurrency { get; set; } = 2;
}

/// <summary>Where this PC keeps its state for one drive (profile).</summary>
public sealed class AppState
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Dir { get; }
    public string? Folder { get; }   // FolderRemote path (offline trial), or null for Telegram
    public AppConfig Config { get; private set; } = new();

    public string ConfigPath => Path.Combine(Dir, "config.json");
    public string SessionPath => Path.Combine(Dir, "telegram.session");
    public string MasterKeyPath => Path.Combine(Dir, "unlock.key");
    public string IndexPath => Path.Combine(Dir, "index.db");
    public string LogDir => Path.Combine(Dir, "logs");

    public AppState(string? folder)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveGram");
        if (folder is null) Dir = Path.Combine(root, "telegram");
        else
        {
            Folder = Path.GetFullPath(folder);
            var tag = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Folder.ToUpperInvariant())))[..10];
            Dir = Path.Combine(root, "folder-" + tag);
        }
        Directory.CreateDirectory(Dir);
        if (File.Exists(ConfigPath))
            Config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), Json) ?? new AppConfig();
    }

    public void Save() => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config, Json));

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
