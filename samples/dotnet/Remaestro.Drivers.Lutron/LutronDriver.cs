using System.Text;
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
    /// <b>That sentence was a promise this method's code could not keep, and the code is what changed.</b>
    /// <see cref="LutronDevice.OffAsync"/> used to answer <c>Ok</c> the moment the write left the socket,
    /// so the only load it could ever name was one whose connection was already down — a processor
    /// answering <c>~ERROR,2</c> for a load that is not in its database was counted as going off. The
    /// refusal was being read: it went into <c>lastError</c>, which nothing on the command path looks at.
    /// It is now waited for.
    /// </para>
    /// <para>
    /// <b>What "refused" means here, and what it does not.</b> It means the processor said <c>~ERROR</c>
    /// within the window, or the connection was not there to write to. It does not mean the lamp is still
    /// lit: the integration protocol reports what the <i>processor</i> did, and a dimmer whose bulb has
    /// gone, whose airgap is out or whose load is dead is indistinguishable from one that worked. A load
    /// that says nothing is counted as done, because saying nothing is what a load already at zero does —
    /// see <see cref="LutronDevice.Objects"/>.
    /// </para>
    /// <para>
    /// <b>The loads are addressed at the same time rather than one after another.</b> Each is its own
    /// connection to the processor, so they always were independent; sending them in turn only became
    /// visible once each send waits for an objection, and a house with twenty loads would have spent
    /// twenty windows to darken. It spends one.
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

        var outcomes = await Task.WhenAll(
            loads.Select(async load => (load.Name, Result: await load.OffAsync(fade, ct))));

        var refused = outcomes.Where(o => !o.Result.Ok).ToList();

        if (refused.Count == loads.Count)
            return AssistantToolAnswer.Failed(
                $"None of the {loads.Count} Lutron loads went off. {refused[0].Result.Error}",
                string.Join(" | ", refused.Select(r => $"{r.Name}: {r.Result.Error}")));

        var done = loads.Count - refused.Count;
        return refused.Count == 0
            ? AssistantToolAnswer.Says($"All {done} Lutron loads are going off over {fade:0.##} seconds.")
            : AssistantToolAnswer.Says(
                $"{done} of {loads.Count} Lutron loads are going off over {fade:0.##} seconds. "
                + $"These didn't take it: {string.Join(", ", refused.Select(r => r.Name))}.");
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
    /// <para>
    /// It goes through <see cref="LineDevice.SendAndHearAsync"/> like every other command, which is what lets the tool
    /// above name a load that refused. It used to be a bare <c>SendResultAsync</c>, so it answered
    /// <c>Ok</c> whenever the socket was up.
    /// </para>
    /// </summary>
    internal Task<CommandResult> OffAsync(double fadeSeconds, CancellationToken ct) =>
        RefusedLogin() is { } refused
            ? Task.FromResult(refused)
            : SendAndHearAsync(
                $"#OUTPUT,{_id},1,0,{fadeSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}", ct);

    /// <summary>
    /// The processor prompts with <c>login:</c> and <c>password:</c> and expects bare lines back.
    ///
    /// <para>
    /// <b>It does not wait to be asked, and that is deliberate rather than left over.</b> A processor that
    /// takes the login must behave exactly as it did before this was written, and waiting for a prompt
    /// before answering it would put a round trip — and, on a processor that does not prompt the way this
    /// driver expects, a whole timeout — in front of every reconnection. Whether the credentials were taken
    /// is read afterwards, off the bytes the processor sends anyway; see <see cref="OnRead"/>.
    /// </para>
    /// <para>
    /// A fresh connection is a fresh session, so everything the last one established is dropped here.
    /// This runs before the read loop starts, so nothing can be watching while it does.
    /// </para>
    /// </summary>
    protected override async Task OnConnectedAsync(CancellationToken ct)
    {
        _greeting.Clear();
        _watchingGreeting = true;
        _loginRefused = null;

        await SendLineAsync(_user, ct);
        await SendLineAsync(_pass, ct);
        if (_id.Length > 0) await SendLineAsync($"?OUTPUT,{_id},1", ct);
    }

    // ---- the integration login, which nothing used to check -------------------------------------------

    /// <summary>The processor's login prompt, which is the one token this check turns on.</summary>
    const string LoginPrompt = "login:";

    /// <summary>
    /// How much of a connection's opening conversation is held while watching for a refusal. A refusal is
    /// the processor's answer to the one set of credentials this driver ever sends, so it is in the first
    /// exchange or it is not coming; the budget is here so that a processor which never refuses cannot
    /// grow this without bound, and it is generous rather than tuned.
    /// </summary>
    const int GreetingBudget = 4096;

    /// <summary>
    /// The opening conversation so far, while it is still being watched. Touched only from the read loop
    /// and from <see cref="OnConnectedAsync"/>, which runs before the read loop for that connection starts
    /// and after the previous one has ended.
    /// </summary>
    readonly StringBuilder _greeting = new();
    bool _watchingGreeting;

    /// <summary>
    /// What to tell whoever pressed the key, once the processor has said the credentials were not taken —
    /// null until it has. Read from command threads, written from the read loop.
    /// </summary>
    volatile string? _loginRefused;

    /// <summary>
    /// Reading the processor's opening conversation for the one thing it says when it will not have us.
    ///
    /// <para>
    /// <b>The defect.</b> <see cref="OnConnectedAsync"/> writes a username and a password and nothing
    /// anywhere looked at what came back. With the wrong integration password the processor takes the
    /// connection, prompts for a login again, and ignores every command sent down it — <i>silently</i>:
    /// there is no <c>~ERROR</c> for this, so a command spends its window hearing nothing and
    /// <see cref="LineDevice.NothingSaid"/> turns that into a success. Every light in the house then
    /// reports as having done what it was told, for ever. <c>LineDevice</c> has also marked the device
    /// online by this point — it does that before it calls <c>OnConnectedAsync</c> — so nothing on any
    /// surface disagreed either.
    /// </para>
    ///
    /// <para>
    /// <b>Why this reads bytes rather than lines, and why that is the part that makes it correct.</b>
    /// <b>Nobody here has a processor</b>, so how a Lutron prompt is framed is not a thing this change is
    /// allowed to assume. A telnet prompt normally carries no terminator, and
    /// <see cref="LineDevice"/> frames strictly on <see cref="LineDevice.Terminator"/> — so under that
    /// reading the refusal is a handful of bytes that sit in the pending buffer and are followed by nothing
    /// at all, which is precisely the shape <see cref="OnLine"/> cannot ever be told about. Under the other
    /// reading the same words arrive as ordinary lines. <b>The bytes are the same either way</b>, and they
    /// are what this looks at, which is the whole of why it does not have to know which reading is right.
    /// </para>
    ///
    /// <para>
    /// <b>The rule: a second login prompt on one connection.</b> The processor prompts for a login once
    /// when the session opens. This driver answers it once. So a second prompt cannot be the greeting and
    /// cannot be an answer to anything else — there is nothing else. It is timing-independent, which
    /// matters because the credentials go out without waiting for the first prompt: counting prompts
    /// against what we have sent would depend on which arrived first, and counting them against each other
    /// does not.
    /// </para>
    ///
    /// <para>
    /// <b>Nothing here establishes the opposite, and it is not for want of trying.</b> The obvious positive
    /// signal is the ready prompt — <c>GNET&gt;</c> on RadioRA 2, which is the one this driver's own line
    /// filter names. It is not the same word on every processor this driver claims, there is no capture of
    /// any of them in this tree, and a check that required the wrong one would refuse to believe a
    /// processor that was working perfectly. So the driver acts on the refusal alone. <b>The rule, rather
    /// than a list of what the alternative might be:</b> anything that is not a second login prompt leaves
    /// this session exactly where it stood before this method existed — no claim, no mark, and every
    /// command behaving as it did.
    /// </para>
    ///
    /// <para>
    /// <b>And an accepted login is not an authorised one.</b> Monitoring rights are granted separately, and
    /// a login accepted without them takes commands and reports nothing back — which is
    /// <see cref="Objects"/>'s silence and stays a success. Nothing in the integration protocol tells a
    /// right this session lacks from a thing the processor simply had nothing to say about, so this cannot
    /// be widened into a check that does.
    /// </para>
    ///
    /// <para>
    /// <b>The prompt is matched with its colon, where <see cref="WithoutPrompts"/> matches the bare
    /// word.</b> The two want opposite leniencies: throwing away a prompt that turns out not to be one
    /// costs nothing, and accusing a household of a wrong password because a banner happened to use the
    /// word costs a support call. So this one is the strict reading.
    /// </para>
    /// </summary>
    protected override void OnRead(string text)
    {
        if (!_watchingGreeting) return;

        _greeting.Append(text);

        if (Occurrences(_greeting.ToString(), LoginPrompt) >= 2)
        {
            StopWatching();

            // Said in the driver's own words and pointed at the setting to go and look at, because the
            // processor's way of saying this is to say nothing. It reaches a person twice: as the answer
            // any command gets from here on, and — since the device is connected and therefore online —
            // as the card's own "Something's wrong", which is what that state is for.
            var why = $"{Name}: the processor asked for a login a second time after this hub had sent the "
                    + "username and password, which is how it says they weren't taken. The connection is "
                    + "up and everything sent over it is being ignored. Check this device's Username and "
                    + "Password against the integration login set on the processor.";

            _loginRefused = why;
            SetState("lastError", why);

            // And the command that raced the login, if there is one. `LineDevice` marks the device online
            // *before* it calls `OnConnectedAsync`, so a command sent the moment a light comes back — an
            // activity firing after a processor reboot, or after one of the five-second retries — is
            // already waiting while these bytes are arriving. Without this it spends its window hearing
            // nothing and takes the success that silence means here, which is the bug being fixed, one
            // command narrower. The same hand-off `OnLine` makes for a `~ERROR`, for the same reason.
            InFlight?.Refused(why);
            return;
        }

        if (_greeting.Length > GreetingBudget) StopWatching();
    }

    void StopWatching()
    {
        _watchingGreeting = false;
        _greeting.Clear();
    }

    static int Occurrences(string text, string token)
    {
        var found = 0;
        for (var at = text.IndexOf(token, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = text.IndexOf(token, at + token.Length, StringComparison.OrdinalIgnoreCase)) found++;
        return found;
    }

    /// <summary>
    /// The answer a command gets once the processor has said it will not have this login — or null while
    /// it has not.
    /// <para>
    /// Sending anyway is the bug: a command written down a session the processor is ignoring hears
    /// nothing, and silence on this protocol is a success. So it is answered here instead, with the
    /// sentence rather than with a shrug.
    /// </para>
    /// </summary>
    CommandResult? RefusedLogin() => _loginRefused is { } why ? CommandResult.Fail(why) : null;

    protected override void OnLine(string raw)
    {
        // Prompts reach this method too, and stripping them is not the same as discarding the line they
        // came on — which is what this did, and which is the second half of not knowing how they are
        // framed. If the processor terminates its prompts, a prompt arrives alone, nothing is left behind
        // it and this returns exactly where the discard did. If it does not, then the first thing it ever
        // terminates is the prompts with a real message glued to the end of them — the connect-time query's
        // own answer, on the very first line — and a discard threw that away along with them. One line
        // covers both readings; neither is assumed.
        var line = WithoutPrompts(raw);
        if (line.Length == 0) return;

        // The processor's own refusal. This driver has recognised `~ERROR` since it was written and has
        // always put it in a state key, while the command had already answered success on the write going
        // out — so a load the processor has never heard of was reported as going off. Handed to whatever
        // command is waiting, before anything else is done with the line.
        if (Refusal(line) is { } refused)
        {
            SetState("lastError", refused);
            InFlight?.Refused(refused);
            return;
        }

        // ~OUTPUT,<id>,1,<level>   — action 1 is "set level"
        if (!line.StartsWith("~OUTPUT,", StringComparison.Ordinal)) return;
        var parts = line["~OUTPUT,".Length..].Split(',');
        if (parts.Length < 3 || parts[0] != _id || parts[1] != "1") return;

        // The processor talking about *this* load, which is the only positive acknowledgement this protocol
        // has. It ends the wait early; see Objects for what that is and is not worth. A malformed level is
        // still the processor answering, so this comes before the parse rather than after it.
        InFlight?.Took();

        if (!double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var level)) return;

        var pct = (int)Math.Round(level);
        var power = pct > 0 ? "on" : "off";
        SetState("brightness", pct.ToString());
        SetState("power", power);
        Emit("brightness.changed", new Dictionary<string, string> { ["brightness"] = pct.ToString() });
        Emit("power.changed", new Dictionary<string, string> { ["power"] = power });
    }

    /// <summary>The words a Lutron processor prompts with. Nothing else in this protocol begins with them.</summary>
    static readonly string[] PromptWords = ["login", "password", "GNET"];

    /// <summary>
    /// A line with any run of prompts taken off the front of it, and whatever the processor actually said
    /// left behind. Empty when the line was nothing but prompts.
    ///
    /// <para>
    /// <b>It eats a prompt word and the punctuation a prompt wears, and stops.</b> A prompt word followed
    /// by anything else — a banner, a word this driver has not thought of — is left whole rather than
    /// guessed at, so the strict direction here is "strip less". That is safe in a way the old discard was
    /// not: the only remainders this method's caller acts on begin with <c>~ERROR</c> or <c>~OUTPUT,</c>,
    /// neither of which can be produced by under-stripping, while over-discarding threw a real message
    /// away.
    /// </para>
    /// <para>
    /// Case is the processor's business and folding it costs nothing, since no line this driver reads
    /// begins with any of these words in any case.
    /// </para>
    /// </summary>
    internal static string WithoutPrompts(string line)
    {
        var rest = line.AsSpan().TrimStart();

        while (rest.Length > 0)
        {
            var word = "";
            foreach (var candidate in PromptWords)
                if (rest.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)) { word = candidate; break; }

            if (word.Length == 0) break;

            var after = rest[word.Length..];
            var eaten = 0;
            while (eaten < after.Length && after[eaten] is ':' or '>' or ' ' or '\t') eaten++;

            // The word on its own, ending the line, is still a prompt — a processor that writes `login`
            // with no colon has said the same thing. A word with real text behind it has not, and is left
            // alone.
            if (eaten == 0 && after.Length > 0) break;

            rest = after[eaten..].TrimStart();
        }

        return rest.ToString();
    }

    /// <summary>
    /// What the processor says when it will not do something, in words rather than in a number. Null for
    /// anything that is not a refusal.
    ///
    /// <para>
    /// <b>The whole of a Lutron refusal is <c>~ERROR,&lt;n&gt;</c>, and it does not carry the integration
    /// id.</b> There is no echo of what was refused, no sequence number and no transaction id anywhere in
    /// the integration protocol — so nothing in the message itself can attribute it to a command.
    /// </para>
    /// <para>
    /// <b>That is what forces the correlation to be positional</b>, which is why nothing is put in
    /// <see cref="LineDevice.Turn.Tag"/> here: the only thing that can attribute a refusal to a command is
    /// that there is exactly one command it could belong to, and the base class allows exactly one. Each
    /// load is its own connection to the processor, so "one in flight" is per load rather than per house,
    /// and two loads can be sent at once without either being able to steal the other's answer.
    /// </para>
    /// <para>
    /// <b>The hole that leaves, stated rather than smoothed over.</b> <see cref="OnLine"/> ends the wait on
    /// <i>any</i> <c>~OUTPUT</c> for this load, and nothing in that line says which command it belongs to —
    /// so a report raised by something other than the command in flight ends the wait before a refusal that
    /// was still coming, and the command reports success. <b>Hold the rule rather than the list</b>, because
    /// the rule is what a source nobody has thought of would also satisfy: <i>anything at all that makes the
    /// processor talk about this load</i>. Three are known.
    /// </para>
    /// <para>
    /// <b>A keypad press</b> on this load, by a person, inside the window.
    /// </para>
    /// <para>
    /// <b>This driver's own connect-time query, which is the one that is easy to miss because it is
    /// ours.</b> <see cref="OnConnectedAsync"/> sends <c>?OUTPUT,&lt;id&gt;,1</c> every time the connection
    /// comes up, and the answer to it is a <c>~OUTPUT</c> for this exact load that no command asked for. The
    /// window it can land in is real rather than theoretical: <see cref="LineDevice"/> marks the device
    /// online <i>before</i> it calls <c>OnConnectedAsync</c>, so a command sent the moment a light comes
    /// back — an activity firing after a processor reboot, or after any of the five-second retries — is
    /// waiting while that answer is still on its way.
    /// </para>
    /// <para>
    /// <b>An earlier command's own report, arriving late.</b> <see cref="Objects"/> below already says the
    /// half of this that matters: a long fade puts a load's level report well outside the window of the
    /// command that caused it. Commands are one at a time, so the window it lands in instead is the next
    /// command's.
    /// </para>
    /// <para>
    /// All three are narrow, and narrowed the same way — how narrow each one is has not been measured
    /// and they are not obviously alike, so do not read the shared narrowing as a shared size. The
    /// dominant refusal is <c>~ERROR,2</c>, an id the processor does not have — and an id it does not have
    /// is not sending level reports for itself either, by a keypad or by our query or behind a fade. What
    /// remains in every case is a real load, reported on by something other than the command in flight,
    /// refusing that command for one of the other five reasons. This driver's own clamping precludes most of
    /// those on the light commands; it precludes none of them on <c>raw</c>, whose content it knows nothing
    /// about.
    /// </para>
    /// <para>
    /// The six numbers are the ones Lutron's integration protocol document defines. The line is quoted
    /// alongside the sentence in every case, including the ones this driver has no words for: a processor
    /// or a firmware with a seventh number gets an honest admission rather than an invented reason, and
    /// the number is still in front of whoever reads it.
    /// </para>
    /// </summary>
    string? Refusal(string line)
    {
        if (!line.StartsWith("~ERROR", StringComparison.Ordinal)) return null;

        var code = line.Split(',') is [_, var n, ..] ? n.Trim() : "";
        var why = code switch
        {
            "1" => "the command didn't have the number of parameters it expects",
            "2" => $"it has no object with integration id {_id} — the project file it was programmed with "
                 + "may have changed since this light was picked",
            "3" => "that action isn't one this kind of object takes",
            "4" => "a value was out of the range it accepts",
            "5" => "a value was malformed",
            "6" => "it doesn't support that command",
            _ => null,
        };

        return why is null
            ? $"The processor refused that for {Name}, with a reason this driver doesn't know ({line})."
            : $"The processor refused that for {Name} — {why} ({line}).";
    }

    /// <summary>
    /// How long to give the processor to object — <see cref="LineDevice"/>'s wait, at this protocol's
    /// number.
    /// <para>
    /// It measures one round trip on a LAN and the processor's own turnaround, and nothing else — not the
    /// fade, which is the light's business and not the processor's. A refusal is a parse or a lookup
    /// failure rather than an action, so it comes back immediately or not at all. Three quarters of a
    /// second is well clear of that and short enough not to be felt on a button.
    /// </para>
    /// <para>
    /// It is spent in full only by a load with nothing to say, which is the ordinary outcome of telling a
    /// light to be what it already is — and, since that is exactly what <c>all_lights_off</c> does to a
    /// room that is already dark, it is why that tool addresses its loads at the same time rather than in
    /// turn.
    /// </para>
    /// <para>
    /// <b>Silence is left a success</b>, which is <see cref="LineDevice.NothingSaid"/>'s default. The
    /// positive side exists but cannot be required: <c>~OUTPUT,&lt;id&gt;,1,&lt;level&gt;</c> does carry
    /// the id and is a real acknowledgement, and <see cref="OnLine"/> uses it to end the wait early — but a
    /// load already at the level it was told to go to reports nothing at all, an integration login without
    /// monitoring rights sees none of these reports, and a long fade can put the report well outside any
    /// window worth waiting. So the acknowledgement only buys back the latency.
    /// </para>
    /// </summary>
    protected override TimeSpan Objects => TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// What the sentence names when the socket goes out from under a command in flight. That answer now
    /// comes from <see cref="LineDevice"/> itself, for every line driver, rather than from this one's
    /// <c>OnDisconnected</c> — which is where it was written first, and which two of the five drivers
    /// override without chaining.
    /// </summary>
    protected override string FarEnd => "the processor";


    string Fade => _fade.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    public override async Task<CommandResult> ExecuteAsync(string commandId, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        // Before anything is sent, and before `raw` too: a session the processor has refused ignores every
        // one of these equally, and it is `raw` whose content this driver knows least about.
        if (RefusedLogin() is { } refused) return refused;

        if (commandId == "raw")
        {
            var raw = args.GetValueOrDefault("command", "").Trim();
            if (raw.Length == 0) return CommandResult.Fail("Empty command");

            // A refusal still reaches the caller, which is the case that matters here: `raw` is the one
            // command whose content this driver knows nothing about, so the processor's opinion is the
            // only opinion there is. A raw command addressed at some *other* integration id gets the
            // weaker half of the deal — OnLine drops that id's report, so nothing ends the wait early and
            // it spends the whole window before succeeding.
            return await SendAndHearAsync(raw.StartsWith('#') || raw.StartsWith('?') ? raw : "#" + raw, ct);
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
            -2 => await SendAndHearAsync($"?OUTPUT,{_id},1", ct),
            -1 => CommandResult.Fail("Brightness is a number from 0 to 100."),
            _ => await SendAndHearAsync($"#OUTPUT,{_id},1,{level},{Fade}", ct),
        };
    }
}
