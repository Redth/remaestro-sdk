using System.Net.Sockets;
using Remaestro.Sdk;

namespace Remaestro.Drivers.Screen;

/// <summary>
/// Motorised projection screens, projector lifts and masking — the things in a cinema room that move out
/// of the way, and that nothing else in a setup can usually reach.
/// <para>
/// One driver rather than one per brand, because they are one product underneath: the tubular motor
/// controllers come from a handful of OEMs and the great majority speak the same five bytes at 2400 baud.
/// Dragonfly and Grandview publish byte-identical tables. Where a screen differs, the frame is spelled out
/// in the advanced fields rather than requiring a new driver.
/// </para>
/// </summary>
public sealed class ScreenDriver : IRemaestroDriver
{
    public string TypeId => "screen";
    public string DisplayName => "Projection Screen or Lift";
    public string Description => "Motorised screens, lifts and masking over RS-232 — Dragonfly, Grandview, Elite and most tubular-motor controllers.";

    public IReadOnlyList<string> Traits { get; } = [DeviceTrait.Cover];

    public IReadOnlyList<ConfigField> ConfigSchema { get; } =
    [
        new("connection", "Connection", Default: "serial", Help: "How reMaestro reaches the screen controller",
            Options: [
                new("serial", "RS-232 serial", "A cable from the hub to the screen's control input"),
                new("network", "Serial over the network", "A serial gateway near the screen — a Global Caché, or a USR-style TCP-to-serial box"),
            ]),

        new("serialPort", "Serial port", Type: "serial", ShowWhen: "connection=serial",
            Help: "The USB-to-serial adapter wired to the screen. Most take RS-232 on two pins — ground and receive"),
        new("baud", "Baud", Default: "2400", ShowWhen: "connection=serial", Advanced: true,
            Help: "These controllers run at 2400 and don't negotiate — leave it unless yours is unusual"),

        new("host", "Gateway host", ShowWhen: "connection=network", Help: "The serial gateway's address"),
        new("port", "Gateway port", Default: "4999", ShowWhen: "connection=network",
            Help: "The gateway's raw serial port — 4999 on a Global Caché"),

        new("address", "Address code", Default: ScreenProtocol.DefaultAddress, Advanced: true,
            Help: "Six hex characters identifying this screen on a shared bus. Screens leave the factory as EEEEEE"),

        new("repeat", "Send each command twice", Type: "bool", Default: "true", Advanced: true,
            Help: "Grandview's manual says the string must be sent twice in a row. Harmless where it isn't needed, and the difference between a screen that always moves and one that usually does"),

        new("travelSeconds", "Travel time", Type: "number", Default: "25", Min: 1, Max: 300,
            Help: "How long a full up or down takes. The protocol can't report position, so this is what the estimate is based on"),

        new("invert", "Up and down are swapped", Type: "bool", Default: "false", Advanced: true,
            Help: "Some lifts and masking systems are wired the other way round. Set this rather than rewiring"),
    ];

    public IReadOnlyList<CommandInfo> Commands { get; } =
    [
        new("down", "Down", "Lower the screen — deploy"),
        new("up", "Up", "Raise the screen — retract"),
        new("stop", "Stop", "Stop wherever it is"),

        new("send_raw", "Send Raw", "Five bytes as hex, for a controller that doesn't use the usual frame",
            [new("bytes", "Bytes", Required: true, Help: "e.g. FF EE EE EE DD")]),
    ];

    public IReadOnlyList<EventSchema> Events { get; } =
    [
        new("screen.moving", "The screen started moving", [new("direction")]),
        new("screen.settled", "The screen should have finished moving", [new("position")]),
    ];

    public IReadOnlyList<StateField> StateSchema { get; } =
    [
        new("online", "bool"), new("position", "string", "up | down | moving | unknown"),
        new("lastCommand"), new("lastFrame"), new("link"), new("note"), new("lastError"),
    ];

    public Task<IRemaestroDevice> CreateDeviceAsync(string deviceId, string name, IReadOnlyDictionary<string, string> config, CancellationToken ct)
        => Task.FromResult<IRemaestroDevice>(new ScreenDevice(deviceId, name, config, Commands));
}

