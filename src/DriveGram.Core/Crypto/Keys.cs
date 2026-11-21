using System.Security.Cryptography;
using System.Text;

namespace DriveGram.Crypto;

/// <summary>
/// The vault header is public (stored in the journal channel's control message): it holds
/// only the KDF salt, the storage-format constants and a verifier. Never the key.
/// ChunkSize and FrameSize are PERMANENT for a drive: every stored chunk depends on them.
/// </summary>
public sealed record VaultHeader(int V, string Salt, int Iter, int ChunkSize, int FrameSize, string Verifier);

public sealed class WrongPasswordException() : Exception("Wrong password for this drive.");

/// <summary>
/// All key material, derived from one master key (PBKDF2 of the user's password).
///
/// Unlike Infinoom's convergent scheme (key = f(content hash) with no secret), every key here
/// depends on the master key, so a copy of the Telegram channels alone decrypts nothing:
///   chunk id  = HMAC(K_id,  sha256(plaintext))   -> the public name of a stored chunk
///   chunk key = HMAC(K_key, sha256(plaintext))   -> its AES-256-GCM key
/// Dedup still works inside the drive (same plaintext -> same id), but the id reveals
/// nothing about the content to anyone without the password.
/// </summary>
public sealed class Keys
{
    public const int DefaultChunkSize = 32 * 1024 * 1024;
    public const int DefaultFrameSize = 256 * 1024;
    public const int DefaultIterations = 600_000;

    readonly byte[] _master, _idKey, _chunkKey, _journalKey;

    public int ChunkSize { get; }
    public int FrameSize { get; }

    Keys(byte[] master, int chunkSize, int frameSize)
    {
        _master = master;
        ChunkSize = chunkSize;
        FrameSize = frameSize;
        _idKey = Sub("drivegram/chunk-id/v1");
        _chunkKey = Sub("drivegram/chunk-key/v1");
        _journalKey = Sub("drivegram/journal/v1");
    }

    byte[] Sub(string label) => HMACSHA256.HashData(_master, Encoding.UTF8.GetBytes(label));

    static string Verifier(byte[] master) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(master, "drivegram/verify/v1"u8));

    public static (VaultHeader Header, Keys Keys) Create(string password, int chunkSize = DefaultChunkSize,
        int frameSize = DefaultFrameSize, int iterations = DefaultIterations)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Password is empty.");
        if (chunkSize <= 0 || frameSize <= 0 || chunkSize % frameSize != 0)
            throw new ArgumentException("ChunkSize must be a positive multiple of FrameSize.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var master = Derive(password, salt, iterations);
        var header = new VaultHeader(1, Convert.ToBase64String(salt), iterations, chunkSize, frameSize, Verifier(master));
        return (header, new Keys(master, chunkSize, frameSize));
    }

    public static Keys Unlock(VaultHeader h, string password)
    {
        var master = Derive(password, Convert.FromBase64String(h.Salt), h.Iter);
        return FromMaster(h, master);
    }

    /// <summary>Re-open from a master key remembered on this PC (DPAPI-protected by the app).</summary>
    public static Keys FromMaster(VaultHeader h, byte[] master)
    {
        if (h.V != 1) throw new NotSupportedException($"Unsupported vault version {h.V}.");
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Verifier(master)), Encoding.ASCII.GetBytes(h.Verifier)))
            throw new WrongPasswordException();
        return new Keys(master, h.ChunkSize, h.FrameSize);
    }

    static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    public byte[] ExportMaster() => (byte[])_master.Clone();

    public string ChunkId(ReadOnlySpan<byte> plainHash) => Convert.ToHexStringLower(HMACSHA256.HashData(_idKey, plainHash));
    public byte[] ChunkKey(ReadOnlySpan<byte> plainHash) => HMACSHA256.HashData(_chunkKey, plainHash);
    public byte[] JournalKey => _journalKey;
}
