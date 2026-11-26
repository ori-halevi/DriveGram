using TL;

namespace DriveGram.Remote;

/// <summary>
/// Account-wide cap on Telegram requests in flight, adapted to flood waits (AIMD, like TCP).
/// Ported from InfiDrive's _UploadGovernor: left to the library, every in-flight request sleeps
/// out a FLOOD_WAIT and then all of them fire again at once (measured there: ~80% of upload
/// bandwidth wasted on rejected parts). Here the wait surfaces (FloodRetryThreshold = 0),
/// everyone holds until it passes, the cap halves once per episode and grows back by one per
/// cap's worth of clean requests. Each attempt also has a deadline, so a request that died
/// silently on a dead connection is retried instead of hanging forever (InfiDrive 6.3).
/// </summary>
public sealed class Governor
{
    readonly string _name;
    readonly int _cap;
    readonly object _gate = new();
    int _limit, _inflight, _ok;
    long _resumeAt;        // Environment.TickCount64
    long _episodeEnd;
    int _floods, _floodSeconds;
    long _reportAt;

    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(15);

    public Governor(string name, int cap)
    {
        _name = name;
        _cap = Math.Max(1, cap);
        _limit = _cap;
        _reportAt = Environment.TickCount64 + 60_000;
    }

    public int Limit { get { lock (_gate) return _limit; } }

    /// <summary>Raised when Telegram says the session is no longer valid (revoked from the phone, etc.).</summary>
    public event Action? SignedOut;

    /// <summary>Asked on a 401: true only if the account's MAIN connection confirms the sign-out.</summary>
    public Func<Task<bool>>? ConfirmSignedOut { get; set; }

    /// <summary>The account's connection is closed for good and cannot be asked anything any more:
    /// tell everyone holding it, so the drive stops instead of retrying into a dead client.</summary>
    public void DeclareSignedOut() => SignedOut?.Invoke();

    /// <summary>When Telegram asked this account to wait, until when (null: no wait).</summary>
    public DateTime? WaitingUntil
    {
        get
        {
            long left;
            lock (_gate) left = _resumeAt - Environment.TickCount64;
            return left > 5000 ? DateTime.Now.AddMilliseconds(left) : null;
        }
    }

    async Task AcquireAsync(CancellationToken ct)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_inflight < _limit) { _inflight++; return; }
            }
            await Task.Delay(15, ct);
        }
    }

    void Release() { lock (_gate) _inflight--; }

    public async Task<T> RunAsync<T>(Func<Task<T>> call, CancellationToken ct, TimeSpan? timeout = null)
    {
        int timeouts = 0;
        while (true)
        {
            await AcquireAsync(ct);
            try
            {
                long pause;
                lock (_gate) pause = _resumeAt - Environment.TickCount64;
                if (pause > 0) await Task.Delay(TimeSpan.FromMilliseconds(pause), ct);

                var task = call();
                if (timeout is { } t)
                {
                    var finished = await Task.WhenAny(task, Task.Delay(t, ct));
                    if (finished != task)
                    {
                        _ = task.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);
                        ct.ThrowIfCancellationRequested();
                        OnTimeout(); // a slow line: fewer requests at once, so each one finishes in time
                        if (++timeouts >= 5) throw new TimeoutException($"{_name}: request timed out {timeouts} times");
                        Log.Warn($"{_name}: request timed out after {t.TotalSeconds:0}s, retrying");
                        continue;
                    }
                }
                var result = await task;
                OnOk();
                return result;
            }
            catch (RpcException e) when (e.Code == 401)
            {
                if (ConfirmSignedOut is { } confirm && !await confirm())
                    throw new IOException($"Telegram refused a secondary connection ({e.Message}); retrying");
                SignedOut?.Invoke();
                throw new NotSignedInException();
            }
            catch (RpcException e) when (e.Code == 420)
            {
                int seconds = Math.Max(1, e.X);
                // even a very long wait is honoured by everyone on the account: hammering on only
                // extends the ban. Waiting requests stop on unmount (ct).
                if (seconds > MaxWait.TotalSeconds)
                    Log.Warn($"telegram {_name}: asked to wait {TimeSpan.FromSeconds(seconds):h\\:mm\\:ss}; pausing this account until then");
                OnFlood(seconds);
            }
            finally { Release(); }
        }
    }

    void OnOk()
    {
        lock (_gate)
        {
            if (_limit < _cap && ++_ok >= _limit) { _limit++; _ok = 0; }
            Report();
        }
    }

    long _timeoutEpisodeEnd;

    void OnTimeout()
    {
        lock (_gate)
        {
            long now = Environment.TickCount64;
            if (now < _timeoutEpisodeEnd) return; // once per episode, like flood waits
            _limit = Math.Max(1, _limit / 2);
            _ok = 0;
            _timeoutEpisodeEnd = now + 30_000;
            Log.Warn($"telegram {_name}: requests are timing out (slow connection?); in-flight cap now {_limit}/{_cap}");
        }
    }

    void OnFlood(int seconds)
    {
        lock (_gate)
        {
            long now = Environment.TickCount64;
            _resumeAt = Math.Max(_resumeAt, now + seconds * 1000L);
            if (now >= _episodeEnd)
            {
                _limit = Math.Max(1, _limit / 2);
                _ok = 0;
                _episodeEnd = _resumeAt;
            }
            _floods++;
            _floodSeconds += seconds;
            Report();
        }
    }

    void Report()
    {
        long now = Environment.TickCount64;
        if (now < _reportAt) return;
        _reportAt = now + 60_000;
        if (_floods > 0)
            Log.Warn($"telegram {_name} rate-limited: {_floods} waits ({_floodSeconds}s) in the last minute; in-flight cap now {_limit}/{_cap}");
        _floods = 0;
        _floodSeconds = 0;
    }
}
