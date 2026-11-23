using DriveGram.Crypto;
using DriveGram.Engine;
using DriveGram.Remote;
using DriveGram.Sync;

namespace DriveGram.Tests;

/// <summary>"Free up space" and a drive created with 64 MB pieces.</summary>
public class SpaceAndPieceTests
{
    [Fact]
    public void ClearingTheCacheFreesEveryCopyAndKeepsWorking()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dg-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new BlockCache(dir, 100L << 20);
            cache.PutWindow(1, 0, new byte[300_000]);
            cache.PutWindow(1, 1, new byte[200_000]);
            Assert.Equal(500_000, cache.Bytes);
            Assert.Equal(500_000, cache.Clear());
            Assert.Equal(0, cache.Bytes);
            Assert.Null(cache.TryGetWindow(1, 0));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(dir, "w")));
            cache.PutWindow(2, 0, new byte[10]);                   // still usable afterwards
            Assert.NotNull(cache.TryGetWindow(2, 0));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ADriveWith64MbPiecesKeepsItsPieceSize()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dg-64-" + Guid.NewGuid().ToString("N"));
        try
        {
            var remote = new FolderRemote(dir);
            var keys = await SyncService.CreateDriveAsync(remote, "correct horse battery", default, chunkSize: 64 << 20, iterations: 1000);
            Assert.Equal(64 << 20, keys.ChunkSize);
            var ctrl = await SyncService.ReadControlAsync(remote, default);
            Assert.Equal(64 << 20, Keys.UnlockAny(ctrl!.Vault, "correct horse battery").ChunkSize); // what every PC will use
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
