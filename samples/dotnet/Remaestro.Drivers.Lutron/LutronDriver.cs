using Remaestro.Sdk;

namespace Remaestro.Drivers.Lutron;

/// <summary>
/// Lutron RadioRA 2, RA3 and HomeWorks QSX over the Integration Protocol on telnet port 23. Lights are
/// addressed by their integration id from the Lutron Designer project file, and the processor reports
/// every level change — including ones made at a keypad — so a dimmer moved by hand shows up here.
/// <para>
/// Caséta is deliberately not this driver: the Smart Bridge Pro speaks LEAP over TLS with a certificate
/// you have to pair for, which is a different job. This covers the processors that speak plain telnet.
/// </para>
/// </summary>
public sealed class LutronDriver : IRemaestroDriver
{
    public string TypeId => "lutron";
    public string DisplayName => "Lutron RadioRA 2 / HomeWorks";
    public string Description => "Lutron processors over the integration protocol. Set a light's level, raise and lower it, and see changes made at the keypads.";

    /// <summary>
    /// What this driver implements, declared rather than left to be discovered by calling — see
    /// <see cref="DriverCapability"/>. This one really does capture its conversation with the device, so it
    /// says so; a driver that answers SetDiagnostics with an empty buffer must not.
    /// </summary>
    public IReadOnlyList<string> Capabilities { get; } = [DriverCapability.Diagnostics];

    /// <summary>What this type is for, so a list can scope itself to the job. See <see cref="DeviceTrait"/>.</summary>
    public IReadOnlyList<string> Traits { get; } = [DeviceTrait.Lighting];

    public IReadOnlyList<ConfigField> ConfigSchema { get; } =
    [
        new("host", "Processor host / IP", Required: true, Help: "The main repeater or processor's LAN address"),
        new("username", "Username", Default: "lutron", Advanced: true, Help: "The integration login — lutron by default"),
        new("password", "Password", Type: "secret", Default: "integration", Advanced: true, Help: "integration by default"),
        new("port", "Port", Default: "23", Advanced: true, Help: "Telnet integration port"),
        new("integrationId", "Integration id", Managed: true,
            Help: "Set automatically when you pick a light off the processor — it's the id from your Lutron project file."),
        new("fadeSeconds", "Fade time", Type: "number", Default: "1", Advanced: true,
            Help: "Seconds a level change takes. 0 snaps instantly."),
    ];

    public IReadOnlyList<CommandInfo> Commands { get; } =
    [
        new("power_on", "On"), new("power_off", "Off"),
        new("set_brightness", "Set Brightness", "0–100",
            [new("brightness", "Brightness", Type: "number", Required: true, Default: "60", Min: 0, Max: 100)]),
        new("brightness_up", "Brighter"), new("brightness_down", "Dimmer"),
        new("refresh", "Refresh", "Ask the processor for the current level"),
        new("raw", "Raw command", "A Lutron integration command without its leading # or ?, e.g. OUTPUT,7,1,100",
            [new("command", "Command", Required: true)]),
    ];

    /// <summary>
    /// One tool, and it is the sample of the <i>acting</i> case — see <see cref="AssistantToolSpec"/>.
    ///
    /// <para>
    /// <b>Console only, and that is the default rather than a precaution taken here.</b> Writing
    /// <c>Surfaces: [AssistantSurface.Console]</c> beside <c>Acts: true</c> is what a driver author gets by
    /// writing the obvious thing; putting <see cref="AssistantSurface.Remote"/> in that list is the opt-in,
    /// and it would print on this plugin's page as a sentence saying anybody speaking in the house can
    /// trigger it.
    /// </para>
    /// <para>
    /// <b>Why this particular tool is the honest example.</b> Turning off every Lutron load at once is not
    /// something <c>do</c> can express — <c>do</c> names one capability on one device, so the same request
    /// through it is a dozen separate calls the model has to decide to make and get right. One command that
    /// darkens the house is worth declaring for exactly the reason it is a bad thing to have said out loud
    /// in a room by somebody who meant "turn this lamp off": the failure is a dark house, and the fix is
    /// walking to a keypad.
    /// </para>
    /// <para>
    /// <b>And the description says what it does rather than what would sound better.</b> It reaches the
    /// loads this hub has been told about, which is not necessarily every load on the processor — the
    /// integration protocol has no line meaning "everything", so a tool claiming one would be a claim its
    /// own code could not keep. A description is the only thing a model is told, and one that overstates is
    /// how a model comes to report something that did not happen.
    /// </para>
    /// </summary>
    public IReadOnlyList<AssistantToolSpec> AssistantTools { get; } =
    [
        new("all_lights_off", "Turn every light off",
            "Turn off every Lutron load this hub controls, in one go — not one room and not one light. Use "
            + "it only when somebody has asked for exactly that, and never as a way of turning off a light "
            + "you could address directly. It cannot reach loads that have not been added to the hub.",
            Surfaces: [AssistantSurface.Console],
            Acts: true,
            Parameters:
            [
                new("fadeSeconds", "Fade time", Type: "number", Default: "3", Min: 0, Max: 60,
                    Help: "Seconds the whole house takes to go dark. 0 snaps instantly."),
            ]),
    ];

