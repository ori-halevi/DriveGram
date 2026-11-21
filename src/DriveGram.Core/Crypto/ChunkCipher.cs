using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DriveGram.Crypto;

/// <summary>
/// Framed AES-256-GCM for stored chunks (same idea as Infinoom's crypto.py, new magic).
///
/// WIRE FORMAT (permanent):
///   HEADER  = "DGC1" | frame_size (4B big-endian)                 -- 8 bytes
///   frame i = AESGCM(chunk_key, nonce = i (12B BE), aad = chunk_id_bytes).Encrypt(P_i) -> ct || tag(16)
///
/// Each frame is authenticated on its own, so a byte-range read downloads and decrypts only
/// the frames it touches (video seeking, random access) instead of the whole chunk.
/// The key is unique per distinct plaintext, so nonce = frame index never repeats under a key.
/// </summary>
public static class ChunkCipher
{
    public static ReadOnlySpan<byte> Magic => "DGC1"u8;
    public const int HeaderLen = 8;
    public const int TagLen = 16;
    const int NonceLen = 12;

    public static int FrameCount(long plainSize, int frameSize) =>
        plainSize <= 0 ? 0 : (int)((plainSize + frameSize - 1) / frameSize);

    public static long EncryptedSize(long plainSize, int frameSize) =>
        HeaderLen + plainSize + (long)FrameCount(plainSize, frameSize) * TagLen;

    /// <summary>(offset, length) of frame i's ciphertext+tag inside the stored message.</summary>
    public static (long Offset, int Length) FrameSpan(int i, long plainSize, int frameSize)
    {
        int plainLen = (int)Math.Min(frameSize, plainSize - (long)i * frameSize);
        return (HeaderLen + (long)i * (frameSize + TagLen), plainLen + TagLen);
    }

    static void Nonce(Span<byte> nonce, int i)
    {
        nonce.Clear();
        BinaryPrimitives.WriteInt32BigEndian(nonce[^4..], i);
    }

    public static byte[] Encrypt(ReadOnlySpan<byte> plain, byte[] key, byte[] aad, int frameSize)
    {
        int frames = FrameCount(plain.Length, frameSize);
        var output = new byte[EncryptedSize(plain.Length, frameSize)];
        Magic.CopyTo(output);
        BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(4, 4), frameSize);
        using var aes = new AesGcm(key, TagLen);
        Span<byte> nonce = stackalloc byte[NonceLen];
        for (int i = 0; i < frames; i++)
        {
            var (off, len) = FrameSpan(i, plain.Length, frameSize);
            int plainLen = len - TagLen;
            var p = plain.Slice(i * frameSize, plainLen);
            var dst = output.AsSpan((int)off, len);
            Nonce(nonce, i);
            aes.Encrypt(nonce, p, dst[..plainLen], dst[plainLen..], aad);
        }
        return output;
    }

    /// <summary>Decrypt one frame (ciphertext||tag) into dst. Throws on tamper / wrong key.</summary>
    public static void DecryptFrame(AesGcm aes, int i, ReadOnlySpan<byte> ctWithTag, Span<byte> dst, byte[] aad)
    {
        Span<byte> nonce = stackalloc byte[NonceLen];
        Nonce(nonce, i);
        int plainLen = ctWithTag.Length - TagLen;
        aes.Decrypt(nonce, ctWithTag[..plainLen], ctWithTag[plainLen..], dst[..plainLen], aad);
    }

    public static void CheckHeader(ReadOnlySpan<byte> header, int expectedFrameSize)
    {
        if (header.Length < HeaderLen || !header[..4].SequenceEqual(Magic))
            throw new InvalidDataException("Not a DriveGram chunk.");
        int fs = BinaryPrimitives.ReadInt32BigEndian(header.Slice(4, 4));
        if (fs != expectedFrameSize) throw new InvalidDataException($"Unexpected frame size {fs}.");
    }
}

/// <summary>Random-nonce AES-GCM envelope for small records (journal entries, snapshots).</summary>
public static class Envelope
{
    static ReadOnlySpan<byte> Magic => "DGE1"u8;
    const int NonceLen = 12, TagLen = 16, Head = 4 + NonceLen;

    public static byte[] Seal(byte[] key, ReadOnlySpan<byte> plain)
    {
        var output = new byte[Head + plain.Length + TagLen];
        Magic.CopyTo(output);
        var nonce = output.AsSpan(4, NonceLen);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagLen);
        aes.Encrypt(nonce, plain, output.AsSpan(Head, plain.Length), output.AsSpan(Head + plain.Length), Magic);
        return output;
    }

    public static byte[] Open(byte[] key, ReadOnlySpan<byte> sealedData)
    {
        if (sealedData.Length < Head + TagLen || !sealedData[..4].SequenceEqual(Magic))
            throw new InvalidDataException("Not a DriveGram envelope.");
        var plain = new byte[sealedData.Length - Head - TagLen];
        using var aes = new AesGcm(key, TagLen);
        aes.Decrypt(sealedData.Slice(4, NonceLen), sealedData.Slice(Head, plain.Length),
            sealedData[^TagLen..], plain, Magic);
        return plain;
    }
}
