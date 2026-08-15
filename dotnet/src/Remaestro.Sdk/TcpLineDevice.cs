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
