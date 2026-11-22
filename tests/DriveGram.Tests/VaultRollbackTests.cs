using DriveGram.Crypto;
using DriveGram.Sync;

namespace DriveGram.Tests;

/// <summary>
/// A PC that was off during a password / recovery-key change must never bring the old vault back:
/// neither by re-signing an old copy written back to Telegram, nor when the user confirms the record there.
/// </summary>
public class VaultRollbackTests
{
    static SyncOptions Fast(string name) => new()
    {
        DeviceName = name, FlushInterval = TimeSpan.FromMilliseconds(100), HeartbeatInterval = TimeSpan.FromMilliseconds(300),
    };

    static string Ctl(TestKit kit) => File.ReadAllText(Path.Combine(kit.Root, "remote", "control.json"));
    static void SetCtl(TestKit kit, string t) => File.WriteAllText(Path.Combine(kit.Root, "remote", "control.json"), t);

    static bool Opens(VaultHeader v, string secret)
    {
        try { Keys.UnlockAny(v, secret); return true; } catch (WrongPasswordException) { return false; }
    }

    [Fact]
    public async Task AnOldVaultReSignedByAPcThatWasOffIsPutBackToTheNewOne()
    {
        await using var kit = await TestKit.CreateAsync();
        var bDir = Path.Combine(kit.Root, "b");
        var b = new Pc(bDir, kit.Remote, kit.Keys, so: Fast("PC-B"));
        await b.Sync.StartAsync(false, default);
        await b.Sync.StopAsync(true, default);
        var old = Ctl(kit);                                       // a signed copy from before the change
        await b.DisposeAsync();

        var a = kit.Pc("a", so: Fast("PC-A"));
        await a.Sync.StartAsync(false, default);
        await a.Sync.ChangePasswordAsync(kit.Keys, "brand new password", default);
        var rk = await a.Sync.CreateRecoveryKeyAsync(kit.Keys, default);
        await Task.Delay(1500);

        SetCtl(kit, old);                                         // written back (or B's late edit)
        b = new Pc(bDir, kit.Remote, kit.Keys, so: Fast("PC-B")); // the PC that missed the change returns
        try
        {
            await b.Sync.StartAsync(false, default);
            var until = DateTime.UtcNow.AddSeconds(30);
            ControlState now;
            do
            {
                await Task.Delay(300);
                now = ControlState.FromJson(Ctl(kit))!;
            } while (!(Opens(now.Vault, "brand new password") && a.Sync.ControlTrusted && b.Sync.ControlTrusted) && DateTime.UtcNow < until);
            await Task.Delay(2000);                               // and it stays that way
            now = ControlState.FromJson(Ctl(kit))!;
            Assert.True(Opens(now.Vault, "brand new password"));
            Assert.True(Opens(now.Vault, rk));
            Assert.False(Opens(now.Vault, "correct horse"));
            Assert.True(a.Sync.ControlTrusted);
            Assert.True(b.Sync.ControlTrusted);
        }
        finally { await b.DisposeAsync(); }
    }

    [Fact]
    public async Task ConfirmingOnAPcThatMissedAPasswordChangeKeepsTheNewPassword()
    {
        await using var kit = await TestKit.CreateAsync();
        var b = kit.Pc("b", so: Fast("PC-B"));
        await b.Sync.StartAsync(false, default);
        await b.Sync.StopAsync(true, default);                    // B's last verified copy: the old vault

        var a = kit.Pc("a", so: Fast("PC-A"));
        await a.Sync.StartAsync(false, default);
        await a.Sync.ChangePasswordAsync(kit.Keys, "brand new password", default);
        var rk = await a.Sync.CreateRecoveryKeyAsync(kit.Keys, default);
        await a.Sync.StopAsync(true, default);

        SetCtl(kit, Ctl(kit) + " ");                              // altered without the password
        _ = await b.Sync.ReadControlAsync(default);
        Assert.False(b.Sync.ControlTrusted);

        await Assert.ThrowsAsync<NewerVaultException>(() => b.Sync.RepairControlAsync(default));
        var wrong = await Assert.ThrowsAsync<NewerVaultException>(() => b.Sync.RepairControlAsync(default, "correct horse"));
        Assert.True(wrong.WrongSecret);                           // the old password does not decide it
        await b.Sync.RepairControlAsync(default, "brand new password");

        var now = ControlState.FromJson(Ctl(kit))!;
        Assert.True(Opens(now.Vault, "brand new password"));
        Assert.True(Opens(now.Vault, rk));
        _ = await a.Sync.ReadControlAsync(default);
        Assert.True(a.Sync.ControlTrusted);
    }
}
