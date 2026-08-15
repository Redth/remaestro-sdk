namespace Remaestro.Sdk;

/// <summary>
/// Keeps a device connection alive for the life of the device. Runs a <c>session</c> delegate, and when it
/// returns or throws, runs it again — after a short idle delay on a clean return, or an exponentially
/// backing-off delay (with jitter) after a failure. This is the one place every driver's
/// connect → listen → reconnect logic lives, so adding a push listener to a driver is a few lines.
/// <para>
/// It serves two shapes with the same primitive:
/// <list type="bullet">
/// <item><b>Persistent listener</b> — <c>session</c> connects and blocks reading the push channel until it
/// drops; a clean return means the peer closed, so we reconnect after <see cref="_idle"/>.</item>
/// <item><b>Poller</b> — <c>session</c> does one poll and returns; <see cref="_idle"/> is the poll interval,
/// and a throw backs off. A succeeding poll counts as "connected", a failing one as "disconnected".</item>
/// </list>
/// </para>
/// </summary>
public sealed class ListenerSupervisor : IAsyncDisposable
{
    readonly Func<CancellationToken, Task> _session;
    readonly TimeSpan _idle, _retryMin, _retryMax;
    readonly Action<bool>? _onConnectedChanged;
    readonly Action<Exception>? _onError;
    readonly CancellationTokenSource _cts;
    Task? _loop;
    bool? _connected;

    /// <param name="session">
    /// Connect and do the work. For a listener, block until the connection drops; for a poller, do one pass.
    /// Honour the cancellation token — it fires when the device is disposed.
    /// </param>
    /// <param name="idle">Delay after a clean session before running again (the poll interval, or a brief reconnect gap).</param>
    /// <param name="retryMin">First delay after a failure; doubles up to <paramref name="retryMax"/>.</param>
    /// <param name="retryMax">Ceiling for the backoff delay.</param>
    /// <param name="onConnectedChanged">Called when connectivity flips — wire it to <c>online</c> state + a connection event.</param>
    /// <param name="onError">Called with each failure (for logging); optional.</param>
    /// <param name="linkedToken">Cancelled alongside the supervisor's own token (e.g. a device-wide CTS).</param>
    public ListenerSupervisor(
        Func<CancellationToken, Task> session,
        TimeSpan idle,
        TimeSpan? retryMin = null,
        TimeSpan? retryMax = null,
        Action<bool>? onConnectedChanged = null,
        Action<Exception>? onError = null,
        CancellationToken linkedToken = default)
    {
        _session = session;
        _idle = idle;
        _retryMin = retryMin ?? TimeSpan.FromSeconds(2);
        _retryMax = retryMax ?? TimeSpan.FromSeconds(30);
        _onConnectedChanged = onConnectedChanged;
        _onError = onError;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken);
    }

    /// <summary>Begin supervising. Idempotent — a second call is a no-op.</summary>
    public ListenerSupervisor Start()
    {
        _loop ??= Task.Run(() => RunAsync(_cts.Token));
        return this;
    }

    async Task RunAsync(CancellationToken ct)
    {
        var backoff = _retryMin;
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                await _session(ct);
                SetConnected(true);
                backoff = _retryMin;   // a good run resets the backoff
                wait = _idle;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetConnected(false);
                try { _onError?.Invoke(ex); } catch { /* logging must never break the loop */ }
                wait = Jitter(backoff);
                backoff = Min(backoff * 2, _retryMax);
            }

            try { await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    void SetConnected(bool value)
    {
        if (_connected == value) return;
        _connected = value;
        try { _onConnectedChanged?.Invoke(value); } catch { /* never break the loop */ }
    }

    // ±20% jitter so a fleet of devices reconnecting after an outage doesn't thunder in lockstep.
    static TimeSpan Jitter(TimeSpan d) =>
        d * (0.8 + Random.Shared.NextDouble() * 0.4);

    static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    public async ValueTask DisposeAsync()
    {
        try { await _cts.CancelAsync(); } catch { }
        if (_loop is not null)
        {
            try { await _loop; } catch { /* already unwinding */ }
        }
        _cts.Dispose();
    }
}
