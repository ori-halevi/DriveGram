namespace DriveGram.Remote;

/// <summary>
/// IRemote on a local (or network) folder. Used by the tests and by `drivegram mount --folder`
/// to try the drive without Telegram. Same semantics as Telegram: ids only grow, data and journal
/// share one id sequence (like messages in channels), files are written atomically.
/// </summary>
public sealed class FolderRemote : IRemote
{
    readonly string _root, _data, _journal;
    readonly object _gate = new();
    public int Latency { get; set; } // ms, to simulate a slow network in tests

    public FolderRemote(string root)
    {
        _root = root;
        _data = Path.Combine(root, "data");
        _journal = Path.Combine(root, "journal");
        Directory.CreateDirectory(_data);
        Directory.CreateDirectory(_journal);
    }

    int NextId()
    {
        lock (_gate)
        {
            var p = Path.Combine(_root, "next-id");
            int id = File.Exists(p) ? int.Parse(File.ReadAllText(p)) : 1;
            WriteAtomic(p, System.Text.Encoding.ASCII.GetBytes((id + 1).ToString()));
            return id;
        }
    }

    static void WriteAtomic(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    async Task Delay(CancellationToken ct) { if (Latency > 0) await Task.Delay(Latency, ct); }

    public int StoredChunkCount => Directory.GetFiles(_data, "*.bin").Length;

    public async Task<int> UploadChunkAsync(string name, byte[] payload, CancellationToken ct)
    {
        await Delay(ct);
        int id = NextId();
        WriteAtomic(Path.Combine(_data, $"{id}.bin"), payload);
        return id;
    }

    public async Task<byte[]> ReadWindowAsync(int msg, int window, CancellationToken ct)
    {
        await Delay(ct);
        using var fs = new FileStream(Path.Combine(_data, $"{msg}.bin"), FileMode.Open, FileAccess.Read, FileShare.Read);
        long off = (long)window * IRemote.WindowSize;
        if (off >= fs.Length) return [];
        var buf = new byte[(int)Math.Min(IRemote.WindowSize, fs.Length - off)];
        fs.Position = off;
        await fs.ReadExactlyAsync(buf, ct);
        return buf;
    }

    public Task DeleteChunksAsync(IReadOnlyList<int> msgs, CancellationToken ct)
    {
        foreach (var m in msgs) File.Delete(Path.Combine(_data, $"{m}.bin"));
        return Task.CompletedTask;
    }

    public async Task<int> AppendJournalAsync(string name, byte[] payload, CancellationToken ct)
    {
        await Delay(ct);
        int id = NextId();
        WriteAtomic(Path.Combine(_journal, $"{id:D10}_{name}"), payload);
        return id;
    }

    public Task<IReadOnlyList<JournalItem>> ListJournalAsync(int afterId, CancellationToken ct)
    {
        var items = Directory.GetFiles(_journal)
            .Select(Path.GetFileName)
            .Where(f => f!.Length > 11 && f[10] == '_' && !f.EndsWith(".tmp"))
            .Select(f => new JournalItem(int.Parse(f![..10]), f[11..]))
            .Where(j => j.Id > afterId)
            .OrderBy(j => j.Id)
            .ToList();
        return Task.FromResult<IReadOnlyList<JournalItem>>(items);
    }

    public Task<byte[]> ReadJournalAsync(int id, CancellationToken ct)
    {
        var f = Directory.GetFiles(_journal, $"{id:D10}_*").Single();
        return Task.FromResult(File.ReadAllBytes(f));
    }

    public Task<string?> ReadControlAsync(CancellationToken ct)
    {
        var p = Path.Combine(_root, "control.json");
        return Task.FromResult(File.Exists(p) ? File.ReadAllText(p) : null);
    }

    public Task WriteControlAsync(string text, CancellationToken ct)
    {
        WriteAtomic(Path.Combine(_root, "control.json"), System.Text.Encoding.UTF8.GetBytes(text));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
