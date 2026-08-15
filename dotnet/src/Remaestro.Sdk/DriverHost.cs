using System.Collections.Concurrent;
using System.Threading.Channels;
using Google.Protobuf.Collections;
using Grpc.Core;
using Remaestro.Grpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Remaestro.Sdk;

/// <summary>
/// Hosts a driver as an out-of-process gRPC server. A driver's whole <c>Program.cs</c> is just:
/// <code>await DriverHost.RunAsync(new MyDriver(), args);</code>
/// The hub launches the process and passes the listen endpoint via <c>ASPNETCORE_URLS</c>.
/// </summary>
public static class DriverHost
{
    public static async Task RunAsync(IRemaestroDriver driver, string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // gRPC needs HTTP/2; the hub talks cleartext h2c on the loopback, so force Http2 on all endpoints.
        builder.Services.Configure<KestrelServerOptions>(o =>
            o.ConfigureEndpointDefaults(l => l.Protocols = HttpProtocols.Http2));

        builder.Services.AddGrpc();
        builder.Services.AddSingleton(driver);
        builder.Services.AddSingleton<DriverServiceImpl>();

        var app = builder.Build();
        app.MapGrpcService<DriverServiceImpl>();
        await app.RunAsync();
    }
}

/// <summary>The gRPC surface a driver exposes. Public so the framing and the command-refresh
/// contract can be tested without standing up a server.</summary>
public sealed class DriverServiceImpl : Driver.DriverBase
{
    private readonly IRemaestroDriver _driver;
    private readonly ConcurrentDictionary<string, IRemaestroDevice> _devices = new();
    private readonly Channel<DeviceEventMessage> _events = Channel.CreateUnbounded<DeviceEventMessage>();

    public DriverServiceImpl(IRemaestroDriver driver) => _driver = driver;

    /// <summary>The call's cancellation token. Guarded so a missing context can't masquerade as a driver error.</summary>
    static CancellationToken Token(ServerCallContext? context) => context?.CancellationToken ?? CancellationToken.None;

    public override Task<DriverDescriptor> Describe(DescribeRequest request, ServerCallContext context)
    {
        var d = new DriverDescriptor
        {
            TypeId = _driver.TypeId,
            DisplayName = _driver.DisplayName,
            Description = _driver.Description,
            SupportsNavigation = _driver.SupportsNavigation,
            SupportsEpg = _driver.SupportsEpg,
            SupportsDeviceRemotes = _driver.SupportsDeviceRemotes,
            Traits = { _driver.Traits }
        };
        d.ConfigSchema.AddRange(_driver.ConfigSchema.Select(ToProto));
        d.Commands.AddRange(_driver.Commands.Select(ToProto));
        d.Events.AddRange(_driver.Events.Select(ToProto));
        d.StateSchema.AddRange(_driver.StateSchema.Select(ToProto));
        d.DiscoveryServices.AddRange(_driver.DiscoveryServices);
        d.RemoteTemplates.AddRange(_driver.RemoteTemplates.Select(ToProto));
        return Task.FromResult(d);
    }

    /// <summary>The device's live input list, when it knows one (<see cref="IInputSourceDevice"/>).</summary>
    public override async Task<InputListMessage> ListInputs(DeviceRef request, ServerCallContext context)
    {
        if (!_devices.TryGetValue(request.DeviceId, out var device) || device is not IInputSourceDevice src)
            return new InputListMessage { Supported = false };
        try
        {
            var inputs = await src.ListInputsAsync(Token(context));
            var msg = new InputListMessage { Supported = true };
            msg.Inputs.AddRange(inputs.Select(i => new InputSourceMessage
            {
                Id = i.Id, Label = i.Label, Detail = i.Detail, Current = i.Current,
            }));
            return msg;
        }
        catch
        {
            // A device that can't be reached right now shouldn't break the picker — fall back to statics.
            return new InputListMessage { Supported = false };
        }
    }

