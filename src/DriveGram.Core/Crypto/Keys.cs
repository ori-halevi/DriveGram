using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DriveGram.Crypto;

/// <summary>The master key, encrypted ("wrapped") under a key derived from a password or a recovery key.</summary>
/// <param name="Salt">base64 salt of the key derivation</param><param name="Iter">PBKDF2 iterations (0 for a recovery key)</param>
/// <param name="Nonce">base64 AES-GCM nonce</param><param name="Wrapped">base64 ciphertext||tag of the 32-byte master key</param>
public sealed record KeyWrap(string Salt, int Iter, string Nonce, string Wrapped);

/// <summary>
/// The vault header is public (stored in the journal channel's control message): KDF parameters, the
/// storage-format constants, a verifier, and the master key WRAPPED under the password / recovery key —
/// never the master key itself. ChunkSize and FrameSize are PERMANENT for a drive.
///
///   V 1: master = PBKDF2(password, Salt, Iter)              (every drive before 1.0; optional Recovery)
///   V 2: master is random-or-inherited, stored in Password   (after a password change; Salt/Iter unused)
/// The master key never changes for a drive — every chunk/journal key derives from it — so changing the
/// password or adding a recovery key re-wraps it and never touches a stored file.
/// </summary>
public sealed record VaultHeader(int V, string Salt, int Iter, int ChunkSize, int FrameSize, string Verifier,
    KeyWrap? Password = null, KeyWrap? Recovery = null)
{
    /// <summary>Fields written by a NEWER version, kept verbatim so this version never drops them when it
    /// rewrites the record (an older client silently deleting a recovery key was a real risk).</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed class WrongPasswordException() : Exception("Wrong password for this drive.");

/// <summary>
/// All key material, derived from one master key.
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
    /// <summary>The newest vault version this build understands.</summary>
    public const int VaultVersion = 2;

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
        CheckVersion(h);
        byte[] master;
        if (h.Password is { } wrap)
        {
            CheckWrap(wrap, passwordWrap: true);
            master = Unwrap(wrap, Derive(password, B64(wrap.Salt), wrap.Iter));
        }
        else
        {
            // a v2 vault without its password wrap (stripped by a damaged/old writer) is unusable this way
            if (h.V >= 2 || !IterOk(h.Iter)) throw new WrongPasswordException();
            master = Derive(password, B64(h.Salt), h.Iter);
        }
        return FromMaster(h, master);
    }

    // The control record is public and editable by anyone with access to the account: a malformed or
    // hostile value must end as "wrong password", never as a crash or hours of PBKDF2.
    static bool IterOk(int iter) => iter is >= 1_000 and <= 20_000_000;

    static byte[] B64(string s)
    {
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { throw new WrongPasswordException(); }
    }

    static void CheckWrap(KeyWrap w, bool passwordWrap)
    {
        if ((passwordWrap && !IterOk(w.Iter)) || B64(w.Nonce).Length != 12 || B64(w.Wrapped).Length != 48 || B64(w.Salt).Length < 8)
            throw new WrongPasswordException();
    }

    /// <summary>Open the drive with its recovery key instead of the password.</summary>
    public static Keys UnlockWithRecovery(VaultHeader h, string recoveryKey)
    {
        CheckVersion(h);
        if (h.Recovery is not { } wrap || !RecoveryKey.TryParse(recoveryKey, out var raw)) throw new WrongPasswordException();
        CheckWrap(wrap, passwordWrap: false);
        return FromMaster(h, Unwrap(wrap, RecoveryKek(raw, B64(wrap.Salt))));
    }

    /// <summary>A password or a recovery key — whichever the user typed.</summary>
    public static Keys UnlockAny(VaultHeader h, string secret)
    {
        if (RecoveryKey.Looks(secret) && h.Recovery is not null)
        {
            try { return UnlockWithRecovery(h, secret); }
            catch (WrongPasswordException) { } // a password that merely looks like a key
        }
        return Unlock(h, secret);
    }

    /// <summary>Re-open from a master key remembered on this PC (DPAPI-protected by the app).</summary>
    public static Keys FromMaster(VaultHeader h, byte[] master)
    {
        CheckVersion(h);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Verifier(master)), Encoding.ASCII.GetBytes(h.Verifier)))
            throw new WrongPasswordException();
        return new Keys(master, h.ChunkSize, h.FrameSize);
    }

    static void CheckVersion(VaultHeader h)
    {
        if (h.V > VaultVersion) throw new NeedsUpdateException($"vault version {h.V}");
        if (h.V < 1) throw new NotSupportedException($"Unsupported vault version {h.V}.");
    }

    static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>A recovery key has 256 random bits: no slow derivation needed, just domain separation.</summary>
    static byte[] RecoveryKek(byte[] raw, byte[] salt) =>
        HMACSHA256.HashData(raw, (byte[])[.. "drivegram/recovery/v1"u8, .. salt]);

    static readonly byte[] WrapAad = "drivegram/wrap/v1"u8.ToArray();

    static KeyWrap Wrap(byte[] master, byte[] kek, byte[] salt, int iter)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var output = new byte[master.Length + 16];
        using var aes = new AesGcm(kek, 16);
        aes.Encrypt(nonce, master, output.AsSpan(0, master.Length), output.AsSpan(master.Length), WrapAad);
        return new KeyWrap(Convert.ToBase64String(salt), iter, Convert.ToBase64String(nonce), Convert.ToBase64String(output));
    }

    static byte[] Unwrap(KeyWrap w, byte[] kek)
    {
        var data = B64(w.Wrapped);
        if (data.Length != 48) throw new WrongPasswordException();
        var master = new byte[data.Length - 16];
        try
        {
            using var aes = new AesGcm(kek, 16);
            aes.Decrypt(B64(w.Nonce), data.AsSpan(0, master.Length), data.AsSpan(master.Length), master, WrapAad);
        }
        catch (CryptographicException) { throw new WrongPasswordException(); }
        return master;
    }

    /// <summary>A new vault header whose password is `newPassword` (same master key: no file changes).</summary>
    public VaultHeader WithNewPassword(VaultHeader h, string newPassword, int iterations = DefaultIterations)
    {
        if (string.IsNullOrEmpty(newPassword)) throw new ArgumentException("Password is empty.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var wrap = Wrap(_master, Derive(newPassword, salt, iterations), salt, iterations);
        return h with { V = 2, Salt = "", Iter = 0, Password = wrap };
    }

    /// <summary>A new vault header with a fresh recovery key (replacing any older one), and that key's text.</summary>
    public (VaultHeader Header, string RecoveryKeyText) WithNewRecoveryKey(VaultHeader h)
    {
        var raw = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(16);
        var wrap = Wrap(_master, RecoveryKek(raw, salt), salt, 0);
        return (h with { Recovery = wrap }, RecoveryKey.Format(raw));
    }

    public byte[] ExportMaster() => (byte[])_master.Clone();

    public string ChunkId(ReadOnlySpan<byte> plainHash) => Convert.ToHexStringLower(HMACSHA256.HashData(_idKey, plainHash));
    public byte[] ChunkKey(ReadOnlySpan<byte> plainHash) => HMACSHA256.HashData(_chunkKey, plainHash);
    public byte[] JournalKey => _journalKey;
}

