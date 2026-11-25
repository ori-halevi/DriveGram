using System.Text.Json;
using DriveGram.Remote;

namespace DriveGram.Host;

/// <summary>An account signed in on this PC (accounts\&lt;id&gt;\account.json).</summary>
public sealed record AccountInfo(string Key, long Id, string Name, int ApiId, string ApiHash);

/// <summary>A drive's (or the wizard's) use of an account's shared Telegram connection.</summary>
public sealed class AccountLease : IDisposable
{
    internal readonly TelegramAccounts.Entry Entry;
    int _released;

    internal AccountLease(TelegramAccounts.Entry entry) => Entry = entry;

    public WTelegram.Client Client => Entry.Client;
    public Governor Up => Entry.Up;
    public Governor Down => Entry.Down;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) TelegramAccounts.Release(Entry);
    }
}

/// <summary>
/// One Telegram connection per ACCOUNT, shared by every drive on it (and by the setup wizard):
///  - one sign-in: a second drive on the same account needs no new QR scan;
///  - one "device" in the account's list of devices;
///  - one rate-limit governor: Telegram limits the account, not the drive, so drives on the same
///    account must share the back-off instead of each spending the account's budget on its own.
/// Never two live clients on the same sign-in file (they would both write it, and fight over the
/// same key's message counters). The connection closes when its last user releases it.
/// </summary>
public static class TelegramAccounts
{
    internal sealed class Entry
    {
        public required string Path;
        public required WTelegram.Client Client;
        public required DpapiSessionStream Stream;
        public required Governor Up, Down;
        public required ClientHealer Healer;
        public Semaphore? ProcessLock;
        public int Refs;
    }

    static readonly object Gate = new();
    static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    public static string AccountsDir => Path.Combine(AppState.Root, "accounts");

    /// <summary>Use the connection of the account this profile points at (creating it if needed).
    /// `askPassword` answers a 2FA prompt, only relevant while signing in.</summary>
    public static AccountLease Acquire(AppState state, Func<string?>? askPassword = null)
    {
        var path = Path.GetFullPath(state.SessionPath);
        lock (Gate)
        {
            if (!Entries.TryGetValue(path, out var e))
            {
                // the same sign-in used by two programs at once makes Telegram reset it (AUTH_KEY_DUPLICATED)
                var processLock = TryLock(path);
                if (processLock is null)
                {
                    throw new InvalidOperationException(L.T("החשבון הזה כבר בשימוש בתוכנת DriveGram אחרת שפתוחה במחשב (למשל שורת הפקודה). סגור אותה ונסה שוב.",
                        "This account is already in use by another DriveGram program on this PC (e.g. the command line). Close it and try again."));
                }
                WTelegram.Client client;
                DpapiSessionStream stream;
                try { client = DriveSession.NewTelegramClient(state, askPassword, out stream); }
                catch { processLock.Release(); processLock.Dispose(); throw; }
                e = new Entry
                {
                    Path = path, Client = client, Stream = stream,
                    Up = new Governor("upload", 8), Down = new Governor("download", 8),
                    Healer = new ClientHealer(client, System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? "account"),
                    ProcessLock = processLock,
                };
                // A 401 can also come from a secondary data center's connection (files stored elsewhere):
                // only a 401 confirmed on the main connection means "signed out".
                e.Up.ConfirmSignedOut = e.Down.ConfirmSignedOut = () => StillSignedOutAsync(client);
                Entries[path] = e;
            }
            e.Refs++;
            return new AccountLease(e);
        }
    }

    internal static void Release(Entry e)
    {
        lock (Gate)
        {
            if (--e.Refs > 0) return;
            Entries.Remove(e.Path);
        }
        try
        {
            e.Healer.Dispose();
            try { e.Client.Dispose(); } catch (Exception ex) { Log.Warn($"closing a Telegram connection: {ex.Message}"); }
        }
        finally
        {
            if (e.ProcessLock is { } pl) { try { pl.Release(); } catch { } pl.Dispose(); }
        }
    }

