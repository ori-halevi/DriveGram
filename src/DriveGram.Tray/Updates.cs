using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DriveGram.Host;

namespace DriveGram.Tray;

/// <summary>A newer DriveGram published on GitHub. Sha256: what GitHub itself measured of the installer
/// (its "digest"), or the line publish-release.cmd writes into the notes; null: it cannot be checked, so it
/// is not installed from here (only the download page is offered).</summary>
sealed record UpdateInfo(Version Version, string Tag, string Notes, string PageUrl, string? SetupUrl, string? SetupName, string? Sha256);

/// <summary>
/// Is there a newer DriveGram? Asks GitHub's public releases page of the project once a day (and a minute
/// after the app starts; half an hour after a failed look). It sends nothing about the user: a plain
/// request named "DriveGram". Only published releases count (not drafts or pre-releases).
/// The installer is taken only from this project's own release downloads, and only when its SHA-256 is known.
///
/// TODO (when DriveGram has many users): sign releases with an offline key. The SHA-256 here only proves the
/// file is the one published on GitHub; whoever controls the GitHub account could publish another one.
/// Deferred on purpose by the owner (2026-11-26), together with turning on GitHub's "Immutable releases";
/// the plan and its one-way consequences are in CLAUDE.md, "Updates and releases".
/// </summary>
sealed class Updates
{
    public const string Repo = "ori-halevi/DriveGram";
    const long Day = 24 * 3600_000L;
    const long MaxSetup = 300L << 20; // an installer is ~40 MB: anything far bigger is not one

