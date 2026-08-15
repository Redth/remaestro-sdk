using System.Text.Json;
using System.Text.Json.Serialization;

namespace Remaestro.ProxyAgent;

/// <summary>What the hub asks for when it opens a channel. The Linux half of <c>ChannelOpen</c>.</summary>
public sealed record ChannelRequest
{
    public string Role { get; init; } = "";
    public int Index { get; init; }

    public static ChannelRequest? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<ChannelRequest>(json, Json); }
        catch (JsonException) { return null; }
    }

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // A baud rate, a carrier, a radio address — every parameter of every other role arrives in this
        // document and means nothing to a USB remote. Skipping them is what lets one hub configure every
        // tier without knowing which it is talking to.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };
}

/// <summary>What this proxy says about itself when it connects. Mirrors <c>TunnelHello</c>.</summary>
public sealed record AgentHello(string Id, string Chip, string Firmware, string Name, string Token)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

/// <summary>
/// One conversation with the hub: say hello, then serve whatever it opens until the socket ends.
/// <para>
/// Takes a <see cref="Stream"/> rather than opening its own socket, and takes a delegate for opening an
/// input device rather than calling <see cref="File"/> itself. Both for the same reason — everything
/// interesting about this class is the protocol, and a test that has to plug in a remote and reach a hub is
/// a test nobody runs. With both seams handed in, the whole of it runs over a pair of in-memory streams and
/// a fake device, which is what <c>CLAUDE.md</c> means by tests not touching the network.
/// </para>
/// </summary>
public sealed class ProxySession
{
    readonly AgentConfig _config;
    readonly BoardIdentity _identity;
    readonly InputDevices _devices;
    readonly Func<InputDevice, CancellationToken, Task<Stream>> _open;
    readonly EvdevReader _evdev;
    readonly Action<string>? _log;

    readonly Dictionary<byte, CancellationTokenSource> _channels = [];
    readonly Lock _gate = new();

    // One writer at a time. Several channels can be pumping key presses while the hub opens another, and
    // two interleaved writes would splice two frames into nonsense — the same reason the hub's own end
    // funnels every write through a single task.
    readonly SemaphoreSlim _writing = new(1, 1);

    public ProxySession(
        AgentConfig config,
        BoardIdentity identity,
        InputDevices devices,
        Func<InputDevice, CancellationToken, Task<Stream>>? open = null,
        EvdevReader? evdev = null,
        Action<string>? log = null)
    {
        _config = config;
        _identity = identity;
        _devices = devices;
        _evdev = evdev ?? new EvdevReader();
        _log = log;

        _open = open ?? ((device, _) => Task.FromResult<Stream>(new FileStream(
            device.Path, FileMode.Open, FileAccess.Read,

            // Shared, because a Pi running a desktop has X or libinput holding the same node open, and
            // taking it exclusively would mean the proxy works only on a machine nobody is using.
            FileShare.ReadWrite)));
    }

    /// <summary>Whether the hub has answered our hello. Useful to a test, and to a log line.</summary>
    public bool Welcomed { get; private set; }

