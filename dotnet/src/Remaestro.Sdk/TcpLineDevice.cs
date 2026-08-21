using System.IO.Ports;
using System.Net.Sockets;
using System.Text;

namespace Remaestro.Sdk;

/// <summary>
/// Where a <see cref="LineDevice"/>'s bytes go. The read/write/reconnect loop above it is identical
/// whether that's a socket or a cable, and writing it twice is how the two drift.
/// </summary>
public abstract class LineTransport
{
    /// <summary>Open a fresh connection. Throwing is how "not reachable" is reported.</summary>
    public abstract Task<(Stream Stream, IDisposable Owner)> OpenAsync(CancellationToken ct);

    /// <summary>Where this points, for the device's own state and for an error message worth reading.</summary>
    public abstract string Describe { get; }

    /// <summary>A short label for diagnostics — "tcp", "serial".</summary>
    public virtual string Kind => "line";
}

/// <summary>A socket. What most AV gear on a network offers.</summary>
public sealed class TcpTransport(string host, int port) : LineTransport
{
    public override string Describe => $"{host}:{port}";
    public override string Kind => "tcp";

    public override async Task<(Stream, IDisposable)> OpenAsync(CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, ct);
            return (client.GetStream(), client);
        }
        catch { client.Dispose(); throw; }
    }
}

/// <summary>
/// A serial cable. Plenty of AV gear speaks the same protocol over RS-232 as over the network — Denon's
/// own document describes one command set for both — and the serial port keeps working when the network
/// is absent, segregated, or the device is asleep enough to have dropped off it.
/// </summary>
public sealed class SerialTransport(string portName, int baud, int dataBits = 8,
    Parity parity = Parity.None, StopBits stopBits = StopBits.One) : LineTransport
{
    public override string Describe => $"{portName} at {baud} baud";
    public override string Kind => "serial";

    public override Task<(Stream, IDisposable)> OpenAsync(CancellationToken ct)
    {
        var port = new SerialPort(portName, baud, parity, dataBits, stopBits)
        {
            Handshake = Handshake.None,
            // The stream blocks on read until bytes arrive, which is what the read loop wants; a timeout
            // here would surface as a spurious disconnect every time the device is simply quiet.
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 2000,
        };

        try
        {
            port.Open();
            return Task.FromResult<(Stream, IDisposable)>((port.BaseStream, port));
        }
        catch { port.Dispose(); throw; }
    }
}

/// <summary>Building a transport from what the user configured.</summary>
public static class LineTransports
{
    /// <summary>
    /// A serial port that may not be a cable.
    /// <para>
    /// A port name of <c>tcp://host:port</c> gives a socket instead. That is how a UART on a proxy board
    /// across the house reaches a driver: the hub listens on a loopback port, splices it onto the proxy
    /// tunnel, and hands the driver an address. The driver goes on speaking the device's RS-232 command
    /// set — which is the point, because the protocol is the part the driver knows and the transport is
    /// the part it shouldn't have to.
    /// </para>
    /// <para>
    /// Note what is <i>not</i> here: the baud rate is ignored for a tcp:// port, because there is no UART
    /// at this end to configure. The hub sends it to the board when it opens the channel, which is what
    /// keeps it a setting on a web page rather than something baked into firmware.
    /// </para>
    /// </summary>
    public static LineTransport Serial(string portName, int baud, int dataBits = 8,
        Parity parity = Parity.None, StopBits stopBits = StopBits.One)
    {
        if (Remote(portName) is { } address) return new TcpTransport(address.Host, address.Port);

        return new SerialTransport(portName, baud, dataBits, parity, stopBits);
    }