    /// <summary>The device's guide over the asked window, when it's a source (<see cref="IEpgSource"/>).</summary>
    public override async Task<EpgMessage> GetEpg(EpgRequest request, ServerCallContext context)
    {
        if (!_devices.TryGetValue(request.DeviceId, out var device) || device is not IEpgSource src)
            return new EpgMessage { Supported = false };
        try
        {
            var from = DateTimeOffset.FromUnixTimeSeconds(request.FromUnix);
            var to = DateTimeOffset.FromUnixTimeSeconds(request.ToUnix);
            var data = await src.GetEpgAsync(from, to, Token(context));

            var msg = new EpgMessage { Supported = true };
            msg.Channels.AddRange(data.Channels.Select(c => new EpgChannelMessage
            {
                Id = c.Id, Name = c.Name, Logo = c.Logo ?? "", Number = c.Number ?? "", StreamUrl = c.StreamUrl ?? "",
            }));
            msg.Programmes.AddRange(data.Programmes.Select(p => new EpgProgrammeMessage
            {
                ChannelId = p.ChannelId,
                StartUnix = p.Start.ToUnixTimeSeconds(),
                StopUnix = p.Stop.ToUnixTimeSeconds(),
                Title = p.Title, Subtitle = p.Subtitle ?? "", Description = p.Description ?? "",
                Category = p.Category ?? "", Image = p.Image ?? "", Episode = p.Episode ?? "", IsNew = p.IsNew,
            }));
            return msg;
        }
        catch
        {
            // An unreachable feed shouldn't blank the whole grid — this source just contributes nothing.
            return new EpgMessage { Supported = false };
        }
    }

    /// <summary>The device's live app list, when it knows one (<see cref="IAppListDevice"/>).</summary>
    public override async Task<AppListMessage> ListApps(DeviceRef request, ServerCallContext context)
    {
        if (!_devices.TryGetValue(request.DeviceId, out var device) || device is not IAppListDevice src)
            return new AppListMessage { Supported = false };
        try
        {
            var apps = await src.ListAppsAsync(Token(context));
            var msg = new AppListMessage { Supported = true };
            foreach (var a in apps)
            {
                var app = new AppMessage
                {
                    Id = a.Id, Name = a.Name, Icon = a.Icon, Detail = a.Detail, Current = a.Current,
                };
                foreach (var p in a.Params ?? [])
                    app.Params.Add(new AppParamMessage { Key = p.Key, Label = p.Label, Kind = p.Kind, Required = p.Required });
                msg.Apps.Add(app);
            }
            return msg;
        }
        catch
        {
            // Same as inputs: an unreachable device falls back to whatever apps the driver declares.
            return new AppListMessage { Supported = false };
        }
    }

    /// <summary>
    /// The values a parameter will take, asked live (<see cref="IOptionSourceDevice"/>). A device that
    /// can't answer for this key reports unsupported, and the caller falls back to free text.
    /// </summary>
    public override async Task<OptionsListMessage> ListOptions(OptionsRequest request, ServerCallContext context)
    {
        if (!_devices.TryGetValue(request.DeviceId, out var device) || device is not IOptionSourceDevice source)
            return new OptionsListMessage { Supported = false };
        try
        {
            var options = await source.ListOptionsAsync(request.OptionsKey, Token(context));
            var msg = new OptionsListMessage { Supported = true };
            msg.Options.AddRange(options.Select(ToProto));
            return msg;
        }
        catch
        {
            // A device that can't be reached shouldn't break the picker — fall back to typing a value.
            return new OptionsListMessage { Supported = false };
        }
    }

    /// <summary>What sits behind this device, when it fronts a bridge (<see cref="IBridgeDevice"/>).</summary>
    public override async Task<BridgedDeviceListMessage> ListBridgedDevices(DeviceRef request, ServerCallContext context)
    {
        if (!_devices.TryGetValue(request.DeviceId, out var device) || device is not IBridgeDevice bridge)
            return new BridgedDeviceListMessage { Supported = false };
        try
        {
            var found = await bridge.ListBridgedDevicesAsync(Token(context));
            var msg = new BridgedDeviceListMessage { Supported = true };
            foreach (var d in found)
            {
                var m = new BridgedDeviceMessage { Id = d.Id, Name = d.Name, Kind = d.Kind, Detail = d.Detail };
                if (d.Config is not null) foreach (var (k, v) in d.Config) m.Config[k] = v;
                msg.Devices.Add(m);
            }
            return msg;
        }
        catch
        {
            // An unreachable bridge shouldn't read as "this isn't a bridge" — there's just nothing to offer.
            return new BridgedDeviceListMessage { Supported = true };
        }
    }

