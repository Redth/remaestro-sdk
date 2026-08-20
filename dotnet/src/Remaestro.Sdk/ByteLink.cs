using System.Text;

namespace Remaestro.Sdk;

/// <summary>
/// A byte pipe with a deadline, for the request/reply gear that doesn't fit <see cref="LineDevice"/>.
/// <para>
/// Some protocols aren't a stream of lines arriving whenever the device feels like it — a Zidoo or a BenQ
/// answers each command and is otherwise silent, so the driver writes, waits a moment for a reply, and gives
/// up. <c>SerialPort</c> offers exactly that shape with <c>ReadExisting</c> and a read timeout, which is why
/// those drivers used it directly and were therefore unable to talk to anything that wasn't a cable.
/// </para>
/// <para>
/// The reason this owns a background reader rather than just calling <c>ReadAsync</c> with a timeout token:
/// cancelling a pending socket read can leave the socket unusable, so a per-read timeout — the obvious
/// implementation — turns every quiet moment into a dead connection. Instead nothing ever cancels a read.
/// The pump blocks until bytes arrive or the stream is disposed, and the deadline is applied to <i>waiting
/// for the buffer to fill</i>, which is a purely local operation.
/// </para>
/// </summary>
public sealed class ByteLink : IDisposable
{
    readonly Stream _stream;
    readonly IDisposable _owner;
    readonly StringBuilder _pending = new();
    readonly Lock _gate = new();
    readonly SemaphoreSlim _arrived = new(0);

    volatile bool _closed;
    Exception? _fault;

    // For diagnostics only: whose conversation this is and over what, so the trace can name it.
    //
    // Empty when the driver didn't identify itself. It is still recorded — under a whole-process capture the
    // sink keeps it, and an unattributed record is worth far more than none. The guard that used to sit here
    // meant a driver which forgot to pass its id was invisible even to "capture everything", which is a
    // promise that then wasn't kept: the trace looked empty and read as "the device said nothing".
    readonly string _diagId;
    readonly string _diagKind;
    readonly string _diagWhere;

    ByteLink(Stream stream, IDisposable owner, Encoding encoding, string? diagId, string diagKind, string diagWhere)
    {
        _stream = stream;
        _owner = owner;
        Encoding = encoding;
        _diagId = diagId ?? "";
        _diagKind = diagKind;
        _diagWhere = diagWhere;

        _ = PumpAsync();
    }

    public Encoding Encoding { get; }

    /// <summary>
    /// Open one over any transport — a cable, or a socket that stands in for one.
    /// <para>
    /// Pass <paramref name="deviceId"/> to have the exchange recorded when the hub turns diagnostics on for
    /// that device — the driver gets a wire trace for free, same as a <see cref="LineDevice"/>. Omit it and
    /// the exchange is still recorded under a whole-process capture, just without a device to attribute it
    /// to; only the per-device switch can't reach it.
    /// </para>
    /// </summary>
    public static async Task<ByteLink> OpenAsync(LineTransport transport, CancellationToken ct,
        Encoding? encoding = null, string? deviceId = null)
    {
        var (stream, owner) = await transport.OpenAsync(ct);
        Diag.Open(deviceId ?? "", transport.Kind, transport.Describe);
        return new ByteLink(stream, owner, encoding ?? Encoding.ASCII, deviceId, transport.Kind, transport.Describe);
    }

    /// <summary>Whether the far end is still there, as far as anything local can tell.</summary>
    public bool Alive => !_closed;

    async Task PumpAsync()
    {
        var buffer = new byte[1024];

        try
        {
            while (true)
            {
                // Deliberately no cancellation token. Disposing the stream is what ends this, and that is
                // the one way to stop a read that leaves nothing half-torn-down.
                var reading = _stream.ReadAsync(buffer);

                // Asked before the await, because *whether this read has to wait* is the only local fact
                // that is about the device rather than about us — see the trace section below, which is
                // the whole reason it is asked at all.
                if (!reading.IsCompleted) TraceLineIdle();

                var read = await reading;
                if (read == 0) break;

                var chunk = Encoding.GetString(buffer, 0, read);
                lock (_gate) _pending.Append(chunk);

                TraceInbound(buffer.AsSpan(0, read));
                _arrived.Release();
            }
        }
        catch (Exception ex) { _fault = ex; FlushTrace(); Diag.Error(_diagId, _diagKind, ex.Message, _diagWhere); }
        finally
        {
            _closed = true;

            // Whatever was still gathering goes out before the close. A reply that never completed is
            // exactly the one worth seeing — losing it to the tidy-up would hide the failure being chased.
            FlushTrace();
            Diag.Close(_diagId, _diagKind);
            _arrived.Release();                               // wake anyone waiting, so they see the close
        }
    }

