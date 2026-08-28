using System.Collections.Concurrent;
using System.Net.Sockets;
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

    /// <summary>
    /// What the hub said about itself on the last <c>Describe</c>, or 0 if it has not asked yet or is older
    /// than negotiation. Exposed so a driver — or a test — can see the other end's version rather than
    /// assume it.
    /// </summary>
    public uint HubProtocol { get; private set; }

    public override Task<DriverDescriptor> Describe(DescribeRequest request, ServerCallContext context)
    {
        // Recorded and never refused. A driver that throws here is a driver the hub cannot name in the
        // sentence it puts in front of a person — refusing is the hub's job because the hub has the screen.
        HubProtocol = request.HubProtocol;

        var d = new DriverDescriptor
        {
            TypeId = _driver.TypeId,
            DisplayName = _driver.DisplayName,
            Description = _driver.Description,
            SupportsNavigation = _driver.SupportsNavigation,
            SupportsEpg = _driver.SupportsEpg,
            SupportsDeviceRemotes = _driver.SupportsDeviceRemotes,
            ProtocolVersion = DriverProtocol.Current,
            Traits = { _driver.Traits }
        };
        if (_driver.MinHubProtocol is { } floor) d.MinHubProtocol = floor;
        d.Capabilities.AddRange(Capabilities(_driver));
        d.ConfigSchema.AddRange(_driver.ConfigSchema.Select(ToProto));
        d.Commands.AddRange(_driver.Commands.Select(ToProto));
        d.Events.AddRange(_driver.Events.Select(ToProto));
        d.StateSchema.AddRange(_driver.StateSchema.Select(ToProto));
        d.DiscoveryServices.AddRange(_driver.DiscoveryServices);
        d.RemoteTemplates.AddRange(_driver.RemoteTemplates.Select(ToProto));
        d.MediaTypes.AddRange(_driver.MediaTypes.Select(ToProto));
        d.AssistantTools.AddRange(_driver.AssistantTools.Select(ToProto));
        d.SettingsSchema.AddRange(_driver.SettingsSchema.Select(ToProto));
        return Task.FromResult(d);
    }

    /// <summary>
    /// What goes in <c>DriverDescriptor.capabilities</c>: whatever the driver declared, plus the three
    /// <c>Supports*</c> booleans folded in.
    /// <para>
    /// <b>The fold is what makes the list authoritative.</b> The hub's reading rule is that a non-empty list
    /// is the complete answer and the booleans are only consulted when it is empty — so a driver that set
    /// <c>SupportsNavigation</c> and then declared one unrelated capability would otherwise have silently
    /// un-declared its navigation. Doing it here means no existing driver has to be touched and none can
    /// make that mistake.
    /// </para>
    /// <para>Ordinal-distinct and in a stable order, because this string list is hashed into a descriptor
    /// cache key and a set that reorders itself would invalidate it on every start.</para>
    /// </summary>
    public static IReadOnlyList<string> Capabilities(IRemaestroDriver driver)
    {
        var declared = new List<string>(driver.Capabilities);
        if (driver.SupportsNavigation) declared.Add(DriverCapability.Navigation);
        if (driver.SupportsEpg) declared.Add(DriverCapability.Epg);
        if (driver.SupportsDeviceRemotes) declared.Add(DriverCapability.DeviceRemotes);
        return declared.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>The device's live input list, when it knows one (<see cref="IInputSourceDevice"/>).</summary>
    public override async Task<InputListMessage> ListInputs(DeviceRef request, ServerCallContext context)
    {
        if (!_devices.TryGetValue(request.DeviceId, out var device))
            return new InputListMessage { Supported = false, Availability = Availability.UnknownDevice };
        if (device is not IInputSourceDevice src)
            return new InputListMessage { Supported = false, Availability = Availability.Unsupported };
        try
        {
            var inputs = await src.ListInputsAsync(Token(context));
            var msg = new InputListMessage { Supported = true, Availability = Availability.Answered };
            msg.Inputs.AddRange(inputs.Select(i => new InputSourceMessage
            {
                Id = i.Id, Label = i.Label, Detail = i.Detail, Current = i.Current,
            }));
            return msg;
        }
        catch
        {
            // A device that can't be reached right now shouldn't break the picker — fall back to statics.
            // `supported` stays false so an older hub falls back exactly as it always did; `availability`
            // says which of the three "no"s this was, which is the thing that could not be said before.
            return new InputListMessage { Supported = false, Availability = Availability.Unavailable };
        }
    }

    /// <summary>The device's guide over the asked window, when it's a source (<see cref="IEpgSource"/>).</summary>
    public override async Task<EpgMessage> GetEpg(EpgRequest request, ServerCallContext context)
    {
        if (!_devices.TryGetValue(request.DeviceId, out var device))
            return new EpgMessage { Supported = false, Availability = Availability.UnknownDevice };
        if (device is not IEpgSource src)
            return new EpgMessage { Supported = false, Availability = Availability.Unsupported };
        try
        {
            var from = DateTimeOffset.FromUnixTimeSeconds(request.FromUnix);
            var to = DateTimeOffset.FromUnixTimeSeconds(request.ToUnix);
            var data = await src.GetEpgAsync(from, to, Token(context));

            // The page of the line-up this hub asked for, and then only the programmes belonging to it.
            //
            // Applied here rather than in any guide source, for the same reason the diagnostics cap is:
            // all three sources build a full line-up and hand it over, so one slice on this line bounds
            // every one of them and none of them changes. `EpgRequest.offset`/`limit` in the proto carries
            // the measurements — ~400 channels with listings behind them encode to 6,616,574 bytes, and a
            // line-up goes over on its channels alone at about 27,600 of them.
            //
            // **Filtering the programmes to the page is not tidiness, it is the point.** Sending the whole
            // guide alongside a page of channels would leave the answer exactly as big as it was, and the
            // programmes are the larger half on a feed with synopses in it.
            //
            // **And the order the page is cut out of is settled here too**, which is the other thing one
            // line on this method buys every driver at once. `offset` addresses "the source's own order",
            // so what that order *is* decides which rows a hub draws at row 500 — and no guide source in
            // this fleet sorts: each projects its upstream's order straight through, an XMLTV document's
            // order or whatever an Xtream panel happened to return. Sorting by channel number here makes
            // every .NET driver ship the order a person expects, with no change to any of them.
            //
            // A plugin in another language that does not sort is *self-consistent* rather than wrong: its
            // section is in its own order, `offset` addresses it exactly, and no row is duplicated or
            // missing. See `EpgChannelOrder` for what "channel number" means, and note that the hub does
            // not sort and cannot check.
            var ordered = EpgChannelOrder.Sorted(data.Channels);

            var channels = (IEnumerable<EpgChannel>)ordered;
            if (request.Offset > 0) channels = channels.Skip(request.Offset);
            if (request.Limit > 0) channels = channels.Take(request.Limit);
            var page = channels.ToList();

            var programmes = (IEnumerable<EpgProgramme>)data.Programmes;
            if (request.Offset > 0 || request.Limit > 0)
            {
                var ids = page.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
                programmes = programmes.Where(p => ids.Contains(p.ChannelId));
            }

            // How tall the whole selection is, so a hub drawing forty rows of it can size a scrollbar
            // without asking for the other twenty-seven thousand. It is the count of everything this
            // device offers, not of the page — `total_channels` in the proto says why, and says what a
            // driver that leaves it at 0 costs itself.
            var msg = new EpgMessage
            {
                Supported = true, Availability = Availability.Answered, TotalChannels = ordered.Count,
            };
            msg.Channels.AddRange(page.Select(c => new EpgChannelMessage
            {
                Id = c.Id, Name = c.Name, Logo = c.Logo ?? "", Number = c.Number ?? "", StreamUrl = c.StreamUrl ?? "",
            }));
            msg.Programmes.AddRange(programmes.Select(p => new EpgProgrammeMessage
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
            // Unavailable rather than unsupported: nothing has been learned about whether it is a guide
            // source, so "this device has no guide" is not a conclusion anyone may draw or cache.
            return new EpgMessage { Supported = false, Availability = Availability.Unavailable };
        }
    }

    /// <summary>The device's live app list, when it knows one (<see cref="IAppListDevice"/>).</summary>
    public override async Task<AppListMessage> ListApps(DeviceRef request, ServerCallContext context)
    {
        if (!_devices.TryGetValue(request.DeviceId, out var device))
            return new AppListMessage { Supported = false, Availability = Availability.UnknownDevice };
        if (device is not IAppListDevice src)
            return new AppListMessage { Supported = false, Availability = Availability.Unsupported };
        try
        {
            var apps = await src.ListAppsAsync(Token(context));
            var msg = new AppListMessage { Supported = true, Availability = Availability.Answered };
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
            return new AppListMessage { Supported = false, Availability = Availability.Unavailable };
        }
    }

    /// <summary>
    /// The values a parameter will take, asked live (<see cref="IOptionSourceDevice"/>). A device that
    /// can't answer for this key reports unsupported, and the caller falls back to free text.
    /// </summary>
    public override async Task<OptionsListMessage> ListOptions(OptionsRequest request, ServerCallContext context)
    {
        if (!_devices.TryGetValue(request.DeviceId, out var device))
            return new OptionsListMessage { Supported = false, Availability = Availability.UnknownDevice };
        if (device is not IOptionSourceDevice source)
            return new OptionsListMessage { Supported = false, Availability = Availability.Unsupported };
        try
        {
            var options = await source.ListOptionsAsync(request.OptionsKey, Token(context));
            var msg = new OptionsListMessage { Supported = true, Availability = Availability.Answered };
            msg.Options.AddRange(options.Select(ToProto));
            return msg;
        }
        catch
        {
            // A device that can't be reached shouldn't break the picker — fall back to typing a value.
            return new OptionsListMessage { Supported = false, Availability = Availability.Unavailable };
        }
    }

    /// <summary>
    /// Run one of the tools this driver declared — see
    /// <see cref="IRemaestroDriver.RunAssistantToolAsync"/>, where the contract lives.
    ///
    /// <para>
    /// <b>Null becomes UNIMPLEMENTED, on purpose.</b> A driver that does not override the method and a
    /// driver built before this rpc existed are the same fact — the plugin declares a tool and this build
    /// of it cannot run one — so they had better be the same answer on the wire. The alternative is a
    /// silent empty result that the hub cannot tell from a tool that genuinely had nothing to say.
    /// </para>
    /// <para>
    /// <b>A throw is not.</b> An exception is a tool that failed, which is an ordinary thing for a tool to
    /// do and is not the same as a driver that has no tools — so it comes back as an answer with
    /// <c>ok = false</c>, and the model is told what happened rather than being handed a dead call.
    /// </para>
    /// </summary>
    public override async Task<AssistantToolResultMessage> InvokeAssistantTool(
        AssistantToolCallRequest request, ServerCallContext context)
    {
        AssistantToolAnswer? answer;
        try
        {
            answer = await _driver.RunAssistantToolAsync(
                request.ToolId, new Dictionary<string, string>(request.Args), request.Surface, Token(context));
        }
        catch (OperationCanceledException)
        {
            // The hub gave up waiting and cancelled. Saying anything here would be answering a question
            // nobody is still listening for, and the hub has its own sentence for a call that ran out of
            // time — one that reads as a fact about the plugin rather than as an answer from it.
            throw;
        }
        catch (Exception ex)
        {
            return new AssistantToolResultMessage
            {
                Ok = false,
                Text = $"That didn't work: {ex.Message}",
                Error = ex.ToString(),
            };
        }

        if (answer is null)
            throw new RpcException(new Status(StatusCode.Unimplemented,
                $"This driver declares assistant tools but does not run them ({request.ToolId})."));

        return new AssistantToolResultMessage
        {
            Ok = answer.Ok,
            Text = answer.Text ?? "",
            Error = answer.Error ?? "",
        };
    }

    /// <summary>
    /// One person's plugin settings, handed to the driver.
    ///
    /// <para>
    /// <b>Null is UNIMPLEMENTED and a throw is a refusal</b> — the same split
    /// <see cref="InvokeAssistantTool"/> makes, for the same reason. A driver that does not take settings
    /// at all is a fact about the driver and the hub says so once on a page; a driver that took them and
    /// disliked them is an ordinary outcome with a sentence attached, and turning that into a dead call
    /// would lose the sentence.
    /// </para>
    /// </summary>
    public override async Task<PluginSettingsAck> ApplyPluginSettings(
        PluginSettingsMessage request, ServerCallContext context)
    {
        PluginSettingsOutcome? outcome;
        try
        {
            outcome = await _driver.ApplyPluginSettingsAsync(
                new PluginUser(request.UserId, request.UserDisplayName),
                new Dictionary<string, string>(request.Values),
                Token(context));
        }
        catch (OperationCanceledException)
        {
            // The hub stopped waiting. Answering now would be replying to a question nobody is listening
            // for, and the hub has its own wording for a call that ran out of time.
            throw;
        }
        catch (Exception ex)
        {
            return new PluginSettingsAck { Ok = false, Error = $"That didn't work: {ex.Message}" };
        }

        if (outcome is null)
            throw new RpcException(new Status(StatusCode.Unimplemented,
                "This driver declares plugin settings but does not take them."));

        return new PluginSettingsAck { Ok = outcome.Ok, Error = outcome.Text ?? "" };
    }

    /// <summary>What sits behind this device, when it fronts a bridge (<see cref="IBridgeDevice"/>).</summary>
    public override async Task<BridgedDeviceListMessage> ListBridgedDevices(DeviceRef request, ServerCallContext context)
    {
        if (!_devices.TryGetValue(request.DeviceId, out var device))
            return new BridgedDeviceListMessage { Supported = false, Availability = Availability.UnknownDevice };
        if (device is not IBridgeDevice bridge)
            return new BridgedDeviceListMessage { Supported = false, Availability = Availability.Unsupported };
        try
        {
            var found = await bridge.ListBridgedDevicesAsync(Token(context));
            var msg = new BridgedDeviceListMessage { Supported = true, Availability = Availability.Answered };
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
            // That comment was the bug report for the other five, and this is now the whole class: every one
            // of them answers Unavailable here, and only this one still needs `supported = true` to carry the
            // distinction to a hub too old to read the enum.
            return new BridgedDeviceListMessage { Supported = true, Availability = Availability.Unavailable };
        }
    }

    /// <summary>
    /// A declared media type, sent verbatim. Nothing is validated here on purpose: the hub is the party
    /// that has to refuse a bad <c>PlaysAs</c> or a redefined <c>NavKind</c>, because a plugin written in
    /// any other language never passes through this method and the two ends must refuse identically.
    /// </summary>
    static MediaTypeMessage ToProto(MediaTypeSpec t)
    {
        var m = new MediaTypeMessage
        {
            Kind = t.Kind, Label = t.Label, LabelPlural = t.LabelPlural,
            Icon = t.Icon, Shape = t.Shape, PlaysAs = t.PlaysAs,
        };
        if (t.Facts is not null) m.Facts.AddRange(t.Facts.Select(ToProto));
        return m;
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
        if (!_devices.TryGetValue(request.DeviceId, out var device))
            return new DeviceRemoteMessage { Supported = false, Availability = Availability.UnknownDevice };
        if (device is not IRemoteSurfaceDevice source)
            return new DeviceRemoteMessage { Supported = false, Availability = Availability.Unsupported };
        try
        {
            // A null from a device that *is* a remote surface is an answer — "not this unit" — and not a
            // failure, so it reads Unsupported rather than Unavailable. The throw below is the other one.
            return await source.GetRemoteAsync(Token(context)) is { } spec
                ? new DeviceRemoteMessage { Supported = true, Availability = Availability.Answered, Remote = ToProto(spec) }
                : new DeviceRemoteMessage { Supported = false, Availability = Availability.Unsupported };
        }
        catch
        {
            return new DeviceRemoteMessage { Supported = false, Availability = Availability.Unavailable };
        }
    }

    public override async Task<CreateDeviceResponse> CreateDevice(CreateDeviceRequest request, ServerCallContext context)
    {
        try
        {
            var config = new Dictionary<string, string>(request.Config);

            // Mark this device's secrets so diagnostics can never surface them — the values matched against
            // every captured record, redacted before it leaves the process.
            //
            // Three ways a field gets here, and the order is the contract. A declared Sensitivity wins
            // outright, in both directions: SENSITIVE and WRITE_ONLY are registered whatever the widget
            // says, and NORMAL is a driver stating that `publicKey` is not a credential, which switches the
            // guesswork off for that field alone. Only a field that declared nothing falls through to what
            // this did before — the type string, then the key reading like a credential — because a driver
            // written before Sensitivity existed still types a token as a string, and narrowing redaction
            // is not a thing to do quietly.
            foreach (var field in _driver.ConfigSchema)
                if (RegistersAsSecret(field) && config.TryGetValue(field.Key, out var value))
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

    /// <summary>
    /// The captured traffic newer than the caller's watermark, at most <c>limit</c> records of it.
    /// <para>
    /// <b>The cap is honoured here rather than in any driver, and that is the whole leverage.</b> Every
    /// driver's capture goes into the one <see cref="Diag"/> buffer, so a single <c>Take</c> on this line
    /// bounds the answer for all of them and no driver author writes anything. See
    /// <c>GetDiagnosticsRequest.limit</c> in the proto for what it costs not to: a full buffer encodes to
    /// 4,340,002 bytes against the 4,194,304 a stock channel receives, so the shipped caps are already over
    /// the limit — and because this rpc is unary, an answer that cannot be received is the whole answer
    /// rather than one frame of it.
    /// </para>
    /// <para>
    /// A hub older than the field sends 0 and gets everything, exactly as before.
    /// </para>
    /// </summary>
    public override Task<DiagnosticsMessage> GetDiagnostics(GetDiagnosticsRequest request, ServerCallContext context)
    {
        var msg = new DiagnosticsMessage
        {
            Enabled = Diag.Everything
                || (request.DeviceId.Length == 0 ? _devices.Keys.Any(Diag.Enabled) : Diag.Enabled(request.DeviceId)),
        };

        IEnumerable<Diag.Entry> held = Diag.Since(request.DeviceId, request.AfterSeq);
        if (request.Limit > 0) held = held.Take(request.Limit);

        foreach (var r in held)
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

    /// <summary>
    /// Whether this field's value is registered with <see cref="Diag"/> for redaction.
    ///
    /// <para>
    /// <b>A declaration beats a guess, both ways round.</b> <see cref="FieldSensitivity.Normal"/> is the
    /// half that is easy to forget: it exists so a driver can say that <c>publicKey</c> or <c>authorName</c>
    /// is not a credential, and saying so has to actually switch <see cref="LooksSecret"/> off or it is a
    /// field that does nothing. The cost of getting that wrong is bounded — a value that should have been
    /// redacted appears in this driver's own wire capture — and it is the driver's own call to make.
    /// </para>
    /// <para>
    /// <see cref="FieldSensitivity.Unspecified"/> is <i>not</i> Normal, which is the whole reason the enum
    /// has four members. It means nobody said, so the guesswork stays exactly as it was: forty drivers were
    /// written before this field existed and several of them type a token as a <c>string</c>.
    /// </para>
    /// </summary>
    static bool RegistersAsSecret(ConfigField field) => field.Sensitivity switch
    {
        FieldSensitivity.Sensitive or FieldSensitivity.WriteOnly => true,
        FieldSensitivity.Normal => false,
        _ => field.Type == "secret" || LooksSecret(field.Key),
    };

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
    /// <remarks>
    /// <b>Whatever it is set to goes on the wire.</b> Every frame carries this interval, so a hub does not
    /// have to guess how long silence has to be to mean anything — which is what it was doing, against this
    /// class's own default, for every driver in every language.
    /// </remarks>
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
                _events.Writer.TryWrite(DriverRuntime.Frame(HeartbeatInterval, _driver.HeartbeatIndependent));
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
        try
        {
            var listing = await nav.BrowseAsync(string.IsNullOrEmpty(request.NodeId) ? null : request.NodeId, opts, Token(context));
            return ToProto(listing);
        }
        catch (Exception ex) when (NavFailure(ex, context) is { } fail) { throw fail; }
    }

    public override async Task<LibraryNodeMessage> GetNode(NodeRefMessage request, ServerCallContext context)
    {
        var nav = Nav(request.DeviceId) ?? throw new RpcException(new Status(StatusCode.Unimplemented, "Device is not navigable."));
        try
        {
            var node = await nav.GetNodeAsync(request.NodeId, Token(context))
                ?? throw new RpcException(new Status(StatusCode.NotFound, "Unknown node."));
            return ToProto(node);
        }
        catch (Exception ex) when (NavFailure(ex, context) is { } fail) { throw fail; }
    }

    public override async Task<NodeListingMessage> SearchNodes(SearchNodesRequest request, ServerCallContext context)
    {
        var nav = Nav(request.DeviceId) ?? throw new RpcException(new Status(StatusCode.Unimplemented, "Device is not navigable."));
        var opts = new BrowseOptions(request.Offset, request.Limit <= 0 ? 100 : request.Limit);
        try
        {
            var listing = await nav.SearchNodesAsync(request.Query, opts, Token(context));
            return ToProto(listing);
        }
        catch (Exception ex) when (NavFailure(ex, context) is { } fail) { throw fail; }
    }

    /// <summary>
    /// What a navigation rpc says when it could not be answered — and the reason the browse surface needs
    /// no new field on the wire.
    ///
    /// <para>
    /// <b>The failure is the error channel.</b> <see cref="NodeListingMessage"/> has nowhere to put "I
    /// couldn't ask", and a driver that swallows a connection failure to return an empty one hands the hub
    /// a fact about the library when all it has is a fact about the network. A failed rpc says the
    /// difference in a way every generated client already understands, and an old hub that reads none of
    /// this still gets a failure rather than a listing it would have believed.
    /// </para>
    ///
    /// <para>
    /// <b>Three outcomes, and the split is what makes it useful.</b>
    /// <see cref="DeviceUnreachableException"/> and the connection-shaped exceptions a plain HTTP client
    /// throws — a refused socket, a DNS miss, a timeout, a non-success status through
    /// <c>EnsureSuccessStatusCode</c> — become <c>UNAVAILABLE</c>, which the hub renders as "can't reach
    /// this source" rather than as a red band with a stack trace in it. Anything else is a bug in the
    /// driver and becomes <c>INTERNAL</c>. Both carry <see cref="Exception.Message"/>, which is the part
    /// that was missing entirely: gRPC's own default for an unhandled handler exception is the detail
    /// string <c>"Exception was thrown by handler."</c>, so before this every driver that threw honestly —
    /// Jellyfin, the reference implementation, among them — reached the screen saying nothing at all.
    /// </para>
    ///
    /// <para>
    /// Cancellation and an <see cref="RpcException"/> the handler raised itself pass through untouched;
    /// returning null from the filter is how they stay unhandled rather than being caught and rethrown.
    /// </para>
    /// </summary>
    static RpcException? NavFailure(Exception ex, ServerCallContext context) => ex switch
    {
        RpcException => null,
        OperationCanceledException when context.CancellationToken.IsCancellationRequested => null,
        DeviceUnreachableException or HttpRequestException or SocketException or IOException
            or TimeoutException or TaskCanceledException
            => new RpcException(new Status(StatusCode.Unavailable, Said(ex))),
        _ => new RpcException(new Status(StatusCode.Internal, Said(ex))),
    };

    /// <summary>The sentence a person will read. Never blank — an empty detail is what gRPC already gives.</summary>
    static string Said(Exception ex) => ex.Message is { Length: > 0 } m ? m : ex.GetType().Name;

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
            var cm = new ItemCommandMessage { Id = c.Id, Label = c.Label, Kind = c.Kind, Icon = c.Icon };
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

        // A hold is raised through the same string-keyed channel every other event uses — that is the only
        // channel a device has — and is lifted back into its typed message here, so nothing string-shaped
        // reaches the hub and the keys stay an implementation detail of this SDK. See DeviceBase.Hold.
        if (e.Type == DeviceEvents.DriverHold && e.Data is { } hold)
        {
            msg.Hold = ToHold(deviceId, hold);
            _events.Writer.TryWrite(msg);
            return;
        }

        Fill(msg.Data, e.Data);
        _events.Writer.TryWrite(msg);
    }

    /// <summary>
    /// One hold frame's payload. Public and static so the mapping — including a malformed <c>until</c>,
    /// which must read as "no horizon" rather than as an exception on the event path — is assertable
    /// without a stream.
    /// </summary>
    public static DriverHoldMessage ToHold(string deviceId, IReadOnlyDictionary<string, string> data)
    {
        var msg = new DriverHoldMessage
        {
            Id = data.GetValueOrDefault(DeviceEvents.HoldKeys.Id, ""),
            DeviceId = deviceId,
            Reason = data.GetValueOrDefault(DeviceEvents.HoldKeys.Reason, ""),
            Released = string.Equals(data.GetValueOrDefault(DeviceEvents.HoldKeys.Released), "true", StringComparison.OrdinalIgnoreCase),
        };

        // 0 is "the driver does not know when this ends", which is the honest answer to a pairing wait and
        // is also what an unparseable value has to become — an event raised from a device's own thread is
        // not a place to throw.
        if (long.TryParse(data.GetValueOrDefault(DeviceEvents.HoldKeys.UntilUnixMs), System.Globalization.CultureInfo.InvariantCulture, out var until) && until > 0)
            msg.UntilUnixMs = until;

        return msg;
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
            Sensitivity = (Remaestro.Grpc.Sensitivity)(int)f.Sensitivity,
        };
        if (f.Options is not null) m.Options.AddRange(f.Options.Select(ToProto));
        return m;
    }

    /// <summary>
    /// A declared assistant tool as it goes over the wire.
    /// <para>
    /// <b>The id is sent bare and the hub namespaces it.</b> Nothing here prefixes it with the type id,
    /// because two parties both prefixing would produce <c>lutron.lutron.scene_report</c> and one party
    /// deciding not to would produce a collision — so exactly one end owns it, and it is the end that knows
    /// every other plugin's name.
    /// </para>
    /// <para>
    /// Nothing here refuses an over-long description either. The hub is the party with a screen to explain a
    /// refusal on and a log to record it in, and it has to make that judgement about plugins written in
    /// languages this SDK will never see — so the check lives there, once, rather than here and there.
    /// </para>
    /// </summary>
    static Remaestro.Grpc.AssistantToolDescriptor ToProto(AssistantToolSpec t)
    {
        var m = new Remaestro.Grpc.AssistantToolDescriptor
        {
            Id = t.Id,
            Label = t.Label,
            Description = t.Description,
            Acts = t.Acts,
        };
        if (t.Surfaces is not null) m.Surfaces.AddRange(t.Surfaces);
        if (t.Parameters is not null) m.Parameters.AddRange(t.Parameters.Select(ToProto));
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