    /// <summary>The host and port of a <c>tcp://host:port</c> port name, or null if it's an ordinary one.</summary>
    public static (string Host, int Port)? Remote(string portName)
    {
        if (!portName.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return null;

        var rest = portName[Scheme.Length..];
        var colon = rest.LastIndexOf(':');

        // A malformed one is left to the serial path, where it fails with a message naming the port — better
        // than silently connecting somewhere unintended.
        if (colon <= 0 || !int.TryParse(rest[(colon + 1)..], out var port) || port is < 1 or > 65535) return null;

        return (rest[..colon], port);
    }

    public const string Scheme = "tcp://";
}

/// <summary>
/// The serial failures whose remedy is the same whatever driver hit them, written once so four drivers
/// can't drift into four different answers to the same question.
/// </summary>
public static class SerialFaults
{
    /// <summary>
    /// What to do about a refused open. Takes the port name because the exception alone cannot tell you.
    /// <para>
    /// <c>SerialPort.Open</c> on Unix raises <see cref="UnauthorizedAccessException"/> — "Access to the port
    /// 'X' is denied" — for <i>every</i> way an open can fail, including a port that simply isn't there.
    /// <c>/dev/doesnotexist</c> and a genuinely unreadable node are indistinguishable by type or by message.
    /// The <see cref="FileNotFoundException"/> arm every driver carries alongside this one is therefore dead
    /// code on Linux, and for years the far more common cause — a port that has been unplugged, renumbered,
    /// or never existed — was being reported as a permissions problem, sending people to `dialout` for a
    /// fault that had nothing to do with permissions.
    /// </para>
    /// <para>
    /// So look at the node before blaming the group. Only when the port is really there is this an access
    /// problem, and only then is <c>group_add</c> worth naming.
    /// </para>
    /// </summary>
    public static string OpenRefused(string? portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
            return "Couldn't open the serial port, and no port name is configured — pick one.";

        // A well-formed tcp:// name is a socket and never reaches this exception, so a tcp:// name that got
        // here was rejected by the parser and quietly tried as a literal device path. Say that, rather than
        // let it read as a permissions fault on a device that was never a device.
        if (portName.StartsWith(LineTransports.Scheme, StringComparison.OrdinalIgnoreCase))
            return $"'{portName}' isn't a usable proxy address, so it was tried as a serial device and there "
                 + "is no such device. It has to look like `tcp://host:port` — check the proxy is online and "
                 + "the port was picked again since it last moved.";

        if (!File.Exists(portName))
            return $"There's no serial port at {portName}. It may have been unplugged, or renumbered — "
                 + "rescan and pick it again. (A missing port and one you're not allowed to open raise the "
                 + "same error here, and this one is missing.)";

        // The GID is given as a number because Docker resolves `group_add` names against the container's
        // /etc/group rather than the host's, so a name that is right on the host can silently grant the wrong
        // group — or none. The .NET runtime image happens to carry `dialout` at 20, which is what makes the
        // name look correct; its `input` is 997 against Raspberry Pi OS's 996, the same mistake with nothing
        // to warn you.
        return $"Permission denied opening {portName} — it exists, but the hub isn't allowed to open it. "
             + "In a container, add the host's serial group to the service: `group_add: [\"20\"]` in compose, "
             + "using the number from `getent group dialout` on the host rather than the name, which Docker "
             + "looks up inside the container. On a bare install, run `sudo usermod -aG dialout $USER` and "
             + "restart the hub.";
    }
}

/// <summary>
/// A device reached over one long-lived connection that speaks lines of text. A large share of AV gear
/// works this way — Anthem, TiVo, Kaleidescape, Lutron, WattBox, HEOS — and each driver was otherwise going
/// to re-write the same connect / read / reconnect loop, which is exactly where the subtle bugs live: a
/// read that returns half a line, a write racing the reconnect, a socket that dies quietly.
/// <para>
/// A driver supplies a transport, the terminator and what to do with each line. Everything else — reconnect
/// with backoff, the <c>online</c> state, framing, one writer at a time — happens here.
/// </para>
/// </summary>
public abstract class LineDevice : DeviceBase
{
    readonly LineTransport _transport;
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _writeLock = new(1, 1);
    Stream? _stream;

    protected LineDevice(string deviceId, string name, LineTransport transport) : base(deviceId, name)
    {
        _transport = transport;
        SetState("online", "false");
        SetState("link", transport.Describe);
    }