    static RemoteTemplateMessage ToProto(RemoteTemplateSpec t)
    {
        var m = new RemoteTemplateMessage
        {
            Id = t.Id, Name = t.Name, Description = t.Description, Icon = t.Icon,
            Category = t.Category, Brand = t.Brand, Width = t.Width, Height = t.Height,
        };
        foreach (var e in t.Elements)
        {
            var el = new RemoteElementMessage
            {
                Kind = e.Kind, Shape = e.Shape, X = e.X, Y = e.Y, W = e.W, H = e.H,
                Capability = e.Capability, Label = e.Label, Icon = e.Icon, Fill = e.Fill,
                Variant = e.Variant, Plus = e.Plus, Minus = e.Minus, FontSize = e.FontSize,
            };
            foreach (var kv in e.Args ?? new Dictionary<string, string>()) el.Args[kv.Key] = kv.Value;
            m.Elements.Add(el);
        }
        return m;
    }

    /// <summary>
    /// The remote this one device draws (<see cref="IRemoteSurfaceDevice"/>), or its refusal to.
    /// <para>
    /// Every way of not having one lands on the same answer — the device doesn't implement it, it answered
    /// null, or it threw reaching hardware that isn't there. The hub falls back to a template either way,
    /// and a driver that can't currently draw its remote must never leave a device with no remote at all.
    /// </para>
    /// </summary>
    public override async Task<DeviceRemoteMessage> GetRemote(DeviceRef request, ServerCallContext context)
    {
        if (!_devices.TryGetValue(request.DeviceId, out var device) || device is not IRemoteSurfaceDevice source)
            return new DeviceRemoteMessage { Supported = false };
        try
        {
            return await source.GetRemoteAsync(Token(context)) is { } spec
                ? new DeviceRemoteMessage { Supported = true, Remote = ToProto(spec) }
                : new DeviceRemoteMessage { Supported = false };
        }
        catch
        {
            return new DeviceRemoteMessage { Supported = false };
        }
    }

    public override async Task<CreateDeviceResponse> CreateDevice(CreateDeviceRequest request, ServerCallContext context)
    {
        try
        {
            var config = new Dictionary<string, string>(request.Config);

            // Mark this device's secrets so diagnostics can never surface them — the values matched against
            // every captured record, redacted before it leaves the process. Type-declared secrets, plus
            // anything whose key reads like a credential, since a driver may have typed a token as a string.
            foreach (var field in _driver.ConfigSchema)
                if ((field.Type == "secret" || LooksSecret(field.Key)) && config.TryGetValue(field.Key, out var value))
                    Diag.RegisterSecret(value);

            var device = await _driver.CreateDeviceAsync(request.DeviceId, request.Name, config, Token(context));
            var deviceId = request.DeviceId;
            device.EventRaised += e => Publish(deviceId, e);
            _devices[deviceId] = device;

            var resp = new CreateDeviceResponse { Ok = true };
            var commands = device.Commands.Select(ToProto).ToList();
            resp.Commands.AddRange(commands);
            _sentCommands[deviceId] = Signature(commands);

            // A device that already knows what it is says so here, so the common case costs no event.
            resp.Traits.AddRange(device.Traits);
            _sentTraits[deviceId] = TraitSignature(device.Traits);

            // What it can be handed to play, if anything. Absent means it can't be — which is the answer
            // for most devices and has to be distinguishable from "didn't say".
            if (device.Playback is { } playback)
            {
                resp.Playback = new MediaPlaybackInfo
                {
                    CommandId = playback.CommandId,
                    UrlParam = playback.UrlParam,
                };
                if (playback.Kinds is { Count: > 0 }) resp.Playback.Kinds.AddRange(playback.Kinds);
            }

            return resp;
        }
        catch (Exception ex)
        {
            return new CreateDeviceResponse { Ok = false, Error = ex.Message };
        }
    }

    public override async Task<ExecuteCommandResponse> ExecuteCommand(ExecuteCommandRequest request, ServerCallContext context)
    {
        if (!_devices.TryGetValue(request.DeviceId, out var device))
            return new ExecuteCommandResponse { Ok = false, Error = "Unknown device." };

        try
        {
            var args = new Dictionary<string, string>(request.Args);
            var result = await device.ExecuteAsync(request.CommandId, args, Token(context));
            var resp = new ExecuteCommandResponse { Ok = result.Ok, Error = result.Error ?? "" };
            Fill(resp.Result, result.Result);
            return resp;
        }
        catch (Exception ex)
        {
            return new ExecuteCommandResponse { Ok = false, Error = ex.Message };
        }
    }

