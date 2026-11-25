using DriveGram.Engine;
using DriveGram.Host;

namespace DriveGram.Tray;

/// <summary>
/// Files being prepared on this PC (downloaded whole ahead of time, e.g. a video for tonight), from the
/// transfers window or Explorer's right-click menu. Says when each one is ready, or why it failed.
/// </summary>
sealed class Preparer
{
    public sealed class Job(DriveSession session, string id, string name)
    {
        public DriveSession Session { get; } = session;
        public string Id { get; } = id;
        public string Name { get; } = name;
        public double Progress;
        public readonly CancellationTokenSource Cts = new();
    }

    readonly List<Job> _jobs = [];
    readonly SynchronizationContext _ui;

    /// <summary>On the UI thread: a file is ready (true) or failed (false, with the reason).</summary>
    public event Action<Job, bool, string?>? Finished;

    public Preparer(SynchronizationContext ui) { _ui = ui; }

    public List<Job> Running { get { lock (_jobs) return _jobs.ToList(); } }

    public bool IsPreparing(DriveSession s, string id)
    {
        lock (_jobs) return _jobs.Any(j => j.Session == s && j.Id == id);
    }

    public double? ProgressOf(DriveSession s, string id)
    {
        lock (_jobs) return _jobs.FirstOrDefault(j => j.Session == s && j.Id == id)?.Progress;
    }

    public void Start(DriveSession s, string id)
    {
        var name = s.Db.Get(id)?.Name ?? "?";
        var job = new Job(s, id, name);
        lock (_jobs)
        {
            if (_jobs.Any(j => j.Session == s && j.Id == id)) return;
            _jobs.Add(job);
        }
        Log.Info($"preparing {name} on this PC");
        _ = Task.Run(async () =>
        {
            string? error = null;
            try
            {
                await s.Engine.PrepareAsync(id, new Progress<double>(p => job.Progress = p), job.Cts.Token);
                job.Progress = s.Engine.LocalFraction(id);
            }
            catch (OperationCanceledException) { error = ""; }
            catch (NotEnoughSpaceException ex)
            {
                error = L.T($"אין מספיק מקום במחשב הזה: צריך עוד {Ui.Size(ex.Needed)}, ופנויים {Ui.Size(ex.Free)} (DriveGram תמיד משאיר עוד 3 GB פנויים). פנה מקום במחשב ונסה שוב.",
                    $"Not enough space on this PC: {Ui.Size(ex.Needed)} more is needed, and {Ui.Size(ex.Free)} is free (DriveGram always keeps another 3 GB free). Free up space on this PC and try again.");
            }
            catch (Exception ex)
            {
                Log.Warn($"preparing {name}: {ex.Message}");
                error = Setup.TelegramErrors.Text(ex);
            }
            lock (_jobs) _jobs.Remove(job);
            _ui.Post(_ => Finished?.Invoke(job, error is null, error), null);
        });
    }

    public void Cancel(DriveSession s, string id)
    {
        lock (_jobs) foreach (var j in _jobs.Where(j => j.Session == s && j.Id == id)) j.Cts.Cancel();
    }

    /// <summary>A drive disconnects: its jobs stop (they can be started again later).</summary>
    public void CancelAll(DriveSession s)
    {
        lock (_jobs) foreach (var j in _jobs.Where(j => j.Session == s)) j.Cts.Cancel();
    }
}
