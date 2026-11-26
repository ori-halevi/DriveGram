using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DriveGram.Host;

namespace DriveGram.Tray;

/// <summary>A newer DriveGram published on GitHub.</summary>
sealed record UpdateInfo(Version Version, string Tag, string Notes, string PageUrl, string? SetupUrl, string? SetupName, string? Sha256);

/// <summary>
/// Is there a newer DriveGram? Asks GitHub's public releases page of the project once a day (and a minute
/// after the app starts). It sends nothing about the user: a plain request with a generic program name.
/// Only published releases count (not drafts or pre-releases). Offline or failing: tried again next time.
/// </summary>
sealed class Updates
{
    public const string Repo = "ori-halevi/DriveGram";

    // (before Http: static fields start in the order they are written, and the client uses this one)
    public static Version Current { get; } = typeof(Updates).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);
    static readonly HttpClient Http = MakeClient();

    /// <summary>The newest release when it is newer than this program (null: none, or not known yet).</summary>
    public UpdateInfo? Available { get; private set; }

    /// <summary>A newer release was found (or the one known changed).</summary>
    public event Action? Changed;

    long _lastAt = Environment.TickCount64 - 23 * 3600_000L - 23 * 60_000L; // the first look a minute after start
    bool _checking;

    static HttpClient MakeClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DriveGram", Current.ToString()));
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return h;
    }

    // what the user said about versions: skipped one, and the version the one-time notice was shown for
    sealed class Seen { public string? Skipped { get; set; } public string? Told { get; set; } }
    static string SeenPath => Path.Combine(AppState.Root, "updates.json");
    Seen _seen = LoadSeen();

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

    /// <summary>Called by the tray's timer: looks once a day.</summary>
    public void Tick()
    {
        if (_checking || Environment.TickCount64 - _lastAt < 24 * 3600_000L) return;
        _ = CheckAsync();
    }

    public async Task CheckAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            _lastAt = Environment.TickCount64;
            using var res = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            if (!res.IsSuccessStatusCode) { Log.Debug($"updates: GitHub answered {(int)res.StatusCode}"); return; }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var r = doc.RootElement;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return;
            version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
            if (version <= Current) { if (Available is not null) { Available = null; Changed?.Invoke(); } return; }
            string? url = null, name = null;
            if (r.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                {
                    var n = a.GetProperty("name").GetString() ?? "";
                    if (n.StartsWith("DriveGram-Setup-", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    { url = a.GetProperty("browser_download_url").GetString(); name = n; break; }
                }
            var notes = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var sha = Regex.Match(notes, @"\b[0-9a-fA-F]{64}\b") is { Success: true } m ? m.Value.ToLowerInvariant() : null;
            var info = new UpdateInfo(version, tag, notes, r.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases", url, name, sha);
            if (Available?.Version != info.Version) { Available = info; Log.Info($"updates: DriveGram {version} is available (this is {Current})"); Changed?.Invoke(); }
        }
        catch (Exception ex) { Log.Debug($"updates: {ex.Message}"); }
        finally { _checking = false; }
    }

    /// <summary>Downloads the installer into the temp folder; checks its SHA-256 when the release states it.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo u, IProgress<double> progress, CancellationToken ct)
    {
        if (u.SetupUrl is not { } url || u.SetupName is not { } name) throw new InvalidOperationException("no installer in the release");
        if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("unexpected download address");
        var dir = Path.Combine(Path.GetTempPath(), "DriveGram-update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Path.GetFileName(name));
        using var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        long total = res.Content.Headers.ContentLength ?? 0, done = 0;
        using var sha = SHA256.Create();
        await using (var src = await res.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(path))
        {
            var buf = new byte[1 << 16];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                sha.TransformBlock(buf, 0, n, null, 0);
                done += n;
                if (total > 0) progress.Report((double)done / total);
            }
        }
        sha.TransformFinalBlock([], 0, 0);
        var got = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        if (u.Sha256 is { } want && got != want)
        {
            try { File.Delete(path); } catch { }
            throw new InvalidDataException("the downloaded installer does not match the release (SHA-256)");
        }
        return path;
    }
}