    // ---- Coalescing what arrives, for the trace only ------------------------------------------------
    //
    // A device hands its bytes over in whatever chunks the transport happened to read, so one eight-byte
    // reply arrived as four trace rows of two or three — `*po`, `w=o`, `n#` — and reading it meant
    // reassembling it by eye. Worse, it made a single answer look like four separate things the device
    // said, which is the wrong impression when the question is whether it answered at all.
    //
    // The gap is the signal: bytes of one message follow each other by microseconds, and the next message
    // is a device-turnaround later. So this gathers a burst and writes it once, when the line goes quiet.
    //
    // A message that never completes still appears — the timer fires on silence regardless of whether what
    // arrived was a whole reply, and a close or a fault flushes immediately. Nothing waits for a terminator
    // it may never get, which matters because the reply that never finished is the one being chased.
    //
    // ---- A late reader is not a quiet device -------------------------------------------------------
    //
    // This clock used to be started by a chunk being *processed*, so what it measured was the gap between
    // reads the pump got round to — a fact about this process — while the record it wrote read as a fact
    // about the device. `#334` demonstrated the difference rather than arguing it: both halves of one
    // reply handed to a fake before the link was opened, then the pump stalled 99ms between the two reads,
    // and the trace came back as `*po` and `w=on#` a hundred milliseconds apart. No device caused that
    // boundary. On a hub running thirty-nine drivers a 60ms stall is not exotic, and the symptom — one
    // reply drawn as two records — reads exactly like chatty gear, so nobody would ever report it.
    //
    // So the clock is started by a read that had to *wait*, and stopped by bytes arriving. "The line has
    // gone quiet" is a claim about the far end, and the only local evidence for it is that this link asked
    // for bytes and none were there. How long we then took to get round to them is our business and says
    // nothing about the device, which is why it no longer cuts a record in half.
    //
    // Measured, because the whole thing rests on it: over a loopback socket, every `ReadAsync` with bytes
    // already in the kernel buffer completes synchronously — `IsCompleted` true, four times out of four,
    // including after a deliberate 50ms delay — and the read that finds nothing does not. So a backlogged
    // reader draining a reply that is already there takes it in reads that never wait, and gets one record
    // however far behind it was.
    //
    // What this still cannot tell, stated plainly rather than papered over: a read that genuinely goes
    // pending and whose *completion* the pump is late to observe. Under thread-pool starvation the bytes
    // land, the read completes, and the continuation runs a hundred milliseconds later — and from inside
    // this process that is indistinguishable from a device that paused, because the earliest clock reading
    // available is the one taken when our own continuation runs. There is no arrival time to be had: a
    // Stream hands over bytes, not the moment they arrived, so timestamping "at the socket" and
    // timestamping "at the pump" are the same instant and neither moves that boundary. It is a real
    // residual and it is undecidable here; the fix above is for the part that is not.

    readonly Lock _traceGate = new();
    readonly List<byte> _traceBurst = [];
    Timer? _traceTimer;

    /// <summary>How long a quiet line means the message is over. Well under any device's turnaround.</summary>
    static readonly TimeSpan TraceGap = TimeSpan.FromMilliseconds(60);

    /// <summary>
    /// Flushed regardless at this size, so a device that streams isn't held indefinitely.
    /// <para>
    /// It carries more weight than it looks: a device talking faster than the pump drains it serves every
    /// read from bytes already buffered, so no read ever waits and the clock below never starts. This is
    /// what ends the burst then, and it is the only thing that does.
    /// </para>
    /// </summary>
    const int TraceBurstMax = 512;