    public override Task<DeviceStateMessage> GetState(DeviceRef request, ServerCallContext context)
    {
        var msg = new DeviceStateMessage();
        if (_devices.TryGetValue(request.DeviceId, out var device))
        {
            msg.Online = device.Online;
            Fill(msg.State, device.GetState());

            // Some devices don't know what they can do until they've talked to their hardware: a Hubitat
            // child learns its commands from the hub, and at CreateDevice it has none. The hub took that
            // first answer as final, so those devices sat there with an empty command list forever. Tell
            // it whenever the list changes — and only then, since state is refreshed on every event.
            var commands = device.Commands.Select(ToProto).ToList();
            var signature = Signature(commands);
            if (_sentCommands.GetValueOrDefault(request.DeviceId) != signature)
            {
                _sentCommands[request.DeviceId] = signature;
                msg.CommandsChanged = true;
                msg.Commands.AddRange(commands);
            }

            // Same story for what the device is: a bridge's child only knows once it has read itself.
            var traits = device.Traits;
            var traitSig = TraitSignature(traits);
            if (_sentTraits.GetValueOrDefault(request.DeviceId) != traitSig)
            {
                _sentTraits[request.DeviceId] = traitSig;
                msg.TraitsChanged = true;
                msg.Traits.AddRange(traits);
            }
        }
        return Task.FromResult(msg);
    }

    // --- Diagnostics ---------------------------------------------------------------------------------------

    public override Task<SetDiagnosticsResponse> SetDiagnostics(SetDiagnosticsRequest request, ServerCallContext context)
    {
        if (request.Everything)
        {
            // The process-wide switch, not a loop over the devices we happen to hold: a driver mid-restart
            // has none yet, and the traffic worth seeing is exactly the traffic that happens before it does.
            Diag.Everything = request.Enabled;
        }
        else if (request.DeviceId.Length == 0)
        {
            foreach (var id in _devices.Keys) Diag.SetEnabled(id, request.Enabled);
        }
        else
        {
            Diag.SetEnabled(request.DeviceId, request.Enabled);
        }

        return Task.FromResult(new SetDiagnosticsResponse { Ok = true });
    }

    public override Task<DiagnosticsMessage> GetDiagnostics(GetDiagnosticsRequest request, ServerCallContext context)
    {
        var msg = new DiagnosticsMessage
        {
            Enabled = Diag.Everything
                || (request.DeviceId.Length == 0 ? _devices.Keys.Any(Diag.Enabled) : Diag.Enabled(request.DeviceId)),
        };
        foreach (var r in Diag.Since(request.DeviceId, request.AfterSeq))
            msg.Records.Add(new DiagnosticRecord
            {
                Seq = r.Seq,
                TimestampUnixMs = r.TsMs,
                DeviceId = r.DeviceId,
                Transport = r.Transport,
                Direction = r.Direction,
                Text = r.Text,
                Detail = r.Detail,
                Endpoint = r.Endpoint,
                Hex = r.Hex,
            });
        return Task.FromResult(msg);
    }

    static readonly string[] SecretHints =
        ["key", "password", "passwd", "secret", "token", "psk", "pin", "credential", "auth"];

    static bool LooksSecret(string key) =>
        SecretHints.Any(h => key.Contains(h, StringComparison.OrdinalIgnoreCase));

    /// <summary>What we last told the hub each device could do, so we only speak up when that changes.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _sentCommands = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _sentTraits = new();

    static string TraitSignature(IReadOnlyList<string> traits)
        => string.Join(",", traits.OrderBy(t => t, StringComparer.Ordinal));

    /// <summary>Cheap identity for a command list — ids and parameter names, which is what the hub renders.</summary>
    static string Signature(IEnumerable<CommandDescriptor> commands) =>
        string.Join(";", commands.Select(c => $"{c.Id}:{string.Join(",", c.Parameters.Select(p => p.Key))}"));

