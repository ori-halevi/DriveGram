using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using DriveGram.Crypto;
using DriveGram.Remote;

namespace DriveGram.Engine;

/// <summary>Where a stored chunk lives and how to decrypt it.</summary>
public readonly record struct ChunkLoc(string ChunkId, int Msg, int Size, string Hash);

/// <summary>
/// Byte-range reads of stored chunks: fetch only the 1 MiB windows covering the needed GCM
/// frames (from cache or remote), decrypt only those frames. Concurrent requests for the same
/// window share one download. Sequential readers get read-ahead.
/// </summary>
public sealed class ChunkReader
{
    readonly IRemote _remote;
    readonly BlockCache _cache;
    readonly Keys _keys;
    readonly ConcurrentDictionary<(int, int), Lazy<Task<byte[]>>> _inflight = new();
    readonly SemaphoreSlim _prefetchSlots = new(4);

    public int ReadAheadWindows { get; set; } = 8;
    public long BytesDownloaded;

    public ChunkReader(IRemote remote, BlockCache cache, Keys keys)
    {
        _remote = remote;
        _cache = cache;
        _keys = keys;
    }

    /// <summary>Exact length window `win` of a stored chunk must have (the last one is shorter).</summary>
    int ExpectedWindowLength(ChunkLoc c, int win)
    {
        long enc = ChunkCipher.EncryptedSize(c.Size, _keys.FrameSize);
        return (int)Math.Clamp(enc - (long)win * IRemote.WindowSize, 0, IRemote.WindowSize);
    }

    /// <summary>
    /// One 1 MiB window, from cache or Telegram. Lesson from InfiDrive (fadbfa5/04d5e58): storage
    /// can return a read cut short with no error at all. A short window is never trusted or cached —
    /// it is fetched again, a bounded number of times — so one bad read cannot poison the cache and
    /// leave a file permanently broken. (GCM would still reject the bytes; this keeps it healable.)
    /// </summary>
    Task<byte[]> Window(ChunkLoc c, int win, CancellationToken ct)
    {
        int expected = ExpectedWindowLength(c, win);
        var cached = _cache.TryGetWindow(c.Msg, win);
        if (cached is not null && cached.Length == expected) return Task.FromResult(cached);
        var lazy = _inflight.GetOrAdd((c.Msg, win), key => new Lazy<Task<byte[]>>(async () =>
        {
            try
            {
                for (int attempt = 1; ; attempt++)
                {
                    var data = await _remote.ReadWindowAsync(key.Item1, key.Item2, CancellationToken.None);
                    Interlocked.Add(ref BytesDownloaded, data.Length);
                    if (data.Length == expected)
                    {
                        _cache.PutWindow(key.Item1, key.Item2, data);
                        return data;
                    }
                    Log.Warn($"chunk {c.ChunkId[..12]} window {key.Item2}: got {data.Length:N0} of {expected:N0} bytes (attempt {attempt})");
                    if (attempt >= MaxShortReads)
                        throw new IOException($"Telegram keeps returning a short read for message {key.Item1} (window {key.Item2}).");
                    await Task.Delay(500 * attempt);
                }
            }
            finally { _inflight.TryRemove(key, out _); }
        }));
        return lazy.Value.WaitAsync(ct);
    }

    const int MaxShortReads = 4;

    /// <summary>Read plaintext [offset, offset+dst.Length) of a chunk into dst. A cached window whose bytes
    /// fail authentication (disk corruption) is thrown away and fetched again, once.</summary>
    public async Task ReadAsync(ChunkLoc c, long offset, Memory<byte> dst, bool sequential, CancellationToken ct)
    {
        try { await ReadOnceAsync(c, offset, dst, sequential, ct); }
        catch (CryptographicException)
        {
            int F = _keys.FrameSize, W = IRemote.WindowSize;
            long start = ChunkCipher.FrameSpan((int)(offset / F), c.Size, F).Offset;
            var (lo, ll) = ChunkCipher.FrameSpan((int)((offset + dst.Length - 1) / F), c.Size, F);
            for (int w = (int)(start / W); w <= (int)((lo + ll - 1) / W); w++) _cache.RemoveWindow(c.Msg, w);
            Log.Warn($"chunk {c.ChunkId[..12]}: cached bytes failed authentication; fetching again");
            await ReadOnceAsync(c, offset, dst, sequential, ct);
        }
    }

