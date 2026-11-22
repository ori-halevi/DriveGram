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
    /// <summary>The target holds other files: the caller asks the user, then calls again with allowNonEmpty.</summary>
    public sealed class TargetNotEmptyException() : IOException("the folder is not empty");

    public static async Task<List<string>> CopyAsync(string sourceRoot, string target, IProgress<ExportProgress>? progress, CancellationToken ct,
        bool allowNonEmpty = false)
    {
        var errors = new List<string>();
        int files = 0, skipped = 0;
        long bytes = 0;
        Directory.CreateDirectory(target);
        // only into an empty folder, or one a previous export made (a marker says so): never replace a
        // folder of the user's own, newer files with the drive's versions
        var marker = Path.Combine(target, ".drivegram-export");
        if (!File.Exists(marker))
        {
            // Windows' own hidden items (a USB stick's root has them) do not count
            bool other = new DirectoryInfo(target).EnumerateFileSystemInfos().Any(e =>
                !e.Attributes.HasFlag(FileAttributes.Hidden | FileAttributes.System)
                && e.Name is not ("$RECYCLE.BIN" or "System Volume Information" or "desktop.ini"));
            if (other && !allowNonEmpty) throw new TargetNotEmptyException();
            File.WriteAllText(marker, "DriveGram export\r\n");
            File.SetAttributes(marker, FileAttributes.Hidden);
        }
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
                    long length = info.Length;                       // read BEFORE copying
                    var mtime = info.LastWriteTimeUtc;
                    var ctime = info.CreationTimeUtc;
                    progress?.Report(new ExportProgress(files, skipped, bytes, f));
                    // FAT32 keeps times in 2 s steps, exFAT in 10 ms: compare with a small tolerance
                    if (File.Exists(to) && new FileInfo(to) is { } existing
                        && existing.Length == length && Math.Abs((existing.LastWriteTimeUtc - mtime).TotalSeconds) <= 2)
                    {
                        skipped++;
                        continue;
                    }
                    var tmp = $"{to}.{Guid.NewGuid():N}.dgpart";
                    long copied = 0;
                    try
                    {
                    await using (var input = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, useAsync: true))
                    await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                    {
                        int n;
                        while ((n = await input.ReadAsync(buffer, ct)) > 0)
                        {
                            await output.WriteAsync(buffer.AsMemory(0, n), ct);
                            bytes += n;
                            copied += n;
                            progress?.Report(new ExportProgress(files, skipped, bytes, f));
                        }
                    }
                    info.Refresh();
                    if (copied != length || info.Length != length || info.LastWriteTimeUtc != mtime)
                    {
                        File.Delete(tmp);
                        throw new IOException("the file changed while it was being copied; run the export again");
                    }
                    File.Move(tmp, to, overwrite: true);
                    }
                    catch
                    {
                        try { File.Delete(tmp); } catch { }
                        throw;
                    }
                    File.SetLastWriteTimeUtc(to, mtime);
                    File.SetCreationTimeUtc(to, ctime);
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