    // (before Http: static fields start in the order they are written)
    public static Version Current { get; } = typeof(Updates).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);
    static readonly HttpClient Http = MakeClient();

    /// <summary>The newest release when it is newer than this program (null: none, or not known yet).</summary>
    public UpdateInfo? Available { get; private set; }

    /// <summary>A newer release was found (or what is known about it changed).</summary>
    public event Action? Changed;

    long _nextAt = Environment.TickCount64 + 60_000; // the first look a minute after start
    volatile bool _checking, _lastFailed;

    /// <summary>The network came back, or the PC woke up: after a failed look, look again in a moment
    /// (not half an hour later).</summary>
    public void Nudge()
    {
        if (_lastFailed) Interlocked.Exchange(ref _nextAt, Environment.TickCount64 + 15_000);
    }

    static HttpClient MakeClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(60), MaxResponseContentBufferSize = 4 << 20 }; // the API's answer (downloads stream)
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DriveGram", null));
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return h;
    }

    // what the user said about versions: skipped one, and the version the one-time notice was shown for
    sealed class Seen { public string? Skipped { get; set; } public string? Told { get; set; } }
    static string SeenPath => Path.Combine(AppState.Root, "updates.json");
    readonly Seen _seen = LoadSeen();

    static Seen LoadSeen()
    {
        try { if (File.Exists(SeenPath)) return JsonSerializer.Deserialize<Seen>(File.ReadAllText(SeenPath)) ?? new(); }
        catch (Exception ex) { Log.Debug($"updates: {ex.Message}"); }
        return new();
    }

    void SaveSeen()
    {
        try { File.WriteAllText(SeenPath, JsonSerializer.Serialize(_seen)); }
        catch (Exception ex) { Log.Debug($"updates: {ex.Message}"); }
    }

    /// <summary>The release to offer: newer than this program and not skipped by the user.</summary>
    public UpdateInfo? Offered => Available is { } u && u.Version.ToString() != _seen.Skipped ? u : null;

    public void Skip(UpdateInfo u) { _seen.Skipped = u.Version.ToString(); SaveSeen(); Changed?.Invoke(); }

    /// <summary>True once per version: the one-time notice may be shown.</summary>
    public bool FirstTimeFor(UpdateInfo u)
    {
        if (_seen.Told == u.Version.ToString()) return false;
        _seen.Told = u.Version.ToString();
        SaveSeen();
        return true;
    }

    /// <summary>Called by the tray's timer (UI thread): the look itself runs in the background.</summary>
    public void Tick()
    {
        if (_checking || Environment.TickCount64 < Interlocked.Read(ref _nextAt)) return;
        _checking = true;
        _ = Task.Run(() => CheckCoreAsync());
    }

    /// <summary>"Check for a new version" from the menu: now, and a version skipped before is offered again.
    /// Returns false when GitHub could not be reached.</summary>
    public async Task<bool> CheckNowAsync()
    {
        if (_seen.Skipped is not null) { _seen.Skipped = null; SaveSeen(); Changed?.Invoke(); } // offered again (bar, menu)
        while (_checking) await Task.Delay(200);
        _checking = true;
        return await Task.Run(() => CheckCoreAsync());
    }

    async Task<bool> CheckCoreAsync()
    {
        bool ok = false;
        try
        {
            using var res = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            if (!res.IsSuccessStatusCode) { Log.Debug($"updates: GitHub answered {(int)res.StatusCode}"); return false; }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var r = doc.RootElement;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            ok = true;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return true;
            version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
            UpdateInfo? info = null;
            if (version > Current)
            {
                var notes = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                string? url = null, name = null, sha = null;
                var ownDownloads = $"https://github.com/{Repo}/releases/download/";
                if (r.TryGetProperty("assets", out var assets))
                    foreach (var a in assets.EnumerateArray())
                    {
                        var n = a.GetProperty("name").GetString() ?? "";
                        if (!Regex.IsMatch(n, @"^DriveGram-Setup-[0-9.]+\.exe$", RegexOptions.IgnoreCase)) continue;
                        // the address built from known parts (not taken as given): this project's own release download
                        (url, name) = ($"{ownDownloads}{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(n)}", n);
                        // GitHub's own measurement of the uploaded file (cannot be changed by editing the notes)
                        if (a.TryGetProperty("digest", out var dg) && dg.GetString() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                            sha = d[7..].ToLowerInvariant();
                        break;
                    }
                // otherwise the line publish-release.cmd writes, for exactly this file
                if (sha is null && name is not null
                    && Regex.Match(notes, @"SHA256 \(" + Regex.Escape(name) + @"\):\s*([0-9a-fA-F]{64})") is { Success: true } m)
                    sha = m.Groups[1].Value.ToLowerInvariant();
                info = new UpdateInfo(version, tag, notes, $"https://github.com/{Repo}/releases/tag/{Uri.EscapeDataString(tag)}", url, name, sha);
            }
            if (info != Available)
            {
                Available = info;
                if (info is not null) Log.Info($"updates: DriveGram {info.Version} is available (this is {Current})");
                Changed?.Invoke();
            }
            return true;
        }
        catch (Exception ex) { Log.Debug($"updates: {ex.Message}"); return false; }
        finally
        {
            // looked: again tomorrow; failed (offline, asleep, GitHub busy): again in half an hour
            Interlocked.Exchange(ref _nextAt, Environment.TickCount64 + (ok ? Day : 30 * 60_000L));
            _lastFailed = !ok;
            _checking = false;
        }
    }

    /// <summary>The downloaded installer, held open (others may only read it) until it is started, so it
    /// cannot be swapped between the check and the start.</summary>
    public sealed class Download(string path, FileStream hold) : IDisposable
    {
        public string Path { get; } = path;
        public void Dispose() => hold.Dispose();
    }

    public sealed class MismatchException() : IOException("the downloaded installer does not match the release (SHA-256)");

    /// <summary>Downloads the installer (a new name each time, earlier ones removed) and checks its SHA-256
    /// on disk.</summary>
    public static async Task<Download> DownloadAsync(UpdateInfo u, IProgress<double?> progress, CancellationToken ct)
    {
        if (u.SetupUrl is not { } url || u.SetupName is not { } name || u.Sha256 is not { } want)
            throw new InvalidOperationException("no checkable installer in the release");
        if (!url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("unexpected download address");
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DriveGram-update");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.EnumerateFiles(dir)) try { File.Delete(old); } catch { } // earlier downloads
        var path = System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(name) + "-" + Guid.NewGuid().ToString("N")[..8] + ".exe");

        // a download that stopped moving does not wait forever (2 minutes without a byte); a slow one may go on
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(TimeSpan.FromMinutes(2));
        using (var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token))
        {
            res.EnsureSuccessStatusCode();
            long total = res.Content.Headers.ContentLength ?? 0, done = 0;
            if (total > MaxSetup) throw new InvalidDataException("the download is far too big for an installer");
            await using var src = await res.Content.ReadAsStreamAsync(stall.Token);
            await using var dst = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buf = new byte[1 << 16];
            int n;
            progress.Report(total > 0 ? 0 : null);
            while ((n = await src.ReadAsync(buf, stall.Token)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), stall.Token);
                stall.CancelAfter(TimeSpan.FromMinutes(2));
                done += n;
                if (done > MaxSetup) throw new InvalidDataException("the download is far too big for an installer");
                progress.Report(total > 0 ? (double)done / total : null);
            }
        }
        // checked on disk, and kept open (read-only sharing) until it is started
        var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var got = Convert.ToHexString(await SHA256.HashDataAsync(hold, ct)).ToLowerInvariant();
            if (got != want) throw new MismatchException();
            return new Download(path, hold);
        }
        catch
        {
            hold.Dispose();
            try { File.Delete(path); } catch { }
            throw;
        }
    }
}