    /// <summary>
    /// Signed out only if Telegram says so three times, about 20 seconds apart: after a network outage
    /// (a power cut, a router restart) the first answers can be a passing AUTH_KEY_UNREGISTERED.
    /// </summary>
    static async Task<bool> StillSignedOutAsync(WTelegram.Client client)
    {
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) await Task.Delay(TimeSpan.FromSeconds(20));
            try
            {
                await TL.SchemaExtensions.Users_GetUsers(client, TL.InputUser.Self).WaitAsync(TimeSpan.FromSeconds(20));
                return false;
            }
            catch (TL.RpcException e) when (e.Code == 401) { Log.Warn($"telegram answered {e.Message}; checking again"); }
            catch (Exception) { return false; } // offline / timeout: never sign anyone out on a guess
        }
        return true;
    }

    /// <summary>The PC woke up from sleep: check every connection now instead of in a minute.</summary>
    public static void WakeUp()
    {
        lock (Gate) foreach (var e in Entries.Values) e.Healer.Poke();
    }

    /// <summary>Is any drive (or the wizard) in this process using this account right now?</summary>
    public static bool InUse(string accountKey)
    {
        var path = Path.GetFullPath(Path.Combine(AccountsDir, accountKey, "telegram.session"));
        lock (Gate) return Entries.ContainsKey(path);
    }

    // ── accounts on this PC ──────────────────────────────────────────────────
    public static List<AccountInfo> Known()
    {
        if (!Directory.Exists(AccountsDir)) return [];
        var list = new List<AccountInfo>();
        foreach (var dir in Directory.GetDirectories(AccountsDir))
        {
            var meta = Path.Combine(dir, "account.json");
            if (!File.Exists(meta) || !File.Exists(Path.Combine(dir, "telegram.session"))) continue;
            try
            {
                var a = JsonSerializer.Deserialize<AccountInfo>(File.ReadAllText(meta));
                if (a is not null) list.Add(a with { Key = Path.GetFileName(dir) });
            }
            catch (Exception ex) { Log.Warn($"account {dir}: {ex.Message}"); }
        }
        return list;
    }

    static void SaveInfo(AccountInfo a)
    {
        var dir = Path.Combine(AccountsDir, a.Key);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "account.json"), JsonSerializer.Serialize(a));
    }

    public static string DisplayName(TL.User u) =>
        string.IsNullOrEmpty(u.username) ? $"{u.first_name} {u.last_name}".Trim() : "@" + u.username;

    /// <summary>
    /// A sign-in just completed on a profile-local file: make it the account's shared sign-in. If this
    /// PC already had that account signed in, the new (duplicate) sign-in is logged out — so the
    /// account does not collect a "device" per drive — and the profile switches to the existing one.
    /// Returns the lease the caller should use from now on.
    /// </summary>
    public static AccountLease Adopt(AppState state, AccountLease lease, TL.User user)
    {
        var key = user.id.ToString();
        var target = Path.GetFullPath(Path.Combine(AccountsDir, key, "telegram.session"));
        var info = new AccountInfo(key, user.id, DisplayName(user), state.Config.ApiId, state.Config.ApiHash);
        if (string.Equals(lease.Entry.Path, target, StringComparison.OrdinalIgnoreCase))
        {
            SaveInfo(info);
            return lease;
        }

        bool live;
        lock (Gate) live = Entries.ContainsKey(target);
        // a file left by a revoked or half-finished sign-in must not beat a fresh, valid one
        var probe = live ? Probe.Works : File.Exists(target) ? SignInWorks(info) : Probe.Revoked;
        bool existing = probe == Probe.Works;
        if (!existing && File.Exists(target))
        {
            // revoked: it is gone on Telegram's side. Unknown (e.g. no network): keep the file aside, so a
            // sign-in that may still be valid is not thrown away (it stays listed in Telegram's devices).
            try
            {
                if (probe == Probe.Revoked) File.Delete(target);
                else File.Move(target, target + ".retired", overwrite: true);
            }
            catch (IOException) { }
            Log.Info($"the saved sign-in of {info.Name} {(probe == Probe.Revoked ? "no longer worked" : "could not be checked")}; replaced by the new one");
        }
        if (!existing)
        {
            lock (Gate)
            {
                // a drive of the same account may have signed in meanwhile: then that one is the account's
                if (Entries.ContainsKey(target)) existing = true;
                else
                {
                    var moved = TryLock(target);
                    if (moved is null) existing = true; // another program uses that sign-in: use it, not ours
                    else
                    {
                        Entries.Remove(lease.Entry.Path);
                        lease.Entry.Stream.Relocate(target);
                        lease.Entry.Path = target;
                        if (lease.Entry.ProcessLock is { } old) { try { old.Release(); } catch { } old.Dispose(); }
                        lease.Entry.ProcessLock = moved;
                        Entries[target] = lease.Entry;
                    }
                }
            }
        }
        if (existing)
        {
            try { TL.SchemaExtensions.Auth_LogOut(lease.Client).Wait(TimeSpan.FromSeconds(10)); } catch { }
            var tempPath = lease.Entry.Path;
            lease.Dispose();
            try { File.Delete(tempPath); } catch (IOException) { }
            state.Config.AccountKey = key;
            SaveInfo(info);
            Log.Info($"account {info.Name} was already signed in on this PC; using that sign-in");
            return Acquire(state);
        }

        state.Config.AccountKey = key;
        SaveInfo(info);
        Log.Info($"account {info.Name} signed in on this PC");
        return lease;
    }

    /// <summary>Does the account's saved sign-in still work? (Checked on a throw-away client.)</summary>
    enum Probe { Works, Revoked, Unknown }

    /// <summary>This process's exclusive use of a sign-in file, or null if another DriveGram program has it.</summary>
    public static Semaphore? TryLock(string sessionPath)
    {
        var s = new Semaphore(1, 1, @"Local\DriveGram-account-" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(sessionPath).ToUpperInvariant())))[..16]);
        if (s.WaitOne(0)) return s;
        s.Dispose();
        return null;
    }

    static Probe SignInWorks(AccountInfo a)
    {
        // a sign-in another DriveGram program is using right now certainly works: never open it twice
        var probeLock = TryLock(Path.Combine(AccountsDir, a.Key, "telegram.session"));
        if (probeLock is null) return Probe.Works;
        try { return SignInWorksLocked(a); }
        finally { probeLock.Release(); probeLock.Dispose(); }
    }

    static Probe SignInWorksLocked(AccountInfo a)
    {
        var probe = AppState.Open("__probe-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            probe.Config.AccountKey = a.Key;
            probe.Config.ApiId = a.ApiId;
            probe.Config.ApiHash = a.ApiHash;
            using var client = DriveSession.NewTelegramClient(probe, null, out _);
            var check = Task.Run(() => TelegramRemote.ResumeAsync(client));
            if (!check.Wait(TimeSpan.FromSeconds(20))) { Log.Info($"saved sign-in of {a.Name}: no answer in 20 s"); return Probe.Unknown; }
            return Probe.Works;
        }
        catch (Exception ex)
        {
            var e = ex is AggregateException ag ? ag.InnerException ?? ex : ex;
            bool revoked = e is NotSignedInException || (e is TL.RpcException r && r.Code == 401);
            Log.Info($"saved sign-in of {a.Name} {(revoked ? "was revoked" : "could not be checked")}: {e.Message}");
            return revoked ? Probe.Revoked : Probe.Unknown;
        }
        finally { probe.Delete(); }
    }

    /// <summary>
    /// Drives from before 0.8 kept the sign-in inside the drive's own folder. Move it into the shared
    /// account folder (once per account; a second copy for the same account is retired).
    /// </summary>
    public static void MigrateLegacy(AppState state)
    {
        if (state.Folder is not null || !string.IsNullOrEmpty(state.Config.AccountKey) || state.Config.AccountId == 0) return;
        var local = Path.Combine(state.Dir, "telegram.session");
        if (!File.Exists(local)) return;
        var key = state.Config.AccountId.ToString();
        var target = Path.Combine(AccountsDir, key, "telegram.session");
        try
        {
            if (!File.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(local, target);
            }
            else File.Move(local, local + ".retired", overwrite: true); // the account already has a shared sign-in
            state.Config.AccountKey = key;
            state.Save();
            SaveInfo(new AccountInfo(key, state.Config.AccountId, state.Config.AccountName, state.Config.ApiId, state.Config.ApiHash));
            Log.Info($"moved the sign-in of {state.DisplayName} into the shared account folder");
        }
        catch (Exception ex) { Log.Warn($"could not move the sign-in of {state.DisplayName}: {ex.Message}"); }
    }

    /// <summary>Forget an account on this PC (sign out) when no drive uses it any more.</summary>
    /// <summary>Uninstall: log every sign-in on this PC out of Telegram (so no "DriveGram on PC" device
    /// stays listed in the account). Best effort, a few seconds per account.</summary>
    public static async Task SignOutAllAsync()
    {
        var targets = new List<AppState>();
        foreach (var a in Known())
        {
            var probe = AppState.Open("__signout-" + a.Key);
            probe.Config.AccountKey = a.Key;
            probe.Config.ApiId = a.ApiId;
            probe.Config.ApiHash = a.ApiHash;
            targets.Add(probe);
        }
        // drives from before 0.8 kept their own sign-in in the profile
        targets.AddRange(AppState.All().Where(s => s.Folder is null && string.IsNullOrEmpty(s.Config.AccountKey) && File.Exists(s.SessionPath)));
        foreach (var t in targets)
        {
            using var held = TryLock(t.SessionPath);
            if (held is null) { Log.Warn("sign out: the account is in use by another DriveGram program; skipped"); continue; }
            try
            {
                using var client = DriveSession.NewTelegramClient(t, null, out _);
                await TL.SchemaExtensions.Auth_LogOut(client).WaitAsync(TimeSpan.FromSeconds(15));
                Log.Info($"signed out {t.Config.AccountKey}");
            }
            catch (Exception ex) { Log.Warn($"sign out: {ex.Message}"); }
        }
    }

    public static async Task SignOutIfUnusedAsync(string key)
    {
        if (AppState.All().Any(a => a.Config.AccountKey == key) || InUse(key)) return;
        var dir = Path.Combine(AccountsDir, key);
        var probe = AppState.Open("__signout");
        try
        {
            probe.Config.AccountKey = key;
            if (Known().FirstOrDefault(a => a.Key == key) is { } info)
            {
                probe.Config.ApiId = info.ApiId;
                probe.Config.ApiHash = info.ApiHash;
                using var client = DriveSession.NewTelegramClient(probe, null, out _);
                try { await TL.SchemaExtensions.Auth_LogOut(client).WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (TL.RpcException ex) when (ex.Code == 401) { } // already ended (e.g. from the phone): nothing to keep
                catch (Exception ex)
                {
                    Log.Warn($"sign out of {key} did not reach Telegram ({ex.Message}); the sign-in is kept and tried again later");
                    return;
                }
            }
            Directory.Delete(dir, true);
        }
        catch (Exception ex) { Log.Warn($"sign out {key}: {ex.Message}"); }
        finally { probe.Delete(); }
    }
}