/// <summary>
/// Recovery keys as people can copy them: 256 bits in Crockford base32 (no I, L, O, U), 52 characters
/// in groups of 4 — e.g. "7K3M-QX2P-…". Typing is forgiving: case, spaces, dashes, and the look-alikes
/// O→0, I/L→1 are all accepted.
/// </summary>
public static class RecoveryKey
{
    const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Format(byte[] raw)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in raw)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        var s = sb.ToString(); // 52 characters for 32 bytes
        return string.Join('-', Enumerable.Range(0, (s.Length + 3) / 4).Select(i => s.Substring(i * 4, Math.Min(4, s.Length - i * 4))));
    }

    static string Normalize(string text) => new string(text.ToUpperInvariant()
        .Where(c => !char.IsWhiteSpace(c) && c != '-')
        .Select(c => c switch { 'O' => '0', 'I' or 'L' => '1', _ => c }).ToArray());

    /// <summary>Does this look like a recovery key (rather than a password)?</summary>
    public static bool Looks(string text)
    {
        var n = Normalize(text);
        return n.Length == 52 && n.All(c => Alphabet.Contains(c));
    }

    public static bool TryParse(string text, out byte[] raw)
    {
        raw = [];
        var n = Normalize(text);
        if (n.Length != 52 || !n.All(c => Alphabet.Contains(c))) return false;
        var bytes = new List<byte>(32);
        int buffer = 0, bits = 0;
        foreach (var c in n)
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8) { bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        if (bytes.Count != 32) return false;
        raw = bytes.ToArray();
        return true;
    }
}
