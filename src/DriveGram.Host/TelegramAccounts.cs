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
                var client = DriveSession.NewTelegramClient(state, askPassword, out var stream);
                e = new Entry
                {
                    Path = path, Client = client, Stream = stream,
                    Up = new Governor("upload", 8), Down = new Governor("download", 8),
                };
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
        try { e.Client.Dispose(); } catch (Exception ex) { Log.Warn($"closing a Telegram connection: {ex.Message}"); }
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

        bool existing;
        lock (Gate) existing = Entries.ContainsKey(target) || File.Exists(target);
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

        lock (Gate)
        {
            Entries.Remove(lease.Entry.Path);
            lease.Entry.Stream.Relocate(target);
            lease.Entry.Path = target;
            Entries[target] = lease.Entry;
        }
        state.Config.AccountKey = key;
        SaveInfo(info);
        Log.Info($"account {info.Name} signed in on this PC");
        return lease;
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
                try { await TL.SchemaExtensions.Auth_LogOut(client); } catch { }
            }
            Directory.Delete(dir, true);
        }
        catch (Exception ex) { Log.Warn($"sign out {key}: {ex.Message}"); }
        finally { probe.Delete(); }
    }
}
