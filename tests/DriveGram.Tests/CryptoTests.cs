using System.Security.Cryptography;
using DriveGram.Crypto;
using DriveGram.Engine;
using DriveGram.Remote;

namespace DriveGram.Tests;

public class CryptoTests
{
    [Fact]
    public void WrongPasswordIsRejected()
    {
        var (header, _) = Keys.Create("right", iterations: 1000);
        Assert.Throws<WrongPasswordException>(() => Keys.Unlock(header, "wrong"));
        Assert.Equal(header.ChunkSize, Keys.Unlock(header, "right").ChunkSize);
    }

    [Fact]
    public void ChunkIdsDependOnThePassword()
    {
        var hash = SHA256.HashData("same content"u8);
        var (_, a) = Keys.Create("one", iterations: 1000);
        var (_, b) = Keys.Create("two", iterations: 1000);
        Assert.NotEqual(a.ChunkId(hash), b.ChunkId(hash));
        Assert.Equal(a.ChunkId(hash), a.ChunkId(hash)); // dedup inside one drive still works
        Assert.DoesNotContain(Convert.ToHexStringLower(hash), a.ChunkId(hash)); // the name hides the content hash
    }

    [Fact]
    public void EnvelopeRoundTripsAndDetectsTamper()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var sealedData = Envelope.Seal(key, "hello"u8);
        Assert.Equal("hello"u8.ToArray(), Envelope.Open(key, sealedData));
        sealedData[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => Envelope.Open(key, sealedData));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(65_530, 20)]          // across a frame boundary
    [InlineData(1_048_000, 5_000)]    // across a 1 MiB window boundary
    [InlineData(0, 2_500_000)]        // whole chunk
    [InlineData(2_499_999, 1)]        // last byte
    public async Task RangeReadsDecryptOnlyWhatIsNeeded(int offset, int length)
    {
        var dir = Path.Combine(Path.GetTempPath(), "drivegram-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var remote = new FolderRemote(dir);
            var (_, keys) = Keys.Create("pw", chunkSize: 4 * 64 * 1024 * 16, frameSize: 64 * 1024, iterations: 1000);
            var plain = TestKit.RandomBytes(2_500_000, 7);
            var hash = SHA256.HashData(plain);
            var id = keys.ChunkId(hash);
            var payload = ChunkCipher.Encrypt(plain, keys.ChunkKey(hash), Convert.FromHexString(id), keys.FrameSize);
            int msg = await remote.UploadChunkAsync("x", payload, default);

            var reader = new ChunkReader(remote, new BlockCache(Path.Combine(dir, "cache"), 1 << 30), keys);
            var dst = new byte[length];
            await reader.ReadAsync(new ChunkLoc(id, msg, plain.Length, Convert.ToHexStringLower(hash)), offset, dst, false, default);
            Assert.Equal(plain.AsSpan(offset, length).ToArray(), dst);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
