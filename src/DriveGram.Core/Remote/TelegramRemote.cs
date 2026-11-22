using System.Collections.Concurrent;
using TL;
using WTelegram;

namespace DriveGram.Remote;

/// <summary>Ids of the two private channels a drive lives in (saved locally after first lookup).</summary>
public sealed record TelegramChannels(long DataId, long DataHash, long JournalId, long JournalHash);

/// <summary>Someone who administers one of a drive's channels.</summary>
public sealed record ChannelAdmin(long UserId, string Name, bool IsOwner);

/// <summary>The administrators of one of a drive's channels.</summary>
public sealed record ChannelAdmins(string Title, bool IsJournal, List<ChannelAdmin> Admins);

/// <summary>A drive found on an account. Channels is null when its data channel could not be found.</summary>
public sealed record DriveCandidate(string? DriveId, string Name, TelegramChannels? Channels, bool Owned, bool HasVault, bool Bound);

/// <summary>
/// IRemote on a Telegram user account (MTProto via WTelegramClient). Two private channels:
///   "DriveGram Data"    — one document per encrypted chunk;
///   "DriveGram Journal" — encrypted journal entries + snapshots, and the pinned control message.
/// Uploads are split into 512 KiB parts sent in parallel through the governor (InfiDrive measured
/// sequential parts at ~6.5 Mbps on a 110 Mbps line). Reads use upload.getFile on 1 MiB-aligned
/// windows, on the document's own DC.
/// </summary>
public sealed class TelegramRemote : IRemote
{
    public const string DataTitle = "DriveGram Data";
    public const string JournalTitle = "DriveGram Journal";
    const int PartSize = 512 * 1024;
    const int SmallFileLimit = 10 * 1024 * 1024;
    static readonly TimeSpan PartTimeout = TimeSpan.FromSeconds(90);
    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);

    readonly Client _client;
    readonly Channel _data, _journal;
    readonly ConcurrentDictionary<int, Document> _docs = new(), _jdocs = new();
    readonly Governor _up, _down;
    int _controlId;
    bool _ownsClient = true;

    /// <summary>The account this drive lives on.</summary>
    public User? Account { get; private set; }

    /// <summary>The session stopped being valid while the drive was in use (raised once).</summary>
    public event Action? SignedOut;
    int _signedOut;

    public TelegramChannels Channels => new(_data.id, _data.access_hash, _journal.id, _journal.access_hash);

    IDisposable? _release;

    TelegramRemote(Client client, Channel data, Channel journal, Governor up, Governor down)
    {
        _client = client;
        _data = data;
        _journal = journal;
        _up = up;
        _down = down;
        if (client.TLConfig?.date is { } serverDate && serverDate.Year > 2000)
        {
            var off = serverDate.ToUniversalTime() - DateTime.UtcNow;
            // a config fetched long ago only makes the offset smaller (safe side: less eager to take over)
            _serverOffset = off > TimeSpan.Zero ? off : TimeSpan.Zero;
            if (Math.Abs(off.TotalMinutes) > 2) Log.Warn($"this PC's clock differs from Telegram's by {off.TotalMinutes:0} minutes");
        }
        _ctl.SignedOut += OnSignedOut;
        _up.SignedOut += OnSignedOut;
        _down.SignedOut += OnSignedOut;
    }

    void OnSignedOut()
    {
        if (Interlocked.Exchange(ref _signedOut, 1) == 0)
        {
            Log.Error("Telegram session was revoked; the drive needs a new sign-in");
            SignedOut?.Invoke();
        }
    }

    /// <summary>
    /// Resume the saved session without ever starting an interactive login (LoginUserIfNeeded insists
    /// on a phone number even for a valid saved session — the bug that made the tray think it was
    /// signed out). Asks Telegram "who am I" directly; NotSignedInException if there is no session
    /// or it was revoked (e.g. "terminate other sessions" on the phone).
    /// </summary>
    public static async Task<User> ResumeAsync(Client client)
    {
        try { await client.ConnectAsync(); }
        catch
        {
            // WTelegram keeps the failed attempt and hands it back on every later call: clear it, so the
            // next try (network back) really connects
            try { await client.ResetAsync(resetUser: false, resetSessions: false); } catch { }
            throw;
        }
        if (client.UserId == 0) throw new NotSignedInException();
        try
        {
            var users = await Patiently(() => client.Users_GetUsers(InputUser.Self));
            return users.FirstOrDefault() as User ?? throw new NotSignedInException();
        }
        catch (RpcException e) when (e.Code == 401) { throw new NotSignedInException(); }
    }

    // ── finding drives on an account ─────────────────────────────────────────
    static bool IsJournal(Channel c) => c.title == JournalTitle || c.title.StartsWith(JournalTitle + " · ");
    static bool IsData(Channel c) => c.title == DataTitle || c.title.StartsWith(DataTitle + " · ");

    static async Task<string?> ReadPinnedTextAsync(Client client, Channel channel)
    {
        var full = await Patiently(() => client.Channels_GetFullChannel(channel));
        if (full.full_chat is not ChannelFull { pinned_msg_id: > 0 } cf) return null;
        var r = await Patiently(() => client.Channels_GetMessages(channel, [new InputMessageID { id = cf.pinned_msg_id }]));
        return r.Messages.FirstOrDefault() is Message m ? m.message : null;
    }

    /// <summary>
    /// Every DriveGram drive this account can open — its own, and ones it was made an admin of (e.g. as
    /// a backup account). A journal channel is paired with its data channel by:
    ///   1. the binding in its control record (DriveIdentity.DataChannel) — drives from 0.7 on;
    ///   2. otherwise (older drives): the data channel created closest in time to the journal — both are
    ///      always created a few seconds apart — preferring the same owner;
    /// and DrivePairing.VerifyAsync proves the pairing cryptographically once the password is known.
    /// </summary>
    public static async Task<List<DriveCandidate>> DiscoverAsync(Client client)
    {
        await ResumeAsync(client);
        var all = (await Patiently(() => client.Messages_GetAllChats())).chats.Values.OfType<Channel>().Where(c => !c.flags.HasFlag(Channel.Flags.left)).ToList();
        var journals = all.Where(IsJournal).ToList();
        var datas = all.Where(IsData).ToList();
        var found = new List<(Channel J, Sync.ControlState? Ctrl)>();
        foreach (var j in journals)
        {
            Sync.ControlState? ctrl = null;
            try { ctrl = Sync.ControlState.FromJson(await ReadPinnedTextAsync(client, j)); }
            catch (Exception ex) { Log.Warn($"discover: cannot read the control record of '{j.title}': {ex.Message}"); }
            found.Add((j, ctrl));
        }

        var claimed = found.Where(f => f.Ctrl?.Drive is not null).Select(f => f.Ctrl!.Drive!.DataChannel).ToHashSet();
        var result = new List<DriveCandidate>();
        foreach (var (j, ctrl) in found.OrderBy(f => f.Ctrl?.Drive is null)) // explicit bindings first
        {
            Channel? data = ctrl?.Drive is { } id ? datas.FirstOrDefault(d => d.id == id.DataChannel) : null;
            if (data is null && ctrl?.Drive is null)
            {
                bool owned = j.flags.HasFlag(Channel.Flags.creator);
                data = datas.Where(d => !claimed.Contains(d.id))
                    .OrderBy(d => d.flags.HasFlag(Channel.Flags.creator) == owned ? 0 : 1)
                    .ThenBy(d => Math.Abs((d.date - j.date).TotalSeconds))
                    .FirstOrDefault();
            }
            if (data is not null) claimed.Add(data.id);
            result.Add(new DriveCandidate(
                ctrl?.Drive?.Id, ctrl?.Drive?.Name ?? (j.title.Length > JournalTitle.Length + 3 ? j.title[(JournalTitle.Length + 3)..] : "DriveGram"),
                data is null ? null : new TelegramChannels(data.id, data.access_hash, j.id, j.access_hash),
                Owned: j.flags.HasFlag(Channel.Flags.creator), HasVault: ctrl is not null, Bound: ctrl?.Drive is not null));
        }
        return result;
    }

    /// <summary>Create the two channels of a new drive (named after it, bound by id in the control record).</summary>
    public static async Task<TelegramChannels> CreateChannelsAsync(Client client, string name, string driveId)
    {
        await ResumeAsync(client);
        try { await CleanupLeftoversAsync(client); }
        catch (Exception ex) { Log.Warn($"cleanup of leftover channels skipped: {ex.Message}"); }
        var data = await CreateChannel(client, $"{DataTitle} · {name}",
            $"DriveGram encrypted storage of drive {driveId}. Do not post or delete here.");
        Channel journal;
        try
        {
            journal = await CreateChannel(client, $"{JournalTitle} · {name}",
                $"DriveGram journal of drive {driveId}. Do not post, pin or delete here.");
        }
        catch
        {
            // never leave half a drive behind
            try { await Patiently(() => client.Channels_DeleteChannel(data)); } catch { }
            throw;
        }
        return new TelegramChannels(data.id, data.access_hash, journal.id, journal.access_hash);
    }

    static string NameOf(User u) =>
        string.IsNullOrEmpty(u.username) ? $"{u.first_name} {u.last_name}".Trim() : "@" + u.username;

    /// <summary>Who administers each of the drive's channels (so the user can see a backup really exists).</summary>
    public static async Task<List<ChannelAdmins>> ListAdminsAsync(Client client, TelegramChannels ch)
    {
        await ResumeAsync(client);
        var all = (await Patiently(() => client.Messages_GetAllChats())).chats.Values.OfType<Channel>().ToList();
        var result = new List<ChannelAdmins>();
        foreach (var (id, isJournal) in new[] { (ch.JournalId, true), (ch.DataId, false) })
        {
            if (all.FirstOrDefault(c => c.id == id) is not { } c) continue;
            var r = await Patiently(() => client.Channels_GetParticipants(c, new ChannelParticipantsAdmins(), 0, 100, 0));
            var admins = r.participants.Select(x =>
            {
                long uid = x.UserId;
                var name = r.users.TryGetValue(uid, out var ub) && ub is User u ? NameOf(u) : uid.ToString();
                return new ChannelAdmin(uid, name, x is ChannelParticipantCreator);
            }).OrderByDescending(a => a.IsOwner).ToList();
            result.Add(new ChannelAdmins(c.title, isJournal, admins));
        }
        return result;
    }

    static readonly ChatAdminRights BackupRights = new()
    {
        flags = ChatAdminRights.Flags.change_info | ChatAdminRights.Flags.post_messages | ChatAdminRights.Flags.edit_messages
              | ChatAdminRights.Flags.delete_messages | ChatAdminRights.Flags.invite_users | ChatAdminRights.Flags.pin_messages
              | ChatAdminRights.Flags.add_admins | ChatAdminRights.Flags.manage_call | ChatAdminRights.Flags.other,
    };

    static async Task<(Channel Journal, Channel Data)> DriveChannelsAsync(Client client, TelegramChannels ch)
    {
        var all = (await Patiently(() => client.Messages_GetAllChats())).chats.Values.OfType<Channel>().ToList();
        var journal = all.FirstOrDefault(c => c.id == ch.JournalId);
        var data = all.FirstOrDefault(c => c.id == ch.DataId);
        if (journal is null || data is null) throw new DriveVanishedException();
        return (journal, data);
    }

    static async Task<string> PromoteAsync(Client client, TelegramChannels ch, User u)
    {
        if (u.id == client.UserId) throw new RpcException(400, "BACKUP_IS_SELF");
        if (u.IsBot) throw new RpcException(400, "BACKUP_IS_BOT");
        var (journal, data) = await DriveChannelsAsync(client, ch);
        foreach (var c in new[] { journal, data })
        {
            await Patiently(() => client.Channels_EditAdmin(c, u, BackupRights, "DriveGram backup"));
            Log.Info($"made {NameOf(u)} an admin of '{c.title}'");
        }
        return NameOf(u);
    }

    /// <summary>
    /// Make another account (found by its @username) a full admin of BOTH of the drive's channels, so it
    /// keeps access if this account is blocked or deleted (Telegram keeps a channel whose creator is
    /// gone; its admins keep their rights). The files stay encrypted: it still needs the drive password.
    /// </summary>
    /// <summary>Find the account behind a username (to show the user who exactly is about to become admin).</summary>
    public static async Task<User> ResolveUserAsync(Client client, string username)
    {
        await ResumeAsync(client);
        username = username.Trim().TrimStart('@');
        if (username.Length == 0) throw new ArgumentException("empty username");
        var resolved = await Patiently(() => client.Contacts_ResolveUsername(username));
        return resolved.User as User ?? throw new RpcException(400, "USERNAME_NOT_OCCUPIED");
    }

    public static async Task<string> AddBackupAdminAsync(Client client, TelegramChannels ch, User u)
    {
        await ResumeAsync(client);
        return await PromoteAsync(client, ch, u);
    }

    /// <summary>
    /// For a backup account with no username (and not a contact): one invite link per channel. Each link
    /// works ONCE and expires after 24 hours, so a leaked link is worthless. Once that account has joined
    /// both channels, PromoteJoinedAsync makes it an admin.
    /// </summary>
    public static async Task<(string Journal, string Data)> CreateInviteLinksAsync(Client client, TelegramChannels ch)
    {
        await ResumeAsync(client);
        var (journal, data) = await DriveChannelsAsync(client, ch);
        async Task<string> Link(Channel c)
        {
            var inv = await Patiently(() => client.Messages_ExportChatInvite(c, DateTime.UtcNow.AddHours(24), 1, "DriveGram backup"));
            return inv is ChatInviteExported e ? e.link : throw new IOException("Telegram did not return an invite link.");
        }
        return (await Link(journal), await Link(data));
    }

    /// <summary>Accounts that joined BOTH channels but are not admins yet (candidates for the backup).</summary>
    public static async Task<List<ChannelAdmin>> JoinedNotAdminAsync(Client client, TelegramChannels ch)
    {
        await ResumeAsync(client);
        var (journal, data) = await DriveChannelsAsync(client, ch);
        async Task<Dictionary<long, User>> Members(Channel c)
        {
            var r = await Patiently(() => client.Channels_GetParticipants(c, new ChannelParticipantsRecent(), 0, 200, 0));
            return r.participants
                .Where(p => p is not ChannelParticipantAdmin and not ChannelParticipantCreator)
                .Select(p => r.users.TryGetValue(p.UserId, out var ub) ? ub as User : null)
                .Where(u => u is not null && u.id != client.UserId && !u.IsBot)
                .ToDictionary(u => u!.id, u => u!);
        }
        var inJournal = await Members(journal);
        var inData = await Members(data);
        _joined = inJournal.Where(kv => inData.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        return _joined.Values.Select(u => new ChannelAdmin(u.id, NameOf(u), false)).ToList();
    }
    static Dictionary<long, User> _joined = new();

    /// <summary>Make an account that joined through the links an admin of both channels.</summary>
    public static async Task<string> PromoteJoinedAsync(Client client, TelegramChannels ch, long userId)
    {
        await ResumeAsync(client);
        if (!_joined.TryGetValue(userId, out var u)) { await JoinedNotAdminAsync(client, ch); u = _joined.GetValueOrDefault(userId); }
        if (u is null) throw new RpcException(400, "USER_NOT_PARTICIPANT");
        return await PromoteAsync(client, ch, u);
    }

    /// <summary>Do the drive's channels still exist for this account, and does it own them (may delete them)?</summary>
    public static async Task<(bool Exists, bool Owner)> ChannelsStatusAsync(Client client, TelegramChannels ch)
    {
        await ResumeAsync(client);
        var all = (await Patiently(() => client.Messages_GetAllChats())).chats.Values.OfType<Channel>()
            .Where(c => !c.flags.HasFlag(Channel.Flags.left)).ToList();
        var data = all.FirstOrDefault(c => c.id == ch.DataId);
        var journal = all.FirstOrDefault(c => c.id == ch.JournalId);
        bool exists = data is not null || journal is not null;
        bool owner = (data ?? journal) is { } any && any.flags.HasFlag(Channel.Flags.creator)
                     && (journal is null || journal.flags.HasFlag(Channel.Flags.creator));
        return (exists, owner);
    }

    /// <summary>
    /// Permanently delete a drive's two channels (and with them every stored chunk, journal entry and
    /// snapshot). The journal goes first, so other PCs immediately see the drive as gone rather than
    /// half there. Only the owner can do this; channels already gone are skipped.
    /// </summary>
    public static async Task DeleteChannelsAsync(Client client, TelegramChannels ch)
    {
        await ResumeAsync(client);
        var all = (await Patiently(() => client.Messages_GetAllChats())).chats.Values.OfType<Channel>().ToList();
        // a damaged or altered record must never turn "delete this drive" into "delete another drive"
        var journal = all.FirstOrDefault(c => c.id == ch.JournalId);
        var data = all.FirstOrDefault(c => c.id == ch.DataId);
        if (journal is not null && !IsJournal(journal))
            throw new InvalidOperationException($"'{journal.title}' is not a DriveGram journal channel; nothing was deleted.");
        if (data is not null && !data.title.StartsWith(DataTitle))
            throw new InvalidOperationException($"'{data.title}' is not a DriveGram data channel; nothing was deleted.");
        if (journal is not null)
        {
            Sync.ControlState? ctrl = null;
            try { ctrl = Sync.ControlState.FromJson(await ReadPinnedTextAsync(client, journal)); } catch { }
            if (ctrl?.Drive is { } identity && identity.DataChannel != ch.DataId)
                throw new InvalidOperationException("The drive's journal names a different data channel; nothing was deleted.");
        }
        foreach (var other in all.Where(c => IsJournal(c) && c.id != ch.JournalId && !c.flags.HasFlag(Channel.Flags.left)))
        {
            Sync.ControlState? oc;
            try { oc = Sync.ControlState.FromJson(await ReadPinnedTextAsync(client, other)); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not check the drive '{other.title}' ({ex.Message}); to be safe, nothing was deleted. Try again.");
            }
            if (oc?.Drive?.DataChannel == ch.DataId)
                throw new InvalidOperationException($"The data channel also belongs to the drive '{other.title}'; nothing was deleted.");
        }
        foreach (var id in new[] { ch.JournalId, ch.DataId })
        {
            if (all.FirstOrDefault(c => c.id == id) is not { } c) continue;
            if (!c.flags.HasFlag(Channel.Flags.creator))
                throw new UnauthorizedAccessException("Only the drive's owner account can delete its channels.");
            await Patiently(() => client.Channels_DeleteChannel(c));
            Log.Info($"deleted channel '{c.title}' ({c.id})");
        }
    }

    /// <summary>Open a drive whose channels are known (from discovery or this PC's saved config).</summary>
    /// <param name="up">/<param name="down">the account's shared governors (drives on one account share
    /// Telegram's per-account rate limit); new ones when null.</param>
    /// <param name="release">disposed with the remote (e.g. the lease on a shared connection).</param>
    public static async Task<TelegramRemote> OpenAsync(Client client, TelegramChannels known,
        Governor? up = null, Governor? down = null, bool ownsClient = true, IDisposable? release = null)
    {
        var account = await ResumeAsync(client);
        var all = (await Patiently(() => client.Messages_GetAllChats())).chats.Values.OfType<Channel>().ToList();
        var data = all.FirstOrDefault(c => c.id == known.DataId && !c.flags.HasFlag(Channel.Flags.left));
        var journal = all.FirstOrDefault(c => c.id == known.JournalId && !c.flags.HasFlag(Channel.Flags.left));
        // A drive this PC knew is gone: never quietly put an empty one in its place.
        if (data is null || journal is null) throw new DriveVanishedException();
        client.FloodRetryThreshold = 0; // flood waits surface to the governor from now on
        return new TelegramRemote(client, data, journal, up ?? new Governor("upload", 8), down ?? new Governor("download", 8))
            { Account = account, _ownsClient = ownsClient, _release = release };
    }

    /// <summary>Does stored message `msg` in the data channel hold chunk `chunkId`? (Its file name is
    /// derived from the chunk id, which only the password can produce: a wrong pairing never passes.)</summary>
    public async Task<bool> HoldsChunkAsync(int msg, string chunkId, CancellationToken ct)
    {
        try
        {
            var name = (await GetDocAsync(_data, _docs, msg, ct)).Filename;
            return name is null || name == $"{chunkId[..32]}.bin"; // only a clear contradiction fails
        }
        catch (FileNotFoundException) { return false; }
    }

    /// <summary>
    /// For setup-time calls on a client that may be shared with a mounted drive: that drive turned
    /// the library's automatic flood-wait handling off (FloodRetryThreshold = 0, so its governor sees
    /// every wait). Setup calls are few and not governed, so they wait out FLOOD_WAIT themselves —
    /// e.g. Telegram makes a second channel creation right after the first wait ~6 s.
    /// </summary>
    static async Task<T> Patiently<T>(Func<Task<T>> call)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return await call(); }
            catch (RpcException e) when (e.Code == 420 && e.X <= 300 && attempt <= 5)
            {
                Log.Info($"Telegram asks to wait {e.X}s (setup); waiting");
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, e.X) + 1));
            }
        }
    }

    /// <summary>A channel of ours that holds nothing at all (only the "channel created" service message).</summary>
    static async Task<bool> IsEmptyAsync(Client client, Channel c)
    {
        if (c.date > DateTime.UtcNow.AddHours(-1)) return false; // maybe another PC is creating a drive now
        var h = await Patiently(() => client.Messages_GetHistory(c, limit: 5));
        return h.Count <= 2 && !h.Messages.OfType<Message>().Any();
    }

    /// <summary>
    /// Remove channels left over by an interrupted drive creation (e.g. the data channel was created,
    /// then the journal's creation failed). Only channels that are ours, new-style named, bound to no
    /// drive, and completely EMPTY — a channel with any content is never touched.
    /// </summary>
    static async Task CleanupLeftoversAsync(Client client)
    {
        var all = (await Patiently(() => client.Messages_GetAllChats())).chats.Values.OfType<Channel>()
            .Where(c => !c.flags.HasFlag(Channel.Flags.left)).ToList();
        var bound = new HashSet<long>();
        var unboundJournals = new List<Channel>();
        foreach (var j in all.Where(IsJournal))
        {
            Sync.ControlState? ctrl;
            try { ctrl = Sync.ControlState.FromJson(await ReadPinnedTextAsync(client, j)); }
            catch (Exception ex)
            {
                // cannot tell which data channel this journal owns: delete nothing this time
                Log.Warn($"cleanup skipped: cannot read the control record of '{j.title}': {ex.Message}");
                return;
            }
            if (ctrl?.Drive is { } id) { bound.Add(id.DataChannel); bound.Add(j.id); }
            else if (ctrl is not null) bound.Add(j.id); // an older drive: keep it (and never guess its data channel away)
            else unboundJournals.Add(j);
        }
        var leftovers = all.Where(c => c.flags.HasFlag(Channel.Flags.creator) && !bound.Contains(c.id)
                && (c.title.StartsWith(DataTitle + " · ") || unboundJournals.Contains(c)))
            .ToList();
        foreach (var c in leftovers)
        {
            if (!await IsEmptyAsync(client, c)) continue;
            await Patiently(() => client.Channels_DeleteChannel(c));
            Log.Info($"removed an empty leftover channel '{c.title}' ({c.id})");
        }
    }

    static async Task<Channel> CreateChannel(Client client, string title, string about)
    {
        var updates = await Patiently(() => client.Channels_CreateChannel(title, about, broadcast: true));
        var channel = updates.Chats.Values.OfType<Channel>().First();
        Log.Info($"created private channel '{title}'");
        return channel;
    }

    async Task<Client> DcClient(int dcId) =>
        dcId == 0 || dcId == _client.TLConfig?.this_dc ? _client
            : await _client.GetClientForDC(-dcId, true).WaitAsync(ReadTimeout);

    long _bytesSent;
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>Journal entries and the control record (lease heartbeat): small, and they must not wait
    /// behind megabytes of chunk parts, or a slow line lets the lease expire while uploading.</summary>
    readonly Governor _ctl = new("control", 2);

    /// <summary>When Telegram asked this account to wait (flood), until when.</summary>
    public DateTime? WaitingUntil => new[] { _up.WaitingUntil, _down.WaitingUntil, _ctl.WaitingUntil }.Max();

    async Task<InputFileBase> UploadPartsAsync(byte[] payload, string name, CancellationToken ct, Governor? lane = null)
    {
        var gov = lane ?? _up;
        int parts = Math.Max(1, (payload.Length + PartSize - 1) / PartSize);
        bool big = payload.Length > SmallFileLimit;
        long fileId = Random.Shared.NextInt64();
        await Parallel.ForEachAsync(Enumerable.Range(0, parts),
            new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct }, async (i, token) =>
            {
                var bytes = payload.AsSpan(i * PartSize, Math.Min(PartSize, payload.Length - i * PartSize)).ToArray();
                await gov.RunAsync(async () =>
                {
                    bool ok = big
                        ? await _client.Upload_SaveBigFilePart(fileId, i, parts, bytes)
                        : await _client.Upload_SaveFilePart(fileId, i, bytes);
                    return ok ? true : throw new IOException($"Telegram rejected part {i}");
                }, token, PartTimeout);
                Interlocked.Add(ref _bytesSent, bytes.Length);
            });
        return big
            ? new InputFileBig { id = fileId, parts = parts, name = name }
            : new InputFile { id = fileId, parts = parts, name = name, md5_checksum = "" };
    }

    async Task<int> SendDocumentAsync(Channel channel, ConcurrentDictionary<int, Document> docs, string name,
        byte[] payload, CancellationToken ct, Governor? lane = null)
    {
        var gov = lane ?? _up;
        var file = await UploadPartsAsync(payload, name, ct, gov);
        var msg = await gov.RunAsync(() => _client.SendMediaAsync(channel, "", file, "application/octet-stream"), ct, PartTimeout);
        if (msg.media is MessageMediaDocument { document: Document d }) docs[msg.id] = d;
        return msg.id;
    }

    public Task<int> UploadChunkAsync(string name, byte[] payload, CancellationToken ct) =>
        SendDocumentAsync(_data, _docs, name, payload, ct);

    public Task<int> AppendJournalAsync(string name, byte[] payload, CancellationToken ct) =>
        SendDocumentAsync(_journal, _jdocs, name, payload, ct, _ctl);

    async Task<Document> GetDocAsync(Channel channel, ConcurrentDictionary<int, Document> docs, int msg, CancellationToken ct)
    {
        if (docs.TryGetValue(msg, out var d)) return d;
        var r = await _down.RunAsync(() => _client.Channels_GetMessages(channel, [new InputMessageID { id = msg }]), ct, ReadTimeout);
        if (r.Messages.FirstOrDefault() is Message { media: MessageMediaDocument { document: Document doc } })
            return docs[msg] = doc;
        throw new FileNotFoundException($"Telegram message {msg} in '{channel.title}' is missing or has no file.");
    }

    public async Task<byte[]> ReadWindowAsync(int msg, int window, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var doc = await GetDocAsync(_data, _docs, msg, ct);
            try
            {
                var client = await DcClient(doc.dc_id);
                var r = await _down.RunAsync(() => client.Upload_GetFile(doc.ToFileLocation(),
                    (long)window * IRemote.WindowSize, IRemote.WindowSize), ct, ReadTimeout);
                return r is Upload_File f ? f.bytes : throw new IOException("Unexpected CDN redirect from Telegram.");
            }
            catch (RpcException e) when (e.Message.StartsWith("FILE_REFERENCE") && attempt < 2)
            {
                _docs.TryRemove(msg, out _); // file references expire; fetch the message again
            }
        }
    }

    public async Task DeleteChunksAsync(IReadOnlyList<int> msgs, CancellationToken ct)
    {
        foreach (var batch in msgs.Chunk(100))
        {
            await _up.RunAsync(() => _client.Channels_DeleteMessages(_data, batch), ct, ReadTimeout);
            foreach (var m in batch) _docs.TryRemove(m, out _);
        }
    }

    public async Task<IReadOnlyList<JournalItem>> ListJournalAsync(int afterId, CancellationToken ct)
    {
        var items = new List<JournalItem>();
        int offsetId = 0;
        while (true)
        {
            int off = offsetId;
            var history = await _down.RunAsync(() => _client.Messages_GetHistory(_journal, offset_id: off, limit: 100, min_id: afterId), ct, ReadTimeout);
            var msgs = history.Messages;
            if (msgs.Length == 0) break;
            foreach (var mb in msgs)
            {
                if (mb is Message { media: MessageMediaDocument { document: Document d } } m)
                {
                    if (m.fwd_from is not null)
                    {
                        // DriveGram never forwards: a forwarded copy of an old entry must not be replayed
                        Log.Warn($"journal message {m.id} is a forwarded copy; ignored");
                        continue;
                    }
                    _jdocs[m.id] = d;
                    items.Add(new JournalItem(m.id, d.Filename ?? "", d.size));
                }
            }
            offsetId = msgs.Min(m => m.ID);
            if (offsetId <= afterId + 1) break;
        }
        return items.OrderBy(i => i.Id).ToList();
    }

    public async Task<byte[]> ReadJournalAsync(int id, CancellationToken ct)
    {
        var doc = await GetDocAsync(_journal, _jdocs, id, ct);
        if (doc.size > (1L << 30)) throw new InvalidDataException($"journal message {id} is {doc.size:N0} bytes: not a DriveGram record");
        return await _down.RunAsync(async () =>
        {
            using var ms = new MemoryStream(); // fresh for every attempt
            await _client.DownloadFileAsync(doc, ms);
            return ms.ToArray();
        }, ct, TimeSpan.FromMinutes(10));
    }

    async Task<int> ControlIdAsync(CancellationToken ct)
    {
        if (_controlId != 0) return _controlId;
        var full = await _ctl.RunAsync(() => _client.Channels_GetFullChannel(_journal), ct, ReadTimeout);
        return _controlId = full.full_chat is ChannelFull cf ? cf.pinned_msg_id : 0;
    }

    public async Task<string?> ReadControlAsync(CancellationToken ct)
    {
        int id = await ControlIdAsync(ct);
        if (id == 0) return null;
        var r = await _ctl.RunAsync(() => _client.Channels_GetMessages(_journal, [new InputMessageID { id = id }]), ct, ReadTimeout);
        if (r.Messages.FirstOrDefault() is not Message m) return null;
        ControlEditedAtUtc = (m.edit_date > m.date ? m.edit_date : m.date).ToUniversalTime();
        return m.message;
    }

    public DateTime? ControlEditedAtUtc { get; private set; }

    /// <summary>Telegram's clock: the local clock corrected by the offset seen when the connection
    /// received the server configuration (a PC with a wrong clock still judges server times right).</summary>
    public DateTime ServerNowUtc => DateTime.UtcNow + _serverOffset;
    TimeSpan _serverOffset;

    public async Task WriteControlAsync(string text, CancellationToken ct)
    {
        int id = await ControlIdAsync(ct);
        if (id == 0)
        {
            var m = await _ctl.RunAsync(() => _client.SendMessageAsync(_journal, text), ct, ReadTimeout);
            await _ctl.RunAsync(() => _client.Messages_UpdatePinnedMessage(_journal, m.id, silent: true), ct, ReadTimeout);
            _controlId = m.id;
            return;
        }
        try
        {
            await _ctl.RunAsync(() => _client.Messages_EditMessage(_journal, id, message: text), ct, ReadTimeout);
        }
        catch (RpcException e) when (e.Message == "MESSAGE_NOT_MODIFIED") { }
    }

    public ValueTask DisposeAsync()
    {
        _up.SignedOut -= OnSignedOut;
        _ctl.SignedOut -= OnSignedOut;
        _down.SignedOut -= OnSignedOut;
        if (_ownsClient) _client.Dispose();
        Interlocked.Exchange(ref _release, null)?.Dispose();
        return ValueTask.CompletedTask;
    }
}