    async Task ReadOnceAsync(ChunkLoc c, long offset, Memory<byte> dst, bool sequential, CancellationToken ct)
    {
        int len = dst.Length;
        if (len == 0) return;
        if (offset < 0 || offset + len > c.Size) throw new ArgumentOutOfRangeException(nameof(offset));
        int F = _keys.FrameSize, W = IRemote.WindowSize;

        int f0 = (int)(offset / F), f1 = (int)((offset + len - 1) / F);
        long cStart = ChunkCipher.FrameSpan(f0, c.Size, F).Offset;
        var (lastOff, lastLen) = ChunkCipher.FrameSpan(f1, c.Size, F);
        long cEnd = lastOff + lastLen;
        int w0 = (int)(cStart / W), w1 = (int)((cEnd - 1) / W);

        var tasks = new Task<byte[]>[w1 - w0 + 1];
        for (int w = w0; w <= w1; w++) tasks[w - w0] = Window(c, w, ct);
        var windows = await Task.WhenAll(tasks);

        if (sequential) Prefetch(c, w1 + 1);

        long baseOff = (long)w0 * W;
        int need = (int)(cEnd - baseOff);
        var cipher = ArrayPool<byte>.Shared.Rent(need);
        var plainFrame = ArrayPool<byte>.Shared.Rent(F);
        try
        {
            int pos = 0;
            foreach (var w in windows)
            {
                int take = Math.Min(w.Length, need - pos);
                w.AsSpan(0, take).CopyTo(cipher.AsSpan(pos));
                pos += take;
                if (w.Length < W && pos < need) break; // a short window is the end of the message
            }
            if (pos < need) throw new InvalidDataException($"chunk {c.ChunkId}: stored message is truncated");

            var hash = Convert.FromHexString(c.Hash);
            var aad = Convert.FromHexString(c.ChunkId);
            using var aes = new AesGcm(_keys.ChunkKey(hash), ChunkCipher.TagLen);
            for (int f = f0; f <= f1; f++)
            {
                var (fo, fl) = ChunkCipher.FrameSpan(f, c.Size, F);
                ChunkCipher.DecryptFrame(aes, f, cipher.AsSpan((int)(fo - baseOff), fl), plainFrame, aad);
                long frameStart = (long)f * F;
                long from = Math.Max(offset, frameStart);
                long to = Math.Min(offset + len, frameStart + fl - ChunkCipher.TagLen);
                plainFrame.AsSpan((int)(from - frameStart), (int)(to - from)).CopyTo(dst.Span[(int)(from - offset)..]);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(cipher);
            ArrayPool<byte>.Shared.Return(plainFrame);
        }
    }

    public async Task<byte[]> ReadWholeAsync(ChunkLoc c, CancellationToken ct)
    {
        var buf = new byte[c.Size];
        await ReadAsync(c, 0, buf, sequential: false, ct);
        return buf;
    }

    void Prefetch(ChunkLoc c, int fromWindow)
    {
        long encSize = ChunkCipher.EncryptedSize(c.Size, _keys.FrameSize);
        int lastWin = (int)((encSize - 1) / IRemote.WindowSize);
        for (int w = fromWindow; w <= Math.Min(lastWin, fromWindow + ReadAheadWindows - 1); w++)
        {
            if (_cache.HasWindow(c.Msg, w) || _inflight.ContainsKey((c.Msg, w))) continue;
            if (!_prefetchSlots.Wait(0)) return; // never let read-ahead pile up
            int win = w;
            _ = Task.Run(async () =>
            {
                try { await Window(c, win, CancellationToken.None); }
                catch (Exception ex) { Log.Debug($"prefetch {c.Msg}/{win} failed: {ex.Message}"); }
                finally { _prefetchSlots.Release(); }
            });
        }
    }
}
