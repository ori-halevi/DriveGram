using DriveGram.Crypto;
using DriveGram.Sync;

namespace DriveGram.Tests;

public class RecoveryTests
{
    [Fact]
    public void RecoveryKeysRoundTripAndForgiveTyping()
    {
        var raw = Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 3)).ToArray();
        var text = RecoveryKey.Format(raw);
        Assert.Equal(13, text.Split('-').Length);                       // 52 characters in groups of 4
        Assert.True(RecoveryKey.TryParse(text, out var back));
        Assert.Equal(raw, back);
        var sloppy = text.ToLowerInvariant().Replace("-", " ").Replace('0', 'o').Replace('1', 'l');
        Assert.True(RecoveryKey.TryParse(sloppy, out var back2));       // case, spaces, O/0, l/1
        Assert.Equal(raw, back2);
        Assert.False(RecoveryKey.Looks("correct horse battery staple"));
    }

    [Fact]
    public async Task ARecoveryKeyOpensTheDriveAndOldVersionsAreUnaffected()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a");
        await a.Sync.StartAsync(false, default);
        var text = await a.Sync.CreateRecoveryKeyAsync(kit.Keys, default);

        var ctrl = (await SyncService.ReadControlAsync(kit.Remote, default))!;
        Assert.Equal(1, ctrl.Vault.V);                                  // still a v1 vault: old versions unaffected
        Assert.Equal(1, ctrl.MinReader);
        var viaKey = Keys.UnlockAny(ctrl.Vault, text);
        Assert.Equal(kit.Keys.ExportMaster(), viaKey.ExportMaster());
        Assert.Equal(kit.Keys.ExportMaster(), Keys.Unlock(ctrl.Vault, "correct horse").ExportMaster());
        Assert.Throws<WrongPasswordException>(() => Keys.UnlockWithRecovery(ctrl.Vault, RecoveryKey.Format(new byte[32])));
    }

    [Fact]
    public async Task ChangingThePasswordKeepsEveryFileAndTheRecoveryKey()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a");
        await a.Sync.StartAsync(false, default);
        var data = TestKit.RandomBytes(TestKit.ChunkSize + 100, 31);
        var f = a.NewFile("tax.pdf");
        a.WriteFile(f, data);
        await a.SettleAsync();
        var recovery = await a.Sync.CreateRecoveryKeyAsync(kit.Keys, default);
        int chunksBefore = kit.Remote.StoredChunkCount;

        await a.Sync.ChangePasswordAsync(kit.Keys, "a much better passphrase", default);
        var ctrl = (await SyncService.ReadControlAsync(kit.Remote, default))!;
        Assert.Equal(2, ctrl.Vault.V);
        Assert.Equal(2, ctrl.MinReader);                                // versions before 1.0 stop safely
        Assert.Throws<WrongPasswordException>(() => Keys.Unlock(ctrl.Vault, "correct horse"));
        var viaNew = Keys.Unlock(ctrl.Vault, "a much better passphrase");
        var viaRecovery = Keys.UnlockAny(ctrl.Vault, recovery);
        Assert.Equal(kit.Keys.ExportMaster(), viaNew.ExportMaster());  // same master key...
        Assert.Equal(kit.Keys.ExportMaster(), viaRecovery.ExportMaster());
        Assert.Equal(chunksBefore, kit.Remote.StoredChunkCount);       // ...so nothing was re-uploaded
        Keys.FromMaster(ctrl.Vault, kit.Keys.ExportMaster());           // PCs that remember the key keep working

        await a.Sync.StopAsync(true, default);
        var b = kit.Pc("b");                                            // a new PC with the new password reads the old file
        await b.Sync.StartAsync(false, default);
        Assert.Equal(data, b.ReadFile(f));
    }
}
