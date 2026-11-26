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
    int _healing, _strikes, _dead;
    readonly SemaphoreSlim _wake = new(0);
    Task _heal = Task.CompletedTask;

    /// <summary>
    /// The connection cannot be repaired any more: the library closed the client for good (it does that
    /// on a fatal error such as a revoked sign-in), so reset + connect "succeed" while every request
    /// fails. Nothing can be healed in place from here — whoever holds the client must be rebuilt.
    /// Raised at most once, off the healing thread.
    /// </summary>
    public event Action<string>? Dead;

    void Die(string why)
    {
        if (_cts.IsCancellationRequested) return; // the drive is closing: this client is meant to go
        if (Interlocked.Exchange(ref _dead, 1) == 1) return;
        Log.Error($"telegram ({_name}): the connection is closed for good ({why}); it cannot be repaired here");
        if (Dead is { } handler) Task.Run(() => handler(why));
    }

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
        if (!e.IsAvailable) return;
        if (_healing == 1) { if (_wake.CurrentCount == 0) _wake.Release(); } // a heal is waiting out its back-off: try now
        else _ = CheckSoonAsync();
    }

    async Task CheckSoonAsync()
    {
        try { await Task.Delay(3000, _cts.Token); } catch (OperationCanceledException) { return; }
        if (_client.UserId != 0 && _client.Disconnected) Kick("network is back");
    }

    /// <summary>The PC woke up: check now. After sleep the socket often still LOOKS connected while it is
    /// dead, so ask the server something small and reconnect if it does not answer.</summary>
    public void Poke()
    {
        if (_healing == 1) { if (_wake.CurrentCount == 0) _wake.Release(); return; }
        _ = ProbeAsync();
    }

    async Task ProbeAsync()
    {
        try { await Task.Delay(3000, _cts.Token); } catch (OperationCanceledException) { return; }
        if (_client.UserId == 0) return;
        if (_client.Disconnected) { Kick("the PC woke up"); return; }
        try { await TL.SchemaExtensions.Updates_GetState(_client).WaitAsync(TimeSpan.FromSeconds(15), _cts.Token); }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (ObjectDisposedException) { Die("it was closed while the PC slept"); }
        catch (TimeoutException) { Kick("no answer after the PC woke up"); }
        catch (Exception) { } // an answer, even an error, means the connection is alive
    }

    async Task WatchAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(20_000, ct); } catch (OperationCanceledException) { return; }
            // twice in a row: the library's own quick reconnect (5 s) gets its chance first
            if (_dead == 1) return; // closed for good: whoever holds it was already told
            try
            {
                if (_client.UserId != 0 && _client.Disconnected) { if (++_strikes >= 2) Kick("connection is down"); }
                else _strikes = 0;
            }
            catch (ObjectDisposedException) { Die("it was closed under us"); return; }
        }
    }

    void Kick(string why)
    {
        if (_cts.IsCancellationRequested || _dead == 1) return;
        if (Interlocked.Exchange(ref _healing, 1) == 1) return;
        _heal = Task.Run(() => HealAsync(why));
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
                    await VerifyAsync(ct);
                    _strikes = 0;
                    Log.Info($"telegram ({_name}): reconnected (attempt {attempt})");
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (ObjectDisposedException) { Die("the Telegram library closed it"); return; }
                catch (Exception ex)
                {
                    if (attempt == 1 || attempt % 10 == 0) Log.Warn($"telegram ({_name}): reconnect failed ({ex.Message}); retrying");
                    try { await Task.WhenAny(Task.Delay(delay, ct), _wake.WaitAsync(ct)); }
                    catch (OperationCanceledException) { return; }
                    if (ct.IsCancellationRequested) return;
                    delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
                }
            }
        }
        finally { Interlocked.Exchange(ref _healing, 0); }
    }

    /// <summary>
    /// A reconnect only counts once the server answers a real request. On a client the library has
    /// closed, reset + connect raise nothing at all, and reporting "reconnected" every minute hid a
    /// drive that could not do a thing for hours. An error answer still counts: the line is alive.
    /// </summary>
    async Task VerifyAsync(CancellationToken ct)
    {
        try { await TL.SchemaExtensions.Updates_GetState(_client).WaitAsync(TimeSpan.FromSeconds(20), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ObjectDisposedException) { throw; }
        catch (TimeoutException) { throw new IOException("no answer after reconnecting"); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _heal.Wait(TimeSpan.FromSeconds(3)); } catch { } // never dispose the client under a running reconnect
        NetworkChange.NetworkAvailabilityChanged -= OnNetwork;
        _client.OnOther -= OnOther;
    }
}