internal sealed class ScreenDevice : DeviceBase
{
    readonly string _address;
    readonly bool _repeat, _invert;
    readonly TimeSpan _travel;
    readonly Func<byte[], CancellationToken, Task> _send;
    readonly string _link;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly CancellationTokenSource _cts = new();

    readonly string? _portName;
    readonly int _baud;
    readonly string _host;
    readonly int _tcpPort;
    ByteLink? _port;

    CancellationTokenSource? _settle;

    public ScreenDevice(string id, string name, IReadOnlyDictionary<string, string> config, IReadOnlyList<CommandInfo> commands)
        : base(id, name)
    {
        Commands = commands;
        _address = config.GetValueOrDefault("address", ScreenProtocol.DefaultAddress);
        _repeat = !config.GetValueOrDefault("repeat", "true").Equals("false", StringComparison.OrdinalIgnoreCase);
        _invert = config.GetValueOrDefault("invert", "false").Equals("true", StringComparison.OrdinalIgnoreCase);
        _travel = TimeSpan.FromSeconds(
            int.TryParse(config.GetValueOrDefault("travelSeconds"), out var t) ? Math.Clamp(t, 1, 300) : 25);

        if (config.GetValueOrDefault("connection", "serial").Equals("network", StringComparison.OrdinalIgnoreCase))
        {
            _host = config.GetValueOrDefault("host", "");
            _tcpPort = int.TryParse(config.GetValueOrDefault("port"), out var p) ? p : 4999;
            _link = $"{_host}:{_tcpPort}";
            _send = SendOverTcpAsync;
            _baud = ScreenProtocol.Baud;
        }
        else
        {
            _portName = config.GetValueOrDefault("serialPort", "");
            _baud = int.TryParse(config.GetValueOrDefault("baud"), out var b) ? b : ScreenProtocol.Baud;
            _host = "";
            _link = LineTransports.Remote(_portName) is not null
                ? "through a proxy"
                : $"{_portName} at {_baud} baud";
            _send = SendOverSerialAsync;
        }

        SetState("link", _link);
        SetState("online", "false");
        SetState("position", ScreenProtocol.Believed.Unknown);
        // Said before anyone has a chance to wonder why the position never updates on its own.
        SetState("note", ScreenProtocol.OneWayNote);
    }

    public override IReadOnlyList<CommandInfo> Commands { get; }

    /// <summary>
    /// Nothing to be online <i>with</i> — the controller never answers. This reports whether the last
    /// attempt to reach it worked, which is the most that can honestly be claimed.
    /// </summary>
    public override bool Online => GetState().GetValueOrDefault("online") == "true";

    public override async Task<CommandResult> ExecuteAsync(string commandId, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        byte[] frame;
        string? settleTo = null;

        switch (commandId)
        {
            case "down":
            case "up":
            {
                // A lift wired the other way round would otherwise need rewiring to behave.
                var down = (commandId == "down") != _invert;
                frame = ScreenProtocol.Frame(down ? ScreenProtocol.ActionDown : ScreenProtocol.ActionUp, _address);
                settleTo = commandId == "down" ? ScreenProtocol.Believed.Down : ScreenProtocol.Believed.Up;
                break;
            }

            case "stop":
                frame = ScreenProtocol.Frame(ScreenProtocol.ActionStop, _address);
                break;

            case "send_raw":
                if (ParseHex(args.GetValueOrDefault("bytes", "")) is not { } raw)
                    return CommandResult.Fail("Give the bytes as hex, e.g. FF EE EE EE DD");
                frame = raw;
                break;

            default:
                return CommandResult.Fail($"Unknown command '{commandId}'");
        }

        await _gate.WaitAsync(ct);
        try
        {
            await _send(frame, ct);

            // Grandview's manual is explicit that the string goes twice in a row. It costs nothing where
            // it isn't needed, and it's the difference between a screen that always moves and one that
            // usually does.
            if (_repeat && commandId != "send_raw")
            {
                await Task.Delay(120, ct);
                await _send(frame, ct);
            }

            SetState("online", "true");
            SetState("lastError", "");
            SetState("lastCommand", commandId);
            SetState("lastFrame", ScreenProtocol.Describe(frame));
        }
        catch (Exception ex)
        {
            Drop();
            var why = Explain(ex);
            SetState("lastError", why);
            return CommandResult.Fail(why);
        }
        finally { _gate.Release(); }

        BeginSettling(commandId, settleTo);
        return CommandResult.Success();
    }