    /// <summary>What ends a line on the wire. CRLF, LF and bare CR are all in use out there.</summary>
    protected virtual string Terminator => "\r\n";

    /// <summary>
    /// What separates one message from the next when reading. Defaults to <see cref="Terminator"/>;
    /// override where a device ends its lines differently from what it expects to receive — Anthem ends
    /// every message with a semicolon and doesn't want newlines at all.
    /// </summary>
    protected virtual string ReadDelimiter => Terminator;

    protected virtual Encoding Wire => Encoding.ASCII;

    /// <summary>How long to wait before trying again after the connection drops.</summary>
    protected virtual TimeSpan RetryDelay => TimeSpan.FromSeconds(5);

    protected CancellationToken Stopping => _cts.Token;
    public override bool Online => GetState().GetValueOrDefault("online") == "true";

    /// <summary>Start talking. Call once the subclass has finished its own construction.</summary>
    protected void Run() => _ = RunAsync();

    /// <summary>
    /// Called each time the connection comes up — ask for whatever state the device won't volunteer.
    /// Anything thrown here drops the connection and retries, same as a read failure.
    /// </summary>
    protected virtual Task OnConnectedAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>One message from the device, with the delimiter stripped. Never called with an empty line.</summary>
    protected abstract void OnLine(string line);

    /// <summary>Called when the connection drops, so a driver can forget state it can no longer vouch for.</summary>
    protected virtual void OnDisconnected() { }