    /// <summary>
    /// How often the heartbeat frame goes out after the first. Matched to the hub sampler's own 2 s cadence
    /// so the System page never draws a driver figure more than one tick staler than the hub's own.
    /// <para>
    /// Settable so a test can push it out of the way entirely and still see the first frame — which is what
    /// makes "the first one does not wait" assertable without a stopwatch, and a stopwatch is what would
    /// make that test flake on a loaded machine.
    /// </para>
    /// </summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(2);

    public override async Task StreamEvents(StreamEventsRequest request, IServerStreamWriter<DeviceEventMessage> responseStream, ServerCallContext context)
    {
        var ct = Token(context);

        // The heartbeat lives for exactly as long as the stream does. It writes into the same channel as
        // real events rather than to the response stream directly, so there is one writer and the ordering
        // is the channel's problem rather than a lock's — and so nothing accumulates while no hub is
        // connected, because the loop only runs inside a call.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var beating = Task.Run(() => BeatAsync(stop.Token), CancellationToken.None);

        try
        {
            await foreach (var evt in _events.Reader.ReadAllAsync(ct))
                await responseStream.WriteAsync(evt);
        }
        catch (OperationCanceledException) { /* hub disconnected */ }
        finally
        {
            stop.Cancel();
            try { await beating; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// The heartbeat itself. <b>The first frame goes out immediately</b>, before any wait — that is what
    /// lets the hub tell a driver too old to answer from a new one that simply has not ticked yet. Without
    /// it, "no sample" would mean "old driver" and "started less than an interval ago" at the same time,
    /// and the whole point of this frame is that those are different statements.
    /// </summary>
    async Task BeatAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _events.Writer.TryWrite(DriverRuntime.Frame());
                await Task.Delay(HeartbeatInterval, ct);
            }
        }
        catch (OperationCanceledException) { /* the stream closed */ }
    }

    public override async Task<DisposeResponse> DisposeDevice(DeviceRef request, ServerCallContext context)
    {
        if (_devices.TryRemove(request.DeviceId, out var device))
            await device.DisposeAsync();
        return new DisposeResponse { Ok = true };
    }

    // ---- Navigation ----

    INavigableDevice? Nav(string deviceId)
        => _devices.TryGetValue(deviceId, out var d) ? d as INavigableDevice : null;

    public override async Task<NodeListingMessage> Browse(BrowseRequest request, ServerCallContext context)
    {
        var nav = Nav(request.DeviceId) ?? throw new RpcException(new Status(StatusCode.Unimplemented, "Device is not navigable."));
        var opts = new BrowseOptions(request.Offset, request.Limit <= 0 ? 100 : request.Limit,
            string.IsNullOrEmpty(request.SortBy) ? null : request.SortBy,
            string.IsNullOrEmpty(request.Filter) ? null : request.Filter);
        var listing = await nav.BrowseAsync(string.IsNullOrEmpty(request.NodeId) ? null : request.NodeId, opts, Token(context));
        return ToProto(listing);
    }

    public override async Task<LibraryNodeMessage> GetNode(NodeRefMessage request, ServerCallContext context)
    {
        var nav = Nav(request.DeviceId) ?? throw new RpcException(new Status(StatusCode.Unimplemented, "Device is not navigable."));
        var node = await nav.GetNodeAsync(request.NodeId, Token(context))
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Unknown node."));
        return ToProto(node);
    }

    public override async Task<NodeListingMessage> SearchNodes(SearchNodesRequest request, ServerCallContext context)
    {
        var nav = Nav(request.DeviceId) ?? throw new RpcException(new Status(StatusCode.Unimplemented, "Device is not navigable."));
        var opts = new BrowseOptions(request.Offset, request.Limit <= 0 ? 100 : request.Limit);
        var listing = await nav.SearchAsync(request.Query, opts, Token(context));
        return ToProto(listing);
    }

    public override async Task<ExecuteCommandResponse> InvokeItem(InvokeItemRequest request, ServerCallContext context)
    {
        var nav = Nav(request.DeviceId);
        if (nav is null) return new ExecuteCommandResponse { Ok = false, Error = "Device is not navigable." };
        try
        {
            var args = new Dictionary<string, string>(request.Args);
            var result = await nav.InvokeItemAsync(request.NodeId, request.CommandId, args, Token(context));
            var resp = new ExecuteCommandResponse { Ok = result.Ok, Error = result.Error ?? "" };
            Fill(resp.Result, result.Result);
            return resp;
        }
        catch (Exception ex)
        {
            return new ExecuteCommandResponse { Ok = false, Error = ex.Message };
        }
    }

