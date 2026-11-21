using System.Collections.Concurrent;
using TL;
using WTelegram;

namespace DriveGram.Remote;

/// <summary>Ids of the two private channels a drive lives in (saved locally after first lookup).</summary>
public sealed record TelegramChannels(long DataId, long DataHash, long JournalId, long JournalHash);

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
        await client.ConnectAsync();
        if (client.UserId == 0) throw new NotSignedInException();
        try
        {
            var users = await client.Users_GetUsers(InputUser.Self);
            return users.FirstOrDefault() as User ?? throw new NotSignedInException();
        }
        catch (RpcException e) when (e.Code == 401) { throw new NotSignedInException(); }
    }

    // ── finding drives on an account ─────────────────────────────────────────
    static bool IsJournal(Channel c) => c.title == JournalTitle || c.title.StartsWith(JournalTitle + " · ");
    static bool IsData(Channel c) => c.title == DataTitle || c.title.StartsWith(DataTitle + " · ");

    static async Task<string?> ReadPinnedTextAsync(Client client, Channel channel)
    {
        var full = await client.Channels_GetFullChannel(channel);
        if (full.full_chat is not ChannelFull { pinned_msg_id: > 0 } cf) return null;
        var r = await client.Channels_GetMessages(channel, [new InputMessageID { id = cf.pinned_msg_id }]);
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
        var all = (await client.Messages_GetAllChats()).chats.Values.OfType<Channel>().Where(c => !c.flags.HasFlag(Channel.Flags.left)).ToList();
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
        var data = await CreateChannel(client, $"{DataTitle} · {name}",
            $"DriveGram encrypted storage of drive {driveId}. Do not post or delete here.");
        var journal = await CreateChannel(client, $"{JournalTitle} · {name}",
            $"DriveGram journal of drive {driveId}. Do not post, pin or delete here.");
        return new TelegramChannels(data.id, data.access_hash, journal.id, journal.access_hash);
    }

    /// <summary>Open a drive whose channels are known (from discovery or this PC's saved config).</summary>
    /// <param name="up">/<param name="down">the account's shared governors (drives on one account share
    /// Telegram's per-account rate limit); new ones when null.</param>
    /// <param name="release">disposed with the remote (e.g. the lease on a shared connection).</param>
    public static async Task<TelegramRemote> OpenAsync(Client client, TelegramChannels known,
        Governor? up = null, Governor? down = null, bool ownsClient = true, IDisposable? release = null)
    {
        var account = await ResumeAsync(client);
        var all = (await client.Messages_GetAllChats()).chats.Values.OfType<Channel>().ToList();
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

    static async Task<Channel> CreateChannel(Client client, string title, string about)
    {
        var updates = await client.Channels_CreateChannel(title, about, broadcast: true);
        var channel = updates.Chats.Values.OfType<Channel>().First();
        Log.Info($"created private channel '{title}'");
        return channel;
    }

    async Task<Client> DcClient(int dcId) =>
        dcId == 0 || dcId == _client.TLConfig?.this_dc ? _client : await _client.GetClientForDC(-dcId, true);

    async Task<InputFileBase> UploadPartsAsync(byte[] payload, string name, CancellationToken ct)
    {
        int parts = Math.Max(1, (payload.Length + PartSize - 1) / PartSize);
        bool big = payload.Length > SmallFileLimit;
        long fileId = Random.Shared.NextInt64();
        await Parallel.ForEachAsync(Enumerable.Range(0, parts),
            new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct }, async (i, token) =>
            {
                var bytes = payload.AsSpan(i * PartSize, Math.Min(PartSize, payload.Length - i * PartSize)).ToArray();
                await _up.RunAsync(async () =>
                {
                    bool ok = big
                        ? await _client.Upload_SaveBigFilePart(fileId, i, parts, bytes)
                        : await _client.Upload_SaveFilePart(fileId, i, bytes);
                    return ok ? true : throw new IOException($"Telegram rejected part {i}");
                }, token, PartTimeout);
            });
        return big
            ? new InputFileBig { id = fileId, parts = parts, name = name }
            : new InputFile { id = fileId, parts = parts, name = name, md5_checksum = "" };
    }

    async Task<int> SendDocumentAsync(Channel channel, ConcurrentDictionary<int, Document> docs, string name,
        byte[] payload, CancellationToken ct)
    {
        var file = await UploadPartsAsync(payload, name, ct);
        var msg = await _up.RunAsync(() => _client.SendMediaAsync(channel, "", file, "application/octet-stream"), ct, PartTimeout);
        if (msg.media is MessageMediaDocument { document: Document d }) docs[msg.id] = d;
        return msg.id;
    }

    public Task<int> UploadChunkAsync(string name, byte[] payload, CancellationToken ct) =>
        SendDocumentAsync(_data, _docs, name, payload, ct);

    public Task<int> AppendJournalAsync(string name, byte[] payload, CancellationToken ct) =>
        SendDocumentAsync(_journal, _jdocs, name, payload, ct);

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
                    _jdocs[m.id] = d;
                    items.Add(new JournalItem(m.id, d.Filename ?? ""));
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
        using var ms = new MemoryStream();
        await _down.RunAsync(() => _client.DownloadFileAsync(doc, ms), ct, TimeSpan.FromMinutes(10));
        return ms.ToArray();
    }

    async Task<int> ControlIdAsync(CancellationToken ct)
    {
        if (_controlId != 0) return _controlId;
        var full = await _down.RunAsync(() => _client.Channels_GetFullChannel(_journal), ct, ReadTimeout);
        return _controlId = full.full_chat is ChannelFull cf ? cf.pinned_msg_id : 0;
    }

    public async Task<string?> ReadControlAsync(CancellationToken ct)
    {
        int id = await ControlIdAsync(ct);
        if (id == 0) return null;
        var r = await _down.RunAsync(() => _client.Channels_GetMessages(_journal, [new InputMessageID { id = id }]), ct, ReadTimeout);
        return r.Messages.FirstOrDefault() is Message m ? m.message : null;
    }

    public async Task WriteControlAsync(string text, CancellationToken ct)
    {
        int id = await ControlIdAsync(ct);
        if (id == 0)
        {
            var m = await _up.RunAsync(() => _client.SendMessageAsync(_journal, text), ct, ReadTimeout);
            await _up.RunAsync(() => _client.Messages_UpdatePinnedMessage(_journal, m.id, silent: true), ct, ReadTimeout);
            _controlId = m.id;
            return;
        }
        try
        {
            await _up.RunAsync(() => _client.Messages_EditMessage(_journal, id, message: text), ct, ReadTimeout);
        }
        catch (RpcException e) when (e.Message == "MESSAGE_NOT_MODIFIED") { }
    }

    public ValueTask DisposeAsync()
    {
        _up.SignedOut -= OnSignedOut;
        _down.SignedOut -= OnSignedOut;
        if (_ownsClient) _client.Dispose();
        Interlocked.Exchange(ref _release, null)?.Dispose();
        return ValueTask.CompletedTask;
    }
}
