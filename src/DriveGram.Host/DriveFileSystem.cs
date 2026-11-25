using System.Security.AccessControl;
using DriveGram.Engine;
using DriveGram.Index;
using DriveGram.Sync;
using Fsp;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace DriveGram.Host;

/// <summary>
/// The Windows side: WinFsp calls these for every Explorer / application file operation, and
/// they map onto the index (tree, metadata) and the content engine (bytes). Metadata answers
/// come from the local SQLite index only, so browsing never waits on Telegram; only reading
/// file bytes that are not cached locally does.
/// </summary>
public sealed class DriveFileSystem : FileSystemBase
{
    sealed class Handle(string id, bool dir, bool writer, bool canWrite)
    {
        /// <summary>This handle created the file (and nothing of it was ever stored remotely before).</summary>
        public bool Created { get; init; }
        public string Id { get; } = id;
        public bool Dir { get; } = dir;
        public bool Writer { get; set; } = writer;
        public bool CanWrite { get; } = canWrite;
        /// <summary>The sorted listing of a directory being enumerated through this handle.</summary>
        public List<(string Name, FileInfo Info)>? Listing;
    }

    /// <summary>The first change through a handle opened for writing makes it a writer (holds the commit
    /// until it closes). Office keeps a document open read/write for hours: counting it as a writer from
    /// the open on meant a SAVED document did not upload until Word was closed.</summary>
    void BecomeWriter(object fileNode)
    {
        if (fileNode is Handle { Dir: false, Writer: false, CanWrite: true } h)
        {
            h.Writer = true;
            _engine.Opened(h.Id, true);
        }
    }

    /// <summary>The stored (case-correct) full path, e.g. \Projects\Report.docx.</summary>
    string FullPath(Node n)
    {
        var parts = new List<string>();
        for (var x = n; x is not null && !x.IsRoot && parts.Count < 4096; x = x.Parent is null ? null : _db.Get(x.Parent))
            parts.Add(x.Name);
        parts.Reverse();
        return "\\" + string.Join("\\", parts);
    }

    sealed class DirEnum(List<(string Name, FileInfo Info)> entries, int index)
    {
        public readonly List<(string Name, FileInfo Info)> Entries = entries;
        public int Index = index;
    }

    const uint AttrDirectory = (uint)FileAttributes.Directory;
    const uint AttrArchive = (uint)FileAttributes.Archive;
    const uint AttrNormal = (uint)FileAttributes.Normal;
    const uint WriteAccess = 0x0002 | 0x0004; // FILE_WRITE_DATA | FILE_APPEND_DATA
    const ulong TotalSize = 1UL << 50;          // 1 PiB: Telegram has no fixed quota; never block a copy
    const int StatusDiskFull = unchecked((int)0xC000007F);
    const int StatusIoTimeout = unchecked((int)0xC00000B5);
    const int StatusUnexpectedIo = unchecked((int)0xC00000E9);
    const int StatusWriteProtected = unchecked((int)0xC00000A2);
    const int StatusFileIsOffline = unchecked((int)0xC0000267);

    readonly IndexDb _db;
    readonly ContentEngine _engine;
    readonly SyncService _sync;
    readonly string _label;
    readonly byte[] _sd;

    readonly bool _network;

    public DriveFileSystem(IndexDb db, ContentEngine engine, SyncService sync, string label, bool network = false)
    {
        _network = network;
        _db = db;
        _engine = engine;
        _sync = sync;
        _label = label;
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "BA";
        var sd = new RawSecurityDescriptor($"O:{me}G:{me}D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;{me})(A;;FA;;;AC)");
        _sd = new byte[sd.BinaryLength];
        sd.GetBinaryForm(_sd, 0);
    }

    bool ReadOnly => !_sync.Writable || _sync.HandingOver; // handing over: no new changes here

    /// <summary>Read-only for NEW changes; a handle that was already open for writing keeps working (Windows
    /// may deliver an app's saved data later, and the app was already told it is saved). Such changes stay
    /// on this PC and are sent when it can write again.</summary>
    bool ReadOnlyFor(object fileNode) => ReadOnly && fileNode is not Handle { CanWrite: true };
    static long Now => DateTime.UtcNow.ToFileTimeUtc();