    public IReadOnlyList<EventSchema> Events { get; } =
    [
        new("power.changed", "On or off changed", [new("power", "string", "on | off")]),
        new("brightness.changed", "Level changed", [new("brightness", "number")]),
    ];

    public IReadOnlyList<StateField> StateSchema { get; } =
    [
        new("online", "bool"), new("power", "string"), new("brightness", "number"),
        new("integrationId", "number"), new("lastError"),
    ];

    /// <summary>
    /// The loads this driver has been asked to create, so a tool that belongs to the <i>driver</i> has
    /// something to act on. A tool is declared per driver rather than per device, so it arrives with no
    /// device named — and the hub's own device list is private to <c>DriverServiceImpl</c>.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, LutronDevice> _loads = new();

    public Task<IRemaestroDevice> CreateDeviceAsync(string deviceId, string name, IReadOnlyDictionary<string, string> config, CancellationToken ct)
    {
        var host = config.GetValueOrDefault("host", "");
        if (host.Length == 0) throw new ArgumentException("A Lutron processor needs a host address.");
        var port = int.TryParse(config.GetValueOrDefault("port"), out var p) ? p : 23;
        var fade = double.TryParse(config.GetValueOrDefault("fadeSeconds"), System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 1;
        var device = new LutronDevice(
            deviceId, name, host, port,
            config.GetValueOrDefault("username", "lutron"),
            config.GetValueOrDefault("password", "integration"),
            config.GetValueOrDefault("integrationId", ""), fade, Commands);

        _loads[deviceId] = device;
        return Task.FromResult<IRemaestroDevice>(device);
    }

    /// <summary>
    /// The other half of the declaration above — the point at which a plugin's code runs because a model
    /// asked it to.
    ///
    /// <para>
    /// <b>It reports what happened rather than that it was attempted.</b> A tool that acts and answers
    /// "done" whatever the outcome teaches a model to say "done" to a person, and the person is standing in
    /// a room that is still lit. So the loads that refused are named and counted, and the answer is
    /// <c>ok: false</c> when none of them worked.
    /// </para>
    /// <para>
    /// <b>The surface is not checked here, and must not be.</b> The hub has already refused anything this
    /// tool did not declare itself for — a model naming it on the voice path never reaches this method — so
    /// a second check would be a second copy of a rule that lives in one place, and the copy is the one that
    /// would go stale.
    /// </para>
    /// </summary>
    public async Task<AssistantToolAnswer?> RunAssistantToolAsync(
        string toolId, IReadOnlyDictionary<string, string> args, string surface, CancellationToken ct)
    {
        if (toolId != "all_lights_off")
            return AssistantToolAnswer.Failed($"This plugin has no tool called '{toolId}'.");

        var loads = _loads.Values.Where(d => d.HasIntegrationId).ToList();
        if (loads.Count == 0)
            return AssistantToolAnswer.Failed("No Lutron loads have been added to this hub yet.");

        var fade = double.TryParse(args.GetValueOrDefault("fadeSeconds"),
            System.Globalization.CultureInfo.InvariantCulture, out var f) ? Math.Clamp(f, 0, 60) : 3;

        var refused = new List<string>();
        foreach (var load in loads)
        {
            var result = await load.OffAsync(fade, ct);
            if (!result.Ok) refused.Add(load.Name);
        }

        if (refused.Count == loads.Count)
            return AssistantToolAnswer.Failed(
                $"None of the {loads.Count} Lutron loads went off — the processor didn't take the command.");

        var done = loads.Count - refused.Count;
        return refused.Count == 0
            ? AssistantToolAnswer.Says($"All {done} Lutron loads are going off over {fade:0.##} seconds.")
            : AssistantToolAnswer.Says(
                $"{done} of {loads.Count} Lutron loads are going off over {fade:0.##} seconds. "
                + $"These didn't take it: {string.Join(", ", refused)}.");
    }
}

internal sealed class LutronDevice : TcpLineDevice
{
    readonly string _user, _pass, _id;
    readonly double _fade;