    /// <summary>Bytes are here, so the line is not quiet — whatever we were doing before we noticed.</summary>
    void TraceInbound(ReadOnlySpan<byte> data)
    {
        // Asked before anything is buffered: gathering bytes nobody will read is the cost this is here to
        // avoid, and this runs on every read of every device.
        if (!Diag.Enabled(_diagId)) return;

        bool full;
        lock (_traceGate)
        {
            _traceBurst.AddRange(data);
            full = _traceBurst.Count >= TraceBurstMax;

            // Stopped rather than restarted. The burst ends when the *device* stops talking, and the next
            // read that has to wait is what says so; until one does, there is no reason to be counting.
            if (!full) _traceTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        if (full) FlushTrace();
    }

    /// <summary>
    /// A read found nothing waiting, which is the far end having stopped talking — start the clock.
    /// <para>
    /// Called from the pump before it awaits, and only when the read did not complete on the spot. Nothing
    /// gathered means nothing to cut, so an idle link costs one no-op rather than a timer per quiet moment.
    /// </para>
    /// </summary>
    void TraceLineIdle()
    {
        if (!Diag.Enabled(_diagId)) return;

        lock (_traceGate)
        {
            if (_traceBurst.Count == 0) return;
            _traceTimer ??= new Timer(_ => FlushTrace(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _traceTimer.Change(TraceGap, Timeout.InfiniteTimeSpan);
        }
    }

    void FlushTrace()
    {
        byte[] whole;
        lock (_traceGate)
        {
            if (_traceBurst.Count == 0) return;
            whole = [.. _traceBurst];
            _traceBurst.Clear();
        }

        Diag.RxBytes(_diagId, _diagKind, whole, Encoding.GetString(whole).Trim('\r', '\n'), _diagWhere);
    }

    /// <summary>Everything received and not yet drained. For a caller that wants a partial answer.</summary>
    public string Buffered
    {
        get { lock (_gate) return _pending.ToString(); }
    }

    /// <summary>Throw away anything already buffered, so a reply can't be confused with the last one's tail.</summary>
    public void Drain()
    {
        lock (_gate) _pending.Clear();
    }

    public async Task WriteAsync(byte[] data, CancellationToken ct = default)
    {
        if (_closed) throw _fault ?? new IOException("The connection has closed.");

        await _stream.WriteAsync(data, ct);
        await _stream.FlushAsync(ct);
        Diag.TxBytes(_diagId, _diagKind, data, Encoding.GetString(data).Trim('\r', '\n'), _diagWhere);
    }

    /// <summary>
    /// Wait until <paramref name="answered"/> finds what it's looking for, or the deadline passes.
    /// <para>
    /// The predicate sees everything received so far rather than one line at a time, because a reply can
    /// arrive split across reads — which on a cable is rare and over a network is routine.
    /// </para>
    /// <para>
    /// <b><paramref name="within"/> is a total, and deliberately still is.</b> It bounds one question and
    /// one answer over a link that is already open, which is a short enough span that "the device is being
    /// slow" and "the device is not going to answer" are not worth separating. Where that span covers
    /// several phases — a connect, a greeting, an auth round trip, a reply — a total is the wrong instrument
    /// and <see cref="IdleGap"/> below is the right one. The two sit in the same file so the difference is
    /// visible rather than a thing each driver has to rediscover.
    /// </para>
    /// </summary>
    public async Task<(bool Found, T Value)> AwaitReplyAsync<T>(Func<string, (bool Found, T Value)> answered,
        TimeSpan within, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + within;

        while (true)
        {
            string sofar;
            lock (_gate) sofar = _pending.ToString();

            if (answered(sofar) is { Found: true } found) return found;

            // A closed connection is reported as no-reply rather than thrown: to the caller "the device
            // never answered" and "the device went away mid-answer" want the same message.
            if (_closed) return (false, default!);

            var left = deadline - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero) return (false, default!);

            // Waits for the pump to signal rather than polling on a timer, so a fast device answers in the
            // time it takes to arrive rather than on the next tick.
            await _arrived.WaitAsync(left, ct);
        }
    }

    public void Dispose()
    {
        _closed = true;
        FlushTrace();
        lock (_traceGate) { _traceTimer?.Dispose(); _traceTimer = null; }
        try { _owner.Dispose(); } catch { /* already gone */ }
        _arrived.Release();
        _arrived.Dispose();
    }
}

/// <summary>
/// A deadline that anything the far end says puts back to the start — so <b>slowness is free and only
/// silence spends it</b>.
///
/// <para>
/// <b>The bug this exists to stop being written again.</b> A driver that talks to a television or a
/// projector spends most of its life telling "it hasn't answered yet" from "it is never going to answer",
/// and until now every one of them did that with a total: one <c>CancelAfter</c> wrapped round a connect, a
/// handshake and a reply. A total cannot tell those two apart, because it is spent identically by a device
/// working steadily through four questions and by a device that took the socket and then died. So the number
/// gets raised, which makes the real failure take longer to report and still does not buy the slow device
/// enough — and the phase that runs last is quietly left with whatever the earlier ones did not use.
/// </para>
///
/// <para>
/// A gap is spent only by silence. A device that is answering renews it on every byte, however slowly the
/// answers come; a device that has gone renews nothing. That is why the numbers here did not have to grow
/// when the totals were taken apart — see <c>docs/webos-capabilities.md</c> §7.2, which walks the whole
/// argument through on the driver it was first found in.
/// </para>
///
/// <para>
/// <b>A gap on its own is not enough, and this deliberately does not pretend otherwise.</b> A device that
/// talks without ever answering renews the gap for ever, so every loop built on one needs a second exit — a
/// total, kept comfortably above the sum of the phases inside it so that it can only ever be a backstop.
/// Pass that total in as <paramref name="ct"/> and this will step out of its way: a cancellation that came
/// from the caller is reported as a cancellation, and everything else is reported as silence. That
/// distinction is the one thing in here worth centralising, because getting it backwards tells a user their
/// device is silent when in fact the hub is shutting down.
/// </para>
///
/// <para>
/// <b>Everything else</b> is meant literally, and <see cref="Silent"/> says why. The line is drawn round the
/// caller's token rather than round this gap's own expiry, because those two are not complements: on an
/// abortable transport a cancellation can arrive with neither token touched, and a filter that asks "was it
/// mine?" lets that one escape as the bare "The operation was canceled" all of this exists to have removed.
/// </para>
/// </summary>
/// <param name="gap">How long the far end may say <b>nothing at all</b> before it counts as gone.</param>
/// <param name="ct">
/// The caller's own reason to stop — a lifetime token, or an operation backstop. Cancellation reaching this
/// token is <i>not</i> silence, and <see cref="Silent"/> says so. It is also the <i>only</i> thing that is
/// not, which is the deliberate half.
/// </param>
public sealed class IdleGap(TimeSpan gap, CancellationToken ct) : IDisposable
{
    readonly CancellationTokenSource _cts = Arm(gap, ct);

    static CancellationTokenSource Arm(TimeSpan gap, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(gap);
        return cts;
    }

    /// <summary>
    /// The token to give the receive. Cancelled when the far end has said nothing for <c>gap</c> — or when
    /// the caller's own token was, which <see cref="Silent"/> tells apart.
    /// </summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>
    /// The far end said something, so the gap starts again. Call it for <b>anything</b> that arrives — a
    /// byte, a frame, a message nobody wanted — because the question this measures is whether the device is
    /// alive, not whether it is being useful.
    /// </summary>
    public void Spoke() => _cts.CancelAfter(gap);

    /// <summary>
    /// Whether the wait ended because the far end went quiet, as opposed to because the caller said stop.
    /// <para>
    /// <b>Only meaningful inside an exception filter</b> — <c>catch (OperationCanceledException) when
    /// (gap.Silent)</c> — and that is not a style note. It answers a question about a cancellation that has
    /// already happened, so read outside a <c>catch</c> it is not false, it is meaningless: on a healthy
    /// gap that has never fired it reads <c>true</c>. There is no state here that could make it self-guard,
    /// because the case it exists for leaves no trace on either token. The one call it has is the filter.
    /// </para>
    /// <para>
    /// <b>It is the caller's cancellation that is identified, and everything else is silence.</b> That is
    /// deliberately the wide form. The obvious reading — "did <i>my</i> deadline fire?", <c>gap fired
    /// &amp;&amp; !ct fired</c> — partitions the cancellations arriving at a receive into three parts, not
    /// two: mine, the caller's, and <i>neither</i>. The third part has no home. It falls through the filter
    /// and escapes as a bare <see cref="OperationCanceledException"/>, which is how "The operation was
    /// canceled" reaches a person — the class of message the whole idle-gap exercise exists to have removed.
    /// A shutdown, a lifetime token or an operation backstop still keeps its own identity, because that is
    /// what the caller's own token is; nothing else does, and nothing else should.
    /// </para>
    /// <para>
    /// The third part is real on an abortable transport. A <c>ClientWebSocket</c> that has been aborted
    /// throws <c>OperationCanceledException("Aborted")</c> with <b>neither token cancelled</b>, and a socket
    /// that sets <c>KeepAliveTimeout</c> aborts <i>itself</i> when a ping goes unanswered — measured by
    /// <c>#166</c> on .NET 10.0.10 against a server that completes the handshake and then never pongs:
    /// <c>Aborted</c> at 2.3s with a sixty-second gap unfired. Reading that as silence is not a
    /// convenience: the socket aborted <i>because</i> nothing came back, so "the far end went quiet" is
    /// exactly what happened, and the caller's sentence for it is the true one.
    /// </para>
    /// <para>
    /// <b>What this costs, stated plainly.</b> On a <see cref="Stream"/> there is no third source at all, so
    /// on PjLink and on Samsung as it stands this is a no-op — the two forms differ on no reachable input.
    /// Where it could ever differ is a future transport that raises an
    /// <see cref="OperationCanceledException"/> for a reason that is <i>not</i> the far end going quiet, and
    /// there the caller's timeout sentence would name a cause that is not the real one. The narrow form does
    /// not do better with that fault, though: it does not diagnose it either, it just reports it as a bare
    /// cancellation naming no device and no phase. Between a message that is approximately true and
    /// actionable and one that is precisely useless, this picks the first — and the wire trace, which
    /// records what actually happened, is where the real cause was always going to be found.
    /// </para>
    /// <para>
    /// The webOS driver reached the same filter independently and for the same reason; its private wrapper
    /// at <c>WebOsDevice.ReceiveJsonAsync</c> carries the long version of the WebSocket half of this, and
    /// <c>docs/webos-capabilities.md</c> §7.2 the history.
    /// </para>
    /// </summary>
    public bool Silent => !ct.IsCancellationRequested;

    public void Dispose() => _cts.Dispose();
}