    public override int Init(object host0)
    {
        var host = (FileSystemHost)host0;
        host.SectorSize = 4096;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        host.FileInfoTimeout = 1000;
        host.CaseSensitiveSearch = false;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = true;
        host.PostCleanupWhenModifiedOnly = true;
        host.PassQueryDirectoryPattern = false;
        host.FlushAndPurgeOnCleanup = true; // else Windows may hold the file (and its upload) for minutes
        host.VolumeCreationTime = (ulong)new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        host.VolumeSerialNumber = 0x0D17E6A4;
        host.FileSystemName = "DriveGram";
        return STATUS_SUCCESS;
    }

    public override int ExceptionHandler(Exception ex)
    {
        switch (ex)
        {
            case FileNotFoundException: return STATUS_OBJECT_NAME_NOT_FOUND;
            case DataNotLocalException: return StatusFileIsOffline;
            case TimeoutException or OperationCanceledException:
                Log.Warn($"fs: timed out ({ex.Message})");
                return StatusIoTimeout;
            case IOException io when io.Message.Contains("staging area is full"):
                Log.Warn(io.Message);
                return StatusDiskFull;
            case IOException io when (io.HResult & 0xFFFF) is 112 or 39: // ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL
                Log.Warn($"fs: the local disk is full ({io.Message})");
                return StatusDiskFull;
            default:
                Log.Error("fs: unexpected error", ex);
                return StatusUnexpectedIo;
        }
    }

    FileInfo Info(Node n)
    {
        uint attrs = n.Dir ? (n.Attrs | AttrDirectory) : (n.Attrs & ~AttrDirectory);
        if (attrs == 0) attrs = AttrNormal;
        if (attrs != AttrNormal) attrs &= ~AttrNormal;
        return new FileInfo
        {
            FileAttributes = attrs,
            FileSize = n.Dir ? 0 : (ulong)n.Size,
            AllocationSize = n.Dir ? 0 : ((ulong)n.Size + 4095) / 4096 * 4096,
            CreationTime = (ulong)n.Ctime,
            LastAccessTime = (ulong)n.Mtime,
            LastWriteTime = (ulong)n.Mtime,
            ChangeTime = (ulong)n.Mtime,
            IndexNumber = n.Ino,
            HardLinks = 0,
        };
    }

    Node NodeOf(object fileNode) => _db.Get(((Handle)fileNode).Id) ?? throw new FileNotFoundException();

    static uint CleanAttrs(uint attrs, bool dir)
    {
        attrs &= ~AttrNormal;
        return dir ? attrs | AttrDirectory : attrs & ~AttrDirectory;
    }

