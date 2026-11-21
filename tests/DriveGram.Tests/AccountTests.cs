using DriveGram.Host;

namespace DriveGram.Tests;

/// <summary>Shared sign-ins per account. Runs against an isolated DRIVEGRAM_ROOT (never the real one).</summary>
[Collection("isolated-root")]
public sealed class AccountTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "drivegram-tests", "root-" + Guid.NewGuid().ToString("N"));
    readonly string? _previous = Environment.GetEnvironmentVariable("DRIVEGRAM_ROOT");

    public AccountTests() => Environment.SetEnvironmentVariable("DRIVEGRAM_ROOT", _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DRIVEGRAM_ROOT", _previous);
        try { Directory.Delete(_root, true); } catch { }
    }

    static AppState Profile(string id, long account)
    {
        var s = AppState.Open(id);
        s.Config.ApiId = 12345;
        s.Config.ApiHash = new string('a', 32);
        s.Config.AccountId = account;
        s.Config.AccountName = "@tester";
        s.Save();
        return s;
    }

    [Fact]
    public void OldPerDriveSignInsMoveIntoOneAccountFolder()
    {
        var first = Profile("telegram", 777);
        File.WriteAllBytes(first.SessionPath, AppState.Protect("session-A"u8.ToArray()));
        var second = Profile("drive-2", 777);              // a second drive, same account, its own old sign-in
        File.WriteAllBytes(second.SessionPath, AppState.Protect("session-B"u8.ToArray()));

        TelegramAccounts.MigrateLegacy(first);
        TelegramAccounts.MigrateLegacy(second);

        Assert.Equal("777", first.Config.AccountKey);
        Assert.Equal(first.SessionPath, second.SessionPath);  // both drives now use one sign-in
        Assert.Equal("session-A"u8.ToArray(), AppState.Unprotect(File.ReadAllBytes(first.SessionPath)));
        Assert.True(File.Exists(Path.Combine(second.Dir, "telegram.session.retired")));
        var known = Assert.Single(TelegramAccounts.Known());
        Assert.Equal(777, known.Id);
        Assert.Equal("777", AppState.Open("drive-2").Config.AccountKey); // saved, survives a restart
    }

    [Fact]
    public void DrivesOfOneAccountShareOneConnectionUntilTheLastLetsGo()
    {
        var a = Profile("drive-a", 5);
        var b = Profile("drive-b", 5);
        a.Config.AccountKey = b.Config.AccountKey = "5";

        var la = TelegramAccounts.Acquire(a);
        var lb = TelegramAccounts.Acquire(b);
        Assert.Same(la.Client, lb.Client);                  // never two clients on one sign-in file
        Assert.Same(la.Up, lb.Up);                          // and one rate-limit governor per account
        Assert.True(TelegramAccounts.InUse("5"));

        la.Dispose();
        la.Dispose();                                       // releasing twice is harmless
        Assert.True(TelegramAccounts.InUse("5"));
        lb.Dispose();
        Assert.False(TelegramAccounts.InUse("5"));
    }
}

[CollectionDefinition("isolated-root", DisableParallelization = true)]
public sealed class IsolatedRootCollection;