    public LutronDevice(string deviceId, string name, string host, int port, string user, string pass,
        string integrationId, double fadeSeconds, IReadOnlyList<CommandInfo> commands)
        : base(deviceId, name, host, port)
    {
        _user = user; _pass = pass; _id = integrationId; _fade = fadeSeconds;
        Commands = commands;
        if (_id.Length > 0) SetState("integrationId", _id);
        Run();
    }

    public override IReadOnlyList<CommandInfo> Commands { get; }

    /// <summary>Whether this load has been given the processor's number for it, without which it is unaddressable.</summary>
    internal bool HasIntegrationId => _id.Length > 0;

    /// <summary>
    /// Off, at a fade the caller chose rather than the one configured — the driver's <c>all_lights_off</c>
    /// tool takes a fade as an argument, and going through <c>ExecuteAsync("power_off")</c> would silently
    /// use the device's own setting instead.
    /// </summary>
    internal Task<CommandResult> OffAsync(double fadeSeconds, CancellationToken ct) =>
        SendResultAsync(
            $"#OUTPUT,{_id},1,0,{fadeSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}", ct);

    /// <summary>The processor prompts with "login:" and "password:" and expects bare lines back.</summary>
    protected override async Task OnConnectedAsync(CancellationToken ct)
    {
        await SendLineAsync(_user, ct);
        await SendLineAsync(_pass, ct);
        if (_id.Length > 0) await SendLineAsync($"?OUTPUT,{_id},1", ct);
    }

    protected override void OnLine(string line)
    {
        // Prompts come through as lines too; they aren't errors and aren't data.
        if (line.StartsWith("login", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("password", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("GNET", StringComparison.Ordinal)) return;

        if (line.StartsWith("~ERROR", StringComparison.Ordinal)) { SetState("lastError", $"The processor rejected that ({line})."); return; }

        // ~OUTPUT,<id>,1,<level>   — action 1 is "set level"
        if (!line.StartsWith("~OUTPUT,", StringComparison.Ordinal)) return;
        var parts = line["~OUTPUT,".Length..].Split(',');
        if (parts.Length < 3 || parts[0] != _id || parts[1] != "1") return;
        if (!double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var level)) return;

        var pct = (int)Math.Round(level);
        var power = pct > 0 ? "on" : "off";
        SetState("brightness", pct.ToString());
        SetState("power", power);
        Emit("brightness.changed", new Dictionary<string, string> { ["brightness"] = pct.ToString() });
        Emit("power.changed", new Dictionary<string, string> { ["power"] = power });
    }

    string Fade => _fade.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    public override async Task<CommandResult> ExecuteAsync(string commandId, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        if (commandId == "raw")
        {
            var raw = args.GetValueOrDefault("command", "").Trim();
            if (raw.Length == 0) return CommandResult.Fail("Empty command");
            return await SendResultAsync(raw.StartsWith('#') || raw.StartsWith('?') ? raw : "#" + raw, ct);
        }

        if (_id.Length == 0)
            return CommandResult.Fail("This light has no integration id yet — pick one off the processor first.");

        var current = int.TryParse(GetState().GetValueOrDefault("brightness"), out var b) ? b : 0;

        var level = commandId switch
        {
            "power_on" => 100,
            "power_off" => 0,
            "brightness_up" => Math.Min(100, current + 10),
            "brightness_down" => Math.Max(0, current - 10),
            "set_brightness" => int.TryParse(args.GetValueOrDefault("brightness"), out var v) ? Math.Clamp(v, 0, 100) : -1,
            "refresh" => -2,
            _ => -3,
        };

        return level switch
        {
            -3 => CommandResult.Fail($"Unknown command '{commandId}'"),
            -2 => await SendResultAsync($"?OUTPUT,{_id},1", ct),
            -1 => CommandResult.Fail("Brightness is a number from 0 to 100."),
            _ => await SendResultAsync($"#OUTPUT,{_id},1,{level},{Fade}", ct),
        };
    }
}