    // ── volume ───────────────────────────────────────────────────────────────
    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        volumeInfo.TotalSize = TotalSize;
        volumeInfo.FreeSize = TotalSize - (ulong)Math.Max(0, _db.UsedBytes());
        volumeInfo.SetVolumeLabel(_label);
        return STATUS_SUCCESS;
    }

    // Antivirus full scans and the search indexer read EVERY file of every fixed drive. Here that would
    // download the whole drive from Telegram (rate limits, a thrashing cache): they get only what is
    // already on this PC. People's apps (Explorer thumbnails included) are not affected. Decided at Open:
    // WinFsp names the requesting process only for Create/Open/Rename.
    static readonly HashSet<string> Scanners = new(StringComparer.OrdinalIgnoreCase)
    {
        "MsMpEng", "MpDefenderCoreService", "NisSrv", "MpCopyAccelerator",
        "SearchProtocolHost", "SearchIndexer", "SearchFilterHost",
        "bztransmit", "bzfilelist", "bzserv", "CrashPlanService", "IDrive", "CarboniteService",
        "AvastSvc", "AvgSvc", "avp", "ekrn", "MBAMService", "vsserv", "bdservicehost", "NortonSecurity", "mcshield",
    };
    static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (bool Scanner, long At)> ProcessKinds = new();

    bool IsBackgroundScanner()
    {
        uint pid;
        try { pid = (uint)GetOperationProcessId(); } catch { return false; }
        long now = Environment.TickCount64;
        if (ProcessKinds.TryGetValue(pid, out var k) && now - k.At < 60_000) return k.Scanner;
        bool scanner;
        try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); scanner = Scanners.Contains(p.ProcessName); }
        catch { scanner = false; } // protected processes (Defender) may refuse: decide by the name we can see
        if (!scanner)
        {
            try
            {
                var name = GetProcessImageName(pid);
                scanner = name is not null && Scanners.Contains(Path.GetFileNameWithoutExtension(name));
            }
            catch { }
        }
        ProcessKinds[pid] = (scanner, now);
        return scanner;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder name, ref uint size);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    /// <summary>The image path of a process, readable even for protected ones (limited query right).</summary>
    static string? GetProcessImageName(uint pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _createdAt = new();
    // names a file paused by itself just left (renamed away by a "safe save"): the file that takes the name
    // over within a moment is the same document, so it stays paused too (parent + upper-case name → when)
    readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), long> _pausedNames = new();

    static bool LooksTemporary(string name) =>
        name.StartsWith("~") || name.StartsWith(".~lock") ||
        Path.GetExtension(name).ToLowerInvariant() is ".tmp" or ".temp" or ".crdownload" or ".part" or ".partial" or ".download"
            or ".lock" or ".lck" or ".ldb" or ".laccdb" or ".swp" or ".swo";

    static bool InGitDir(string path) =>
        path.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase);

    public override int SetVolumeLabel(string volumeLabel, out VolumeInfo volumeInfo)
    {
        GetVolumeInfo(out volumeInfo);
        return STATUS_ACCESS_DENIED; // the name is set in DriveGram's settings; never pretend it changed
    }

    // ── lookup / security ────────────────────────────────────────────────────
    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        var n = _db.Resolve(fileName);
        if (n is null)
        {
            fileAttributes = 0;
            return _db.ResolveParent(fileName).Parent is null && IndexDb.SplitPath(fileName).Length > 1
                ? STATUS_OBJECT_PATH_NOT_FOUND
                : STATUS_OBJECT_NAME_NOT_FOUND;
        }
        fileAttributes = Info(n).FileAttributes;
        if (securityDescriptor is not null) securityDescriptor = _sd;
        return STATUS_SUCCESS;
    }

    public override int GetSecurity(object fileNode, object fileDesc, ref byte[] securityDescriptor)
    {
        securityDescriptor = _sd;
        return STATUS_SUCCESS;
    }

    public override int SetSecurity(object fileNode, object fileDesc, AccessControlSections sections, byte[] securityDescriptor) =>
        STATUS_SUCCESS; // ACLs are not stored: the drive belongs to the Windows user who mounted it

    // ── create / open / close ────────────────────────────────────────────────
    public override int Create(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes,
        byte[] securityDescriptor, ulong allocationSize, out object fileNode, out object fileDesc,
        out FileInfo fileInfo, out string normalizedName)
    {
        fileNode = default!; fileDesc = default!; fileInfo = default; normalizedName = default!;
        if (ReadOnly) return StatusWriteProtected;
        var (parent, name) = _db.ResolveParent(fileName);
        if (parent is null) return STATUS_OBJECT_PATH_NOT_FOUND;
        if (name.Length > 255) return STATUS_OBJECT_NAME_INVALID;
        if (_db.Child(parent.Id, name) is not null) return STATUS_OBJECT_NAME_COLLISION;
        if (parent.IsRoot && (name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
                              || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)))
            return STATUS_ACCESS_DENIED;
        // Explorer's thumbnail cache files (written into every photo folder on network drives): never
        // uploaded and synced; Explorer simply does without them
        if (_network && (name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) || name.Equals("ehthumbs_vista.db", StringComparison.OrdinalIgnoreCase)))
            return STATUS_ACCESS_DENIED;

        bool dir = (createOptions & FILE_DIRECTORY_FILE) != 0;
        uint attrs = CleanAttrs(dir ? fileAttributes : fileAttributes | AttrArchive, dir);
        var id = Guid.NewGuid().ToString("N");
        long now = Now;
        _db.ApplyLocal(new UpsertOp(id, parent.Id, name, dir, attrs, now, now));
        if (!dir)
        {
            long t = Environment.TickCount64;
            if (_createdAt.Count > 5000) // forget files created long ago (only the last half hour matters)
                foreach (var old in _createdAt.Where(kv => t - kv.Value > 30 * 60_000).Select(kv => kv.Key).ToList())
                    _createdAt.TryRemove(old, out _);
            _createdAt[id] = t;
        }
        bool writer = !dir && (grantedAccess & WriteAccess) != 0;
        _engine.Opened(id, writer);
        fileNode = new Handle(id, dir, writer, writer) { Created = true };
        fileInfo = Info(_db.Get(id)!);
        normalizedName = FullPath(_db.Get(id)!);
        return STATUS_SUCCESS;
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess, out object fileNode,
        out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        fileNode = default!; fileDesc = default!; fileInfo = default; normalizedName = default!;
        var n = _db.Resolve(fileName);
        if (n is null) return STATUS_OBJECT_NAME_NOT_FOUND;
        if (!n.Dir && n.Size > 0 && IsBackgroundScanner() && !_engine.IsFullyLocal(n.Id)) return StatusFileIsOffline;
        if (ReadOnly && !n.Dir && (grantedAccess & WriteAccess) != 0) return STATUS_ACCESS_DENIED;
        bool canWrite = !n.Dir && !ReadOnly && (grantedAccess & WriteAccess) != 0;
        fileNode = new Handle(n.Id, n.Dir, false, canWrite); // a writer only once it changes something
        fileInfo = Info(n);
        normalizedName = FullPath(n);
        return STATUS_SUCCESS;
    }

    public override int Overwrite(object fileNode, object fileDesc, uint fileAttributes, bool replaceFileAttributes,
        ulong allocationSize, out FileInfo fileInfo)
    {
        fileInfo = default;
        if (ReadOnly) return StatusWriteProtected;
        var n = NodeOf(fileNode);
        BecomeWriter(fileNode);
        _engine.SetSize(n.Id, 0);
        uint attrs = CleanAttrs(replaceFileAttributes ? fileAttributes | AttrArchive : n.Attrs | fileAttributes | AttrArchive, false);
        long now = Now;
        _db.ApplyLocal(new UpsertOp(n.Id, n.Parent!, n.Name, false, attrs, n.Ctime, now));
        fileInfo = Info(_db.Get(n.Id)!);
        return STATUS_SUCCESS;
    }

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        if (fileNode is not Handle h) return;
        if ((flags & CleanupDelete) != 0 && !ReadOnly)
        {
            var n = _db.Get(h.Id);
            if (n is null || n.IsRoot) return;
            bool neverStored = !n.Dir && _db.IsPending(n.Id) && _db.CommittedChunks(n.Id).Count == 0;
            // never uploaded AND (a temp file, or created minutes ago by this session — a cancelled copy or a
            // download that failed); an offline-saved file deleted by mistake later still goes to the trash
            // a copy cancelled half-way is deleted right after it started; a real new document is not
            bool justMade = _createdAt.TryGetValue(n.Id, out var made) && Environment.TickCount64 - made < 2 * 60_000;
            bool gitScratch = InGitDir(fileName) && (n.Name.EndsWith(".lock") || n.Name.StartsWith("tmp_") || LooksTemporary(n.Name));
            bool ownCopy = h.Created && h.Writer; // cancelled through the handle that was writing it
            if (!n.Dir && (n.Name.StartsWith("~$") || gitScratch || (neverStored && (LooksTemporary(n.Name) || justMade || ownCopy))))
            {
                var dropFiles = _engine.DiscardLater(n.Id); // first: the purge removes the rows it uses
                _db.ApplyLocal(new PurgeOp(n.Id));
                if (dropFiles is not null) { _db.Durable(); dropFiles(); }
                Log.Debug($"deleted for good (never uploaded / lock / git file) {fileName}");
                return;
            }
            _db.ApplyLocal(new TrashOp(n.Id, Now));
            Log.Debug($"deleted (to trash) {fileName}");
        }
        else if ((flags & CleanupSetLastWriteTime) != 0 && !ReadOnlyFor(fileNode) && _db.Get(h.Id) is { Dir: false } w)
        {
            // Written through this handle and the app did not set the time itself: "modified now". Done
            // here, not on every write: Windows delivers cached data AFTER a copy has set the original time.
            if (_db.IsPending(w.Id)) _db.SetLocalSize(w.Id, w.Size, Now);
            else _db.ApplyLocal(new UpsertOp(w.Id, w.Parent!, w.Name, false, w.Attrs, w.Ctime, Now));
        }
    }

    public override void Close(object fileNode, object fileDesc)
    {
        if (fileNode is Handle h) _engine.Closed(h.Id, h.Writer);
    }

    // ── data ─────────────────────────────────────────────────────────────────
    public override unsafe int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length,
        out uint bytesTransferred)
    {
        bytesTransferred = 0;
        var h = (Handle)fileNode;
        var n = _db.Get(h.Id) ?? throw new FileNotFoundException();
        if (offset >= (ulong)n.Size) return STATUS_END_OF_FILE;
        var span = new Span<byte>((void*)buffer, (int)length);
        bytesTransferred = (uint)_engine.Read(h.Id, (long)offset, span);
        return STATUS_SUCCESS;
    }

    public override unsafe int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length,
        bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred, out FileInfo fileInfo)
    {
        bytesTransferred = 0;
        fileInfo = default;
        if (!constrainedIo && ReadOnlyFor(fileNode)) return StatusWriteProtected; // Windows' delayed writes: always
        var h = (Handle)fileNode;
        var n = _db.Get(h.Id) ?? throw new FileNotFoundException();
        ulong size = (ulong)n.Size;
        if (constrainedIo)
        {
            if (offset >= size) { fileInfo = Info(n); return STATUS_SUCCESS; }
            if (offset + length > size) length = (uint)(size - offset);
        }
        else if (writeToEndOfFile) offset = size;

        BecomeWriter(fileNode);
        _engine.Write(h.Id, (long)offset, new ReadOnlySpan<byte>((void*)buffer, (int)length), mayExceedQuota: constrainedIo);
        bytesTransferred = length;
        fileInfo = Info(_db.Get(h.Id)!);
        return STATUS_SUCCESS;
    }

    public override int Flush(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        if (fileNode is Handle { Dir: false } fh) _engine.FlushToDisk(fh.Id); // the app wants it on disk: really
        else if (fileNode is null) _engine.FlushAllToDisk();                  // the whole volume
        fileInfo = fileNode is Handle h && _db.Get(h.Id) is { } n ? Info(n) : default;
        return STATUS_SUCCESS;
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = Info(NodeOf(fileNode));
        return STATUS_SUCCESS;
    }

    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes, ulong creationTime,
        ulong lastAccessTime, ulong lastWriteTime, ulong changeTime, out FileInfo fileInfo)
    {
        fileInfo = default;
        if (ReadOnlyFor(fileNode)) return StatusWriteProtected;
        var n = NodeOf(fileNode);
        if (n.IsRoot) { fileInfo = Info(n); return STATUS_SUCCESS; }
        uint attrs = fileAttributes != unchecked((uint)-1) ? CleanAttrs(fileAttributes, n.Dir) : n.Attrs;
        static bool Real(ulong t) => t != 0 && t < 0xFFFFFFFFFFFFFFFE;
        long ctime = Real(creationTime) ? (long)creationTime : n.Ctime;
        long mtime = Real(lastWriteTime) ? (long)lastWriteTime : n.Mtime;
        if (attrs != n.Attrs || ctime != n.Ctime || mtime != n.Mtime)
            _db.ApplyLocal(new UpsertOp(n.Id, n.Parent!, n.Name, n.Dir, attrs, ctime, mtime));
        fileInfo = Info(_db.Get(n.Id)!);
        return STATUS_SUCCESS;
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out FileInfo fileInfo)
    {
        fileInfo = default;
        if (ReadOnlyFor(fileNode)) return StatusWriteProtected;
        var n = NodeOf(fileNode);
        // An allocation-size request only matters when it cuts the file; we never preallocate.
        if (!setAllocationSize || (ulong)n.Size > newSize)
        {
            if ((ulong)n.Size != newSize) BecomeWriter(fileNode);
            _engine.SetSize(n.Id, (long)newSize);
        }
        fileInfo = Info(_db.Get(n.Id)!);
        return STATUS_SUCCESS;
    }

    // ── delete / rename ──────────────────────────────────────────────────────
    public override int CanDelete(object fileNode, object fileDesc, string fileName)
    {
        if (ReadOnly) return StatusWriteProtected;
        var n = NodeOf(fileNode);
        if (n.IsRoot) return STATUS_ACCESS_DENIED;
        if (n.Dir && _db.HasChildren(n.Id)) return STATUS_DIRECTORY_NOT_EMPTY;
        return STATUS_SUCCESS;
    }

    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists)
    {
        if (ReadOnly) return StatusWriteProtected;
        var n = NodeOf(fileNode);
        var (newParent, newName) = _db.ResolveParent(newFileName);
        if (newParent is null) return STATUS_OBJECT_PATH_NOT_FOUND;
        if (n.Dir && _db.IsSelfOrAncestor(n.Id, newParent.Id)) return STATUS_INVALID_PARAMETER;

        var ops = new List<Op>();
        bool dropExisting = false;
        var existing = _db.Child(newParent.Id, newName);
        if (existing is not null && existing.Id != n.Id)
        {
            if (!replaceIfExists) return STATUS_OBJECT_NAME_COLLISION;
            if (existing.Dir) return STATUS_ACCESS_DENIED;
            // replaced file goes to the trash (= old version kept 30 days); git rewrites its own files constantly
            dropExisting = InGitDir(newFileName) || existing.Size == 0
                           || (_db.IsPending(existing.Id) && _db.CommittedChunks(existing.Id).Count == 0);
            ops.Add(dropExisting ? new PurgeOp(existing.Id) : new TrashOp(existing.Id, Now));
        }
        // a save via "temp file + rename over the original" keeps the document's creation date
        long ctime = existing is not null && existing.Id != n.Id && !existing.Dir ? existing.Ctime : n.Ctime;
        ops.Add(new UpsertOp(n.Id, newParent.Id, newName, n.Dir, n.Attrs, ctime, n.Mtime));
        // a paused document saved again by Word or Excel (a new file taking its name) stays paused
        long tick = Environment.TickCount64;
        bool inheritPause = !n.Dir && (
            (existing is not null && existing.Id != n.Id && _engine.IsPausedByItself(existing.Id))
            || (_pausedNames.TryRemove((newParent.Id, newName.ToUpperInvariant()), out var leftAt) && tick - leftAt < 120_000));
        if (!n.Dir && n.Parent is { } oldParent && _engine.IsPausedByItself(n.Id))
            _pausedNames[(oldParent, n.Name.ToUpperInvariant())] = tick;
        var dropFiles = dropExisting ? _engine.DiscardLater(existing!.Id) : null;
        _db.ApplyLocal(ops);
        if (dropFiles is not null) { _db.Durable(); dropFiles(); } // a trashed replaced file keeps its content (an unfinished upload still completes)
        if (inheritPause) _engine.Pause(n.Id);
        if (_pausedNames.Count > 1000) _pausedNames.Clear();
        return STATUS_SUCCESS;
    }

    // ── directories ──────────────────────────────────────────────────────────
    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker,
        ref object context, out string fileName, out FileInfo fileInfo)
    {
        if (context is not DirEnum e)
        {
            var h = (Handle)fileNode;
            var entries = marker is not null && h.Listing is { } cached ? cached : null;
            if (entries is null)
            {
                var dir = NodeOf(fileNode);
                entries = new List<(string, FileInfo)>();
                if (!dir.IsRoot)
                {
                    entries.Add((".", Info(dir)));
                    entries.Add(("..", Info(_db.Get(dir.Parent!) ?? dir)));
                }
                foreach (var c in _db.Children(dir.Id).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                    entries.Add((c.Name, Info(c)));
                h.Listing = entries; // later pages of THIS listing (marker != null) reuse it
            }
            int start = 0;
            if (marker is not null)
            {
                int at = entries.FindIndex(x => string.Equals(x.Item1, marker, StringComparison.OrdinalIgnoreCase));
                start = at >= 0 ? at + 1
                    : entries.FindIndex(x => x.Item1 is not "." and not ".." &&
                                              StringComparer.OrdinalIgnoreCase.Compare(x.Item1, marker) > 0) is var g and >= 0
                        ? g : entries.Count;
            }
            context = e = new DirEnum(entries, start);
        }
        if (e.Index < e.Entries.Count)
        {
            (fileName, fileInfo) = e.Entries[e.Index++];
            return true;
        }
        fileName = default!;
        fileInfo = default;
        return false;
    }

    public override int GetDirInfoByName(object fileNode, object fileDesc, string fileName, out string normalizedName,
        out FileInfo fileInfo)
    {
        var dir = NodeOf(fileNode);
        var c = _db.Child(dir.Id, fileName);
        if (c is null) { normalizedName = default!; fileInfo = default; return STATUS_OBJECT_NAME_NOT_FOUND; }
        normalizedName = c.Name;
        fileInfo = Info(c);
        return STATUS_SUCCESS;
    }
}