    protected async Task SendLineAsync(string line, CancellationToken ct = default)
    {
        var stream = _stream ?? throw new InvalidOperationException($"{Name} is offline");
        var bytes = Wire.GetBytes(line + Terminator);
        await _writeLock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
            Diag.TxBytes(DeviceId, _transport.Kind, bytes, line, _transport.Describe);
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>Send a line and report success, so a driver's ExecuteAsync is one line per command.</summary>
    protected async Task<CommandResult> SendResultAsync(string line, CancellationToken ct = default)
    {
        try { await SendLineAsync(line, ct); return CommandResult.Success(); }
        catch (Exception ex) { return CommandResult.Fail(ex.Message); }
    }

    // ---- One command in flight, and the device's verdict on it -----------------------------------------

    /// <summary>
    /// A command that has gone out and is waiting to hear whether the device objects to it.
    ///
    /// <para>
    /// <b>What is being waited for is an objection, not a confirmation.</b> That is the shape every line
    /// protocol in this fleet turned out to have, and it is why this is worth writing once. A refusal —
    /// <c>~ERROR,2</c>, <c>!I</c>, <c>#Error</c>, <c>CH_FAILED NO_LIVE</c>, <c>"result": "fail"</c> — is a
    /// parse or a lookup failure rather than an action, so it comes back within a round trip or not at all.
    /// The positive side is weaker in every one of them: an Anthem echoes what it <i>did</i> rather than
    /// what it was told, a Lutron load already at the level says nothing whatever, a WattBox's <c>OK</c>
    /// precedes the relay. So <see cref="Took"/> exists only to buy back the latency, and what silence
    /// means is the driver's to decide — see <see cref="NothingSaid"/>.
    /// </para>
    /// <para>
    /// <b><see cref="Tag"/> is how a driver recognises its own answer.</b> This is the part that genuinely
    /// differs, and it differs more than it looks: Lutron and Anthem have nothing to correlate on at all
    /// and rely on there being exactly one command it could belong to; HEOS echoes the command path; a TiVo
    /// names the channel it tuned to, and announces channel changes somebody made on the sofa in the same
    /// words. So the tag is whatever that driver needs to hold onto between sending and hearing, and the
    /// comparison stays in the driver's own <see cref="OnLine"/> where the protocol is understood.
    /// </para>
    /// </summary>
    protected sealed class Turn(object? tag)
    {
        /// <summary>Whatever the driver put here when it sent, so its <c>OnLine</c> can recognise the reply.</summary>
        public object? Tag { get; } = tag;

        internal TaskCompletionSource<string?> Verdict { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The device said no, in words worth showing whoever pressed the key.</summary>
        public void Refused(string why) => Verdict.TrySetResult(why);

        /// <summary>
        /// The device answered. Ends the wait early rather than spending the rest of the window; it is not
        /// a claim that the thing asked for happened, which none of these protocols can give.
        /// </summary>
        public void Took() => Verdict.TrySetResult(null);
    }

    Turn? _inFlight;
    readonly SemaphoreSlim _turnLock = new(1, 1);

    /// <summary>The command waiting on this connection, or null. Read from <c>OnLine</c> to answer it.</summary>
    protected Turn? InFlight => Volatile.Read(ref _inFlight);

    /// <summary>
    /// How long a command waits for the device to object to it.
    /// <para>
    /// It measures one round trip and the device's own turnaround, and nothing beyond that — not a zone
    /// powering up, not a lamp fading, not a volume ramping. A driver whose device is slower to object, or
    /// which waits for a physical outcome rather than an objection, overrides this and says why in the
    /// override.
    /// </para>
    /// </summary>
    protected virtual TimeSpan Objects => TimeSpan.FromSeconds(1);

    /// <summary>
    /// What a command reports when the device said nothing at all inside <see cref="Objects"/>.
    ///
    /// <para>
    /// <b>Silence is a success by default, and that default is load-bearing rather than lazy.</b> Every
    /// one of these protocols has a device or a firmware that answers nothing to a command it performed
    /// perfectly — an integration login with no monitoring rights, a load told to be what it already is, a
    /// WattBox 250 where the 800 answers, an Anthem told to do what it is already doing. Reading that as a
    /// failure would put a red step in activities that have always worked, which is a worse bug than the
    /// one this machinery exists to fix.
    /// </para>
    /// <para>
    /// Override it where the protocol really does answer every command, so nothing coming back means
    /// nothing is known — and say, in the sentence, what the person should go and look at.
    /// </para>
    /// </summary>
    protected virtual CommandResult NothingSaid(Turn turn) => CommandResult.Success();

    /// <summary>
    /// What is at the far end of this connection, in a few words — "the processor", "the box". Used in the
    /// sentence a command gets when the connection goes out from under it, which reads better naming the
    /// thing that stopped answering than repeating the device's own name twice.
    /// </summary>
    protected virtual string FarEnd => "the device";

    /// <summary>
    /// Send, and let the device's refusal — if there is one — be the answer.
    ///
    /// <para>
    /// <b>One at a time, per connection.</b> Most of these protocols carry nothing in a refusal that could
    /// attribute it to a command, so the only thing that can is that there is exactly one command it could
    /// belong to. Drivers whose replies <i>are</i> attributable still go through here: the cost is a queue
    /// on a device nobody is pressing two buttons on at once, and the gain is one implementation.
    /// </para>
    /// </summary>
    /// <param name="tag">
    /// Whatever <c>OnLine</c> will need to recognise this command's answer — see <see cref="Turn.Tag"/>.
    /// </param>
    protected async Task<CommandResult> SendAndHearAsync(string line, CancellationToken ct, object? tag = null)
    {
        await _turnLock.WaitAsync(ct);
        var turn = new Turn(tag);
        Volatile.Write(ref _inFlight, turn);
        try
        {
            if (await SendResultAsync(line, ct) is { Ok: false } failed) return failed;

            var verdict = await turn.Verdict.Task.WaitAsync(Objects, ct);
            return verdict is null ? CommandResult.Success() : CommandResult.Fail(verdict);
        }
        catch (TimeoutException) { return NothingSaid(turn); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        finally
        {
            Volatile.Write(ref _inFlight, null);
            _turnLock.Release();
        }
    }

    /// <summary>
    /// A command that was in flight when the connection went has no verdict coming, so it is answered
    /// here and it says honestly that it does not know.
    ///
    /// <para>
    /// <b>This lives in the base class rather than in <see cref="OnDisconnected"/>, and that is the whole
    /// point of it being here.</b> The obvious place to put it is a driver's own <c>OnDisconnected</c>, and
    /// that is where the first one was written — but two of the five drivers already override that method
    /// to forget state and do not chain to <c>base</c>, so a sixth driver clearing a state key would
    /// silently take this back and spend its whole window returning success on a dropped socket. Nothing
    /// would report it: the state is right, the wait is the documented length, and the answer is the
    /// documented answer for silence. Written here, it cannot be forgotten by a driver that never knew
    /// about it.
    /// </para>
    /// <para>
    /// <b>Why not a success.</b> A device that legitimately drops the connection while carrying out what it
    /// was told — a reboot, a power-off that takes the network interface with it — does exist, and for that
    /// one this is pessimistic. But the sentence does not claim a failure either: it says the outcome is
    /// unknown, which is true in both cases, and the far commoner cause is the other one. These are single
    /// long-lived sessions, and several of these devices accept exactly one of them, so a second app on the
    /// network taking the session drops ours mid-command with no relationship to what was sent.
    /// </para>
    /// </summary>
    void NoVerdictIsComing() =>
        Volatile.Read(ref _inFlight)?.Refused(
            $"The connection to {FarEnd} dropped before it answered, so whether {Name} took that is unknown.");

    async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var (stream, owner) = await _transport.OpenAsync(_cts.Token);
                using var _owner = owner;
                using var _stream_ = stream;
                _stream = stream;
                SetState("online", "true");
                SetState("lastError", null);
                Diag.Open(DeviceId, _transport.Kind, _transport.Describe);

                await OnConnectedAsync(_cts.Token);
                await ReadLoopAsync(stream, _cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { SetState("lastError", ex.Message); Diag.Error(DeviceId, _transport.Kind, ex.Message, _transport.Describe); }
            finally
            {
                _stream = null;
                SetState("online", "false");
                Diag.Close(DeviceId, _transport.Kind);
                // Before the driver's own cleanup, and not inside it — see NoVerdictIsComing.
                NoVerdictIsComing();
                try { OnDisconnected(); } catch { /* a driver's own cleanup must not stop the retry */ }
            }

            try { await Task.Delay(RetryDelay, _cts.Token); } catch { break; }
        }
    }

    async Task ReadLoopAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var pending = new StringBuilder();
        var delimiter = ReadDelimiter;

        while (!ct.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;                       // the device closed the connection
            pending.Append(Wire.GetString(buffer, 0, read));

            // One read can carry several messages, or half of one — hand over only whole ones.
            var whole = 0;
            while (true)
            {
                var text = pending.ToString();
                var at = text.IndexOf(delimiter, StringComparison.Ordinal);
                if (at < 0) break;

                var line = text[..at].Trim('\r', '\n');
                pending.Remove(0, at + delimiter.Length);
                whole++;
                if (line.Length > 0) { Diag.Rx(DeviceId, _transport.Kind, line, _transport.Describe); OnLine(line); }
            }

            // Bytes that didn't complete a message are the ones worth seeing raw: a device answering with a
            // terminator you didn't expect looks, in a line-oriented trace, exactly like a device saying
            // nothing at all. This is the record that tells those two apart.
            if (whole == 0)
                Diag.RxBytes(DeviceId, _transport.Kind, buffer.AsSpan(0, read),
                    $"{read} bytes, no complete message yet", _transport.Describe);

            // A device that never sends the delimiter would otherwise grow this forever.
            if (pending.Length > 64 * 1024) pending.Clear();
        }
    }

    public override ValueTask DisposeAsync()
    {
        _cts.Cancel();
        // Anything still waiting on a verdict is answered before the socket goes, so a disposal can never
        // be the thing a command is waiting on. See CLAUDE.md on disposers that await their own teardown.
        NoVerdictIsComing();
        _cts.Dispose();
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The common case: a line device on a socket. Kept as its own type because most drivers are network-only
/// and shouldn't have to say so.
/// </summary>
public abstract class TcpLineDevice(string deviceId, string name, string host, int port)
    : LineDevice(deviceId, name, new TcpTransport(host, port));