    /// <summary>Serve this connection until it ends or <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(Stream transport, CancellationToken ct = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            await SendAsync(transport, new TunnelFrame(TunnelOp.Hello, TunnelWire.Control,
                System.Text.Encoding.UTF8.GetBytes(new AgentHello(
                    _identity.Id, _identity.Chip, _identity.Firmware, _config.Name, _config.Token).ToJson())),
                stop.Token);

            var reader = new TunnelReader();
            var buffer = new byte[8192];

            while (!stop.IsCancellationRequested)
            {
                var read = await transport.ReadAsync(buffer, stop.Token);
                if (read == 0) break;                        // the hub closed

                foreach (var frame in reader.Push(buffer.AsSpan(0, read)))
                    await HandleAsync(transport, frame, stop.Token);

                if (reader.Fault is { } corrupt)
                {
                    _log?.Invoke($"The hub's stream went out of step: {corrupt}");
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down, or the hub went away */ }
        catch (IOException ex) { _log?.Invoke($"The connection to the hub ended: {ex.Message}"); }
        finally
        {
            await stop.CancelAsync();
            CloseEverything();
        }
    }

    async Task HandleAsync(Stream transport, TunnelFrame frame, CancellationToken ct)
    {
        switch (frame.Op)
        {
            case TunnelOp.Welcome:
                Welcomed = true;
                _log?.Invoke($"The hub recognised us as {_identity.Id}.");
                return;

            case TunnelOp.Ping:
                await SendAsync(transport, new TunnelFrame(TunnelOp.Pong, TunnelWire.Control, default), ct);
                return;

            case TunnelOp.Open:
                await OpenAsync(transport, frame, ct);
                return;

            case TunnelOp.Close:
                Release(frame.Channel);
                return;

            case TunnelOp.Data:
                // The driver's own line protocol, arriving through HidHostCodec. For a Bluetooth board these
                // are PAIR and CONNECT; a USB receiver has no pairing, so there is nothing to do and nothing
                // wrong. Dropped rather than refused: the driver says HELLO on every reconnect and a proxy
                // that complained about it would fill a trace with its own noise.
                return;

            case TunnelOp.Update:
                // A hub old enough to think this machine takes firmware. Refusing in words beats ignoring:
                // see ProxyUpdateVerdict.UpdatesItself, which is how a current hub knows not to ask.
                _log?.Invoke("The hub offered a firmware image. This proxy updates itself; ignoring it.");
                return;
        }
    }

    async Task OpenAsync(Stream transport, TunnelFrame frame, CancellationToken ct)
    {
        var channel = frame.Channel;

        if (ChannelRequest.Parse(frame.Text) is not { } request)
        {
            await RefuseAsync(transport, channel, "That open request wasn't readable.", ct);
            return;
        }

        if (!AgentRole.All.Contains(request.Role))
        {
            await RefuseAsync(transport, channel,
                $"This proxy can't do “{request.Role}”. It does: {string.Join(", ", AgentRole.All)}.", ct);
            return;
        }

        if (_config.Find(request.Role, request.Index) is not { } wiring)
        {
            await RefuseAsync(transport, channel,
                $"Nothing is set up as {request.Role} number {request.Index + 1} on this proxy.", ct);
            return;
        }

        if (_devices.Resolve(wiring.Device) is not { } device)
        {
            // Names what it did find. This is the failure somebody will actually hit — a dongle unplugged,
            // or moved to a machine that calls it something slightly different — and "no such device" alone
            // sends them to look at the config, which is where the answer isn't.
            var names = _devices.All().Where(d => d.Name.Length > 0).Select(d => $"“{d.Name}”").ToList();

            await RefuseAsync(transport, channel,
                $"No input device here matches “{wiring.Device}”. "
                + (names.Count > 0
                    ? $"Plugged in right now: {string.Join(", ", names)}."
                    : "Nothing at all is plugged in that reports buttons."), ct);
            return;
        }

        Stream input;
        try { input = await _open(device, ct); }
        catch (Exception ex)
        {
            // Overwhelmingly a permissions problem — an event node is root and the `input` group — and that
            // is worth saying at the hub rather than in a log on a machine with no screen.
            await RefuseAsync(transport, channel,
                $"Couldn't open {device.Path}: {ex.Message}", ct);
            return;
        }

        var stop = new CancellationTokenSource();
        lock (_gate) _channels[channel] = stop;

        await SendAsync(transport, new TunnelFrame(TunnelOp.Opened, channel, default), ct);

        // Which remote this turned out to be, in its own words, so the console and the driver's log name the
        // device rather than the selector that found it.
        await SendAsync(transport, new TunnelFrame(TunnelOp.Data, channel,
            (byte[])[HidHostOp.Attached, .. System.Text.Encoding.UTF8.GetBytes(
                device.Name.Length > 0 ? device.Name : device.Path)]), ct);

        _log?.Invoke($"Channel {channel} is listening to {device.Path} (“{device.Name}”).");

        _ = PumpAsync(transport, channel, input, stop.Token);
    }

    /// <summary>Relay one device's key events until the channel closes or the device goes away.</summary>
    async Task PumpAsync(Stream transport, byte channel, Stream input, CancellationToken ct)
    {
        var buffer = new byte[_evdev.RecordSize * 64];
        var held = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await input.ReadAsync(buffer.AsMemory(held), ct);

                // A device node that returns nothing is one that has been unplugged. The channel ends, the
                // driver's socket ends with it, and the hub reopens when it comes back.
                if (read == 0) break;

                held += read;

                var events = _evdev.Decode(buffer.AsSpan(0, held), out var consumed);

                // Whatever is left is a partial record, kept for the next read — see EvdevReader.Decode.
                if (consumed < held) Buffer.BlockCopy(buffer, consumed, buffer, 0, held - consumed);
                held -= consumed;

                foreach (var what in events)
                {
                    if (!EvdevReader.IsButton(what)) continue;

                    await SendAsync(transport,
                        new TunnelFrame(TunnelOp.Data, channel, EvdevReader.Payload(what)), ct);
                }
            }
        }
        catch (OperationCanceledException) { /* the hub closed this channel */ }
        catch (Exception ex) { _log?.Invoke($"Channel {channel} stopped reading: {ex.Message}"); }
        finally
        {
            await input.DisposeAsync();

            // Tell the hub, so a driver waiting on this isn't left holding a socket nothing will answer.
            try { await SendAsync(transport, new TunnelFrame(TunnelOp.Close, channel, default), CancellationToken.None); }
            catch (Exception) { /* the connection is probably what ended */ }

            Release(channel);
        }
    }

    Task RefuseAsync(Stream transport, byte channel, string why, CancellationToken ct)
    {
        _log?.Invoke($"Refused channel {channel}: {why}");
        return SendAsync(transport, TunnelFrame.OfText(TunnelOp.OpenFailed, channel, why), ct);
    }

    async Task SendAsync(Stream transport, TunnelFrame frame, CancellationToken ct)
    {
        var wire = TunnelWire.Encode(frame);

        await _writing.WaitAsync(ct);
        try
        {
            await transport.WriteAsync(wire, ct);
            await transport.FlushAsync(ct);
        }
        finally { _writing.Release(); }
    }

    void Release(byte channel)
    {
        CancellationTokenSource? stop;
        lock (_gate)
        {
            if (!_channels.Remove(channel, out stop)) return;
        }

        stop.Cancel();
        stop.Dispose();
    }

    void CloseEverything()
    {
        List<CancellationTokenSource> all;
        lock (_gate)
        {
            all = [.. _channels.Values];
            _channels.Clear();
        }

        foreach (var stop in all)
        {
            stop.Cancel();
            stop.Dispose();
        }
    }
}