    /// <summary>
    /// Move the believed position along. There's nothing to poll, so the only thing that can be said is
    /// "it was told to go down, and enough time has passed that it probably has".
    /// </summary>
    void BeginSettling(string commandId, string? settleTo)
    {
        _settle?.Cancel();
        _settle?.Dispose();
        _settle = null;

        if (settleTo is null)
        {
            // Stop lands it somewhere between the two, and there's no way to find out where.
            SetState("position", ScreenProtocol.Believed.Unknown);
            return;
        }

        SetState("position", ScreenProtocol.Believed.Moving);
        Emit("screen.moving", new Dictionary<string, string> { ["direction"] = commandId });

        var settle = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _settle = settle;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_travel, settle.Token);
                SetState("position", settleTo);
                Emit("screen.settled", new Dictionary<string, string> { ["position"] = settleTo });
            }
            catch (OperationCanceledException) { /* another command arrived first */ }
        }, CancellationToken.None);
    }

    // ---- Transports ---------------------------------------------------------------------------------

    async Task SendOverSerialAsync(byte[] frame, CancellationToken ct)
    {
        var port = _port is { Alive: true } open ? open : await OpenAsync(ct);
        await port.WriteAsync(frame, ct);
    }

    /// <summary>
    /// The cable, or a proxy's UART standing in for one — the port name says which, and a screen controller
    /// is exactly the kind of thing that ends up on the far side of a room from the rack.
    /// </summary>
    async Task<ByteLink> OpenAsync(CancellationToken ct)
    {
        Drop();
        return _port = await ByteLink.OpenAsync(LineTransports.Serial(_portName!, _baud), ct, deviceId: DeviceId);
    }

    /// <summary>
    /// A serial gateway near the screen. Common where the screen is across the room from the rack, and
    /// the reason this driver isn't serial-only: the bytes are identical, only the pipe differs.
    /// </summary>
    async Task SendOverTcpAsync(byte[] frame, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connect.CancelAfter(TimeSpan.FromSeconds(5));

        await client.ConnectAsync(_host, _tcpPort, connect.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    void Drop()
    {
        SetState("online", "false");
        try { _port?.Dispose(); } catch { /* already gone */ }
        _port = null;
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    /// <summary>Hex with or without spaces or 0x, since people paste it out of a manual either way.</summary>
    internal static byte[]? ParseHex(string text)
    {
        var s = text.Replace("0x", "", StringComparison.OrdinalIgnoreCase)
                    .Replace(",", " ").Replace("-", " ")
                    .Replace(" ", "").Trim();

        if (s.Length == 0 || s.Length % 2 != 0 || !s.All(Uri.IsHexDigit)) return null;

        var bytes = new byte[s.Length / 2];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return bytes;
    }

    string Explain(Exception ex) => ex switch
    {
        UnauthorizedAccessException => SerialFaults.OpenRefused(_portName),
        FileNotFoundException or DirectoryNotFoundException =>
            "That serial port doesn't exist any more. It may have been unplugged, or renumbered — pick it again.",
        IOException io when io.Message.Contains("busy", StringComparison.OrdinalIgnoreCase) =>
            "Something else has the serial port open.",
        SocketException =>
            "Couldn't reach the serial gateway. Check its address and that its serial port is set to 2400 8N1.",
        _ => ex.Message,
    };

    public override async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _settle?.Cancel();
        _settle?.Dispose();
        _cts.Dispose();
        Drop();
        _gate.Dispose();
    }
}
