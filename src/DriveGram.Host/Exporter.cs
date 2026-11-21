namespace DriveGram.Host;

/// <summary>Progress of an export: files and bytes copied so far, and the file being copied.</summary>
public sealed record ExportProgress(int Files, int Skipped, long Bytes, string Current);

/// <summary>
/// "Take everything out": copies a mounted drive's whole tree to a normal folder (another disk, a USB
/// drive). Reads through the drive itself, so it is exactly what Explorer shows. Re-running it resumes:
/// a file already there with the same size and time is skipped. Errors on single files are collected,
/// not fatal, so one unreadable file never stops the rest.
/// </summary>
public static class Exporter
{
    public static async Task<List<string>> CopyAsync(string sourceRoot, string target, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var errors = new List<string>();
        int files = 0, skipped = 0;
        long bytes = 0;
        Directory.CreateDirectory(target);
        var stack = new Stack<(string Src, string Dst)>();
        stack.Push((sourceRoot, target));
        var buffer = new byte[4 << 20];
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (src, dst) = stack.Pop();
            Directory.CreateDirectory(dst);
            IEnumerable<string> dirs, entries;
            try
            {
                dirs = Directory.EnumerateDirectories(src).ToList();
                entries = Directory.EnumerateFiles(src).ToList();
            }
            catch (Exception ex) { errors.Add($"{src}: {ex.Message}"); continue; }
            foreach (var d in dirs) stack.Push((d, Path.Combine(dst, Path.GetFileName(d))));
            foreach (var f in entries)
            {
                ct.ThrowIfCancellationRequested();
                var to = Path.Combine(dst, Path.GetFileName(f));
                try
                {
                    var info = new FileInfo(f);
                    progress?.Report(new ExportProgress(files, skipped, bytes, f));
                    if (File.Exists(to) && new FileInfo(to) is { } existing
                        && existing.Length == info.Length && existing.LastWriteTimeUtc == info.LastWriteTimeUtc)
                    {
                        skipped++;
                        continue;
                    }
                    var tmp = to + ".drivegram-part";
                    await using (var input = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, useAsync: true))
                    await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                    {
                        int n;
                        while ((n = await input.ReadAsync(buffer, ct)) > 0)
                        {
                            await output.WriteAsync(buffer.AsMemory(0, n), ct);
                            bytes += n;
                            progress?.Report(new ExportProgress(files, skipped, bytes, f));
                        }
                    }
                    File.Move(tmp, to, overwrite: true);
                    File.SetLastWriteTimeUtc(to, info.LastWriteTimeUtc);
                    File.SetCreationTimeUtc(to, info.CreationTimeUtc);
                    files++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add($"{f}: {ex.Message}"); }
            }
        }
        progress?.Report(new ExportProgress(files, skipped, bytes, ""));
        return errors;
    }
}
