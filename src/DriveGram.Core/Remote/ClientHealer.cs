using System.Net.NetworkInformation;
using TL;

namespace DriveGram.Remote;

/// <summary>
/// Keeps a WTelegram client alive across Wi-Fi drops and sleep. Left alone, the library reconnects once
/// after a drop; if that one attempt fails (network still down), the client stays dead for good: every
/// later request waits forever. It also gives up for good on its 5th drop (MaxAutoReconnects). Here a
/// dead client is repaired IN PLACE (reset + connect, with back-off), so everything holding it — every
/// drive on the account — simply works again.
/// </summary>
public sealed class ClientHealer : IDisposable
{
    readonly WTelegram.Client _client;
    readonly string _name;
    readonly CancellationTokenSource _cts = new();
    int _healing, _strikes;

    public ClientHealer(WTelegram.Client client, string name)
    {
        _client = client;
        _name = name;
        client.MaxAutoReconnects = int.MaxValue; // the library's own retries never "run out"
        client.OnOther += OnOther;
        NetworkChange.NetworkAvailabilityChanged += OnNetwork;
        _ = WatchAsync(_cts.Token);
    }

    Task OnOther(IObject o)
    {
        // raised on the main connection only when the library's own reconnect failed
        if (o is ReactorError e) Kick($"connection lost ({e.Exception?.Message})");
        return Task.CompletedTask;
    }

    void OnNetwork(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) _ = CheckSoonAsync();
    }

    async Task CheckSoonAsync()
    {
        try { await Task.Delay(3000, _cts.Token); } catch (OperationCanceledException) { return; }
        if (_client.UserId != 0 && _client.Disconnected) Kick("network is back");
    }

    /// <summary>The PC woke up, or the caller saw the connection fail: check now.</summary>
    public void Poke() => _ = CheckSoonAsync();

    async Task WatchAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(20_000, ct); } catch (OperationCanceledException) { return; }
            // twice in a row: the library's own quick reconnect (5 s) gets its chance first
            if (_client.UserId != 0 && _client.Disconnected) { if (++_strikes >= 2) Kick("connection is down"); }
            else _strikes = 0;
        }
    }

    void Kick(string why)
    {
        if (_cts.IsCancellationRequested || Interlocked.Exchange(ref _healing, 1) == 1) return;
        _ = Task.Run(() => HealAsync(why));
    }

    async Task HealAsync(string why)
    {
        var ct = _cts.Token;
        var delay = TimeSpan.FromSeconds(2);
        try
        {
            Log.Warn($"telegram ({_name}): {why}; reconnecting");
            for (int attempt = 1; !ct.IsCancellationRequested; attempt++)
            {
                try
                {
                    await _client.ResetAsync(resetUser: false, resetSessions: false);
                    await _client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(30), ct);
                    _strikes = 0;
                    Log.Info($"telegram ({_name}): reconnected (attempt {attempt})");
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (ObjectDisposedException) { return; }
                catch (Exception ex)
                {
                    if (attempt == 1 || attempt % 10 == 0) Log.Warn($"telegram ({_name}): reconnect failed ({ex.Message}); retrying");
                    try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
                    delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
                }
            }
        }
        finally { Interlocked.Exchange(ref _healing, 0); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        NetworkChange.NetworkAvailabilityChanged -= OnNetwork;
        _client.OnOther -= OnOther;
    }
}