    static NodeListingMessage ToProto(NodeListing listing)
    {
        var msg = new NodeListingMessage { Total = listing.Total, Shape = listing.Shape, Size = listing.Size };
        if (listing.Node is not null) msg.Node = ToProto(listing.Node);
        msg.Items.AddRange(listing.Items.Select(ToProto));
        return msg;
    }

    static LibraryNodeMessage ToProto(LibraryNode n)
    {
        var msg = new LibraryNodeMessage
        {
            Id = n.Id,
            ParentId = n.ParentId ?? "",
            Kind = n.Kind,
            Title = n.Title,
            Subtitle = n.Subtitle ?? "",
            Overview = n.Overview ?? "",
            IsContainer = n.IsContainer,
            IsPlayable = n.IsPlayable,
            ChildCount = n.ChildCount ?? 0,
            HasChildCount = n.ChildCount.HasValue,
            Shape = n.Shape,
            Size = n.Size,
            Group = n.Group
        };
        Fill(msg.Metadata, n.Metadata);
        msg.Images.AddRange(n.Images.Select(i => new ImageRefMessage
        {
            Kind = i.Kind, Url = i.Url, Width = i.Width, Height = i.Height, BlurHash = i.BlurHash ?? "", Aspect = i.Aspect
        }));
        foreach (var c in n.Commands)
        {
            var cm = new ItemCommandMessage { Id = c.Id, Label = c.Label, Kind = c.Kind };
            if (c.Params is not null) cm.Params.AddRange(c.Params.Select(ToProto));
            msg.Commands.Add(cm);
        }
        return msg;
    }

    void Publish(string deviceId, DeviceEvent e)
    {
        var msg = new DeviceEventMessage
        {
            DeviceId = deviceId,
            Type = e.Type,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        Fill(msg.Data, e.Data);
        _events.Writer.TryWrite(msg);
    }

    static void Fill(MapField<string, string> map, IReadOnlyDictionary<string, string>? src)
    {
        if (src is null) return;
        foreach (var kv in src) map[kv.Key] = kv.Value;
    }

    static Remaestro.Grpc.ConfigField ToProto(ConfigField f)
    {
        var m = new Remaestro.Grpc.ConfigField
        {
            Key = f.Key,
            Label = f.Label,
            Type = f.Type,
            Required = f.Required,
            DefaultValue = f.Default ?? "",
            Help = f.Help ?? "",
            OptionsKey = f.OptionsKey ?? "",
            // Strings, so "no range" stays distinguishable from a range that starts at zero.
            Min = f.Min?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            Max = f.Max?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            Advanced = f.Advanced,
            Managed = f.Managed,
            ShowWhen = f.ShowWhen ?? "",
        };
        if (f.Options is not null) m.Options.AddRange(f.Options.Select(ToProto));
        return m;
    }

    static FieldOptionMessage ToProto(FieldOption o) => new()
    {
        Value = o.Value,
        Label = string.IsNullOrWhiteSpace(o.Label) ? o.Value : o.Label,
        Detail = o.Detail,
        Current = o.Current,
    };

    static Remaestro.Grpc.CommandDescriptor ToProto(CommandInfo c)
    {
        var d = new Remaestro.Grpc.CommandDescriptor { Id = c.Id, Label = c.Label, Description = c.Description ?? "" };
        if (c.Parameters is not null) d.Parameters.AddRange(c.Parameters.Select(ToProto));
        return d;
    }

    static Remaestro.Grpc.EventDescriptor ToProto(EventSchema e)
    {
        var d = new Remaestro.Grpc.EventDescriptor { Type = e.Type, Description = e.Description ?? "", HasExtraData = e.HasExtraData };
        if (e.Fields is not null)
            d.Fields.AddRange(e.Fields.Select(f => new Remaestro.Grpc.EventField { Key = f.Key, Type = f.Type, Description = f.Description ?? "" }));
        return d;
    }

    static Remaestro.Grpc.StateField ToProto(StateField f)
        => new() { Key = f.Key, Type = f.Type, Description = f.Description ?? "" };
}
