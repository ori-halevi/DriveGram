using System.Text.Json;
using System.Text.Json.Serialization;

namespace DriveGram.Index;

/// <summary>
/// Journal operations. Every change to the tree is one of these; the local index is just the
/// result of applying them in journal order. Each op carries the FULL resulting state of what
/// it touches (not a delta), so re-applying an op is harmless (idempotent).
/// Times are Windows FILETIME (100ns ticks since 1601, UTC).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(UpsertOp), "up")]
[JsonDerivedType(typeof(ContentOp), "ct")]
[JsonDerivedType(typeof(TrashOp), "tr")]
[JsonDerivedType(typeof(RestoreOp), "rs")]
[JsonDerivedType(typeof(PurgeOp), "pg")]
public abstract record Op(string Id);

/// <summary>Create, rename, move, change attributes/times. Never changes content.</summary>
public sealed record UpsertOp(string Id, string Parent, string Name, bool Dir, uint Attrs, long Ctime, long Mtime) : Op(Id);

/// <summary>New content of a file. Emitted only after every referenced chunk is stored remotely.</summary>
public sealed record ContentOp(string Id, long Size, long Mtime, ChunkRef[] Chunks) : Op(Id);

/// <param name="C">chunk id</param><param name="M">message id</param>
/// <param name="Z">plaintext size</param><param name="H">sha256 of plaintext (hex)</param>
public sealed record ChunkRef(string C, int M, int Z, string H);

/// <summary>Move to the drive's own trash (Windows has no recycle bin for this drive).</summary>
public sealed record TrashOp(string Id, long At) : Op(Id);

public sealed record RestoreOp(string Id, string Parent, string Name) : Op(Id);

/// <summary>Permanent delete of a (trashed) subtree. Its chunks become garbage.</summary>
/// <param name="Ids">exactly what the purging PC deleted (1.0.3+). Every PC deletes only these; anything
/// else it finds under them (moved there meanwhile, or not sent yet) goes to the root instead of being lost.
/// Null in older entries: the whole subtree, as before.</param>
public sealed record PurgeOp(string Id, string[]? Ids = null) : Op(Id);

/// <param name="Ctl">1 (1.0.3+): the writer signs the control record — every reader then refuses an unsigned one.</param>
public sealed record JournalEntry(int V, string Dev, long Ts, Op[] Ops, int Ctl = 0);

public static class OpJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Serialize(Op op) => JsonSerializer.Serialize(op, Options);
    public static Op Deserialize(string json) => JsonSerializer.Deserialize<Op>(json, Options)!;
    public static byte[] SerializeEntry(JournalEntry e) => JsonSerializer.SerializeToUtf8Bytes(e, Options);
    public static JournalEntry DeserializeEntry(byte[] utf8) => JsonSerializer.Deserialize<JournalEntry>(utf8, Options)!;
}
