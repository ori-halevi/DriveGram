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

    Task<byte[]> Window(int msg, int win, CancellationToken ct)
    {
        var cached = _cache.TryGetWindow(msg, win);
        if (cached is not null) return Task.FromResult(cached);
        var lazy = _inflight.GetOrAdd((msg, win), key => new Lazy<Task<byte[]>>(async () =>
        {
            try
            {
                var data = await _remote.ReadWindowAsync(key.Item1, key.Item2, CancellationToken.None);
                Interlocked.Add(ref BytesDownloaded, data.Length);
                _cache.PutWindow(key.Item1, key.Item2, data);
                return data;
            }
            finally { _inflight.TryRemove(key, out _); }
        }));
        return lazy.Value.WaitAsync(ct);
    }

    /// <summary>Read plaintext [offset, offset+dst.Length) of a chunk into dst.</summary>
    public async Task ReadAsync(ChunkLoc c, long offset, Memory<byte> dst, bool sequential, CancellationToken ct)
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
        for (int w = w0; w <= w1; w++) tasks[w - w0] = Window(c.Msg, w, ct);
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
                try { await Window(c.Msg, win, CancellationToken.None); }
                catch (Exception ex) { Log.Debug($"prefetch {c.Msg}/{win} failed: {ex.Message}"); }
                finally { _prefetchSlots.Release(); }
            });
        }
    }
}
