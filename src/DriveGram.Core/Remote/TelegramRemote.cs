using System.Collections.Concurrent;
using TL;
using WTelegram;

namespace DriveGram.Remote;

/// <summary>Ids of the two private channels a drive lives in (saved locally after first lookup).</summary>
public sealed record TelegramChannels(long DataId, long DataHash, long JournalId, long JournalHash);

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

    /// <summary>The account this drive lives on.</summary>
    public User? Account { get; private set; }

    /// <summary>The session stopped being valid while the drive was in use (raised once).</summary>
    public event Action? SignedOut;
    int _signedOut;

    public TelegramChannels Channels => new(_data.id, _data.access_hash, _journal.id, _journal.access_hash);

    TelegramRemote(Client client, Channel data, Channel journal, int uploadParallel, int downloadParallel)
    {
        _client = client;
        _data = data;
        _journal = journal;
        _up = new Governor("upload", uploadParallel);
        _down = new Governor("download", downloadParallel);
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

    /// <summary>Does this account hold a DriveGram drive (both channels)?</summary>
    public static async Task<bool> DriveExistsAsync(Client client, TelegramChannels? known)
    {
        await ResumeAsync(client);
        var all = (await client.Messages_GetAllChats()).chats.Values.OfType<Channel>().ToList();
        bool Has(long? id, string title) => all.Any(c => c.id == id || c.title == title);
        return Has(known?.DataId, DataTitle) && Has(known?.JournalId, JournalTitle);
    }

    /// <summary>Open — or create — the drive's channels on the signed-in account.</summary>
    public static async Task<TelegramRemote> OpenAsync(Client client, TelegramChannels? known, bool createIfMissing,
        int uploadParallel = 8, int downloadParallel = 8)
    {
        var account = await ResumeAsync(client);
        var all = (await client.Messages_GetAllChats()).chats.Values.OfType<Channel>().ToList();

        Channel? Find(long? id, string title) =>
            (id is { } i ? all.FirstOrDefault(c => c.id == i) : null) ?? all.FirstOrDefault(c => c.title == title);

        var data = Find(known?.DataId, DataTitle);
        var journal = Find(known?.JournalId, JournalTitle);
        if (data is null || journal is null)
        {
            // A drive this PC knew is gone: never quietly put an empty one in its place.
            if (known is not null) throw new DriveVanishedException();
            if (!createIfMissing) throw new NoDriveException();
            data ??= await CreateChannel(client, DataTitle, "DriveGram encrypted storage. Do not post or delete here.");
            journal ??= await CreateChannel(client, JournalTitle, "DriveGram journal. Do not post, pin or delete here.");
        }
        client.FloodRetryThreshold = 0; // flood waits surface to the governor from now on
        return new TelegramRemote(client, data, journal, uploadParallel, downloadParallel) { Account = account };
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
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
