namespace Remaestro.Sdk;

/// <summary>One value a field will accept, as offered to whoever's filling it in.</summary>
/// <param name="Value">What actually gets sent.</param>
/// <param name="Label">What the user reads. Falls back to <paramref name="Value"/> when blank.</param>
/// <param name="Detail">Optional extra — a note, a source's real id, whatever helps someone choose.</param>
/// <param name="Current">This is the value the device reports it's on right now.</param>
public sealed record FieldOption(string Value, string Label = "", string Detail = "", bool Current = false);

/// <summary>
/// What the hub must do with a field's value, as distinct from what the form draws for it. Mirrors
/// <c>Sensitivity</c> in <c>driver.proto</c>, where the reasoning is written out.
/// <para>
/// <b>Two levels rather than a boolean.</b> "Don't put it on a screen" and "don't keep it at all" are
/// different requirements, and every surveyed system that collapsed them got one of the two wrong.
/// </para>
/// </summary>
public enum FieldSensitivity
{
    /// <summary>
    /// You didn't say. The hub reads <see cref="ConfigField.Type"/> and its own heuristics, exactly as it
    /// did before this existed — so declining to declare costs nothing and changes nothing.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// An ordinary value. Worth saying out loud to opt <i>out</i> of the heuristics: the hub's word lists
    /// cannot tell a <c>publicKey</c> from a private one, and one of them has no bare "key" in it at all.
    /// </summary>
    Normal = 1,

    /// <summary>
    /// A credential. Never rendered into a page, never printed in a log, trace or diagnostic bundle — and
    /// still stored, so the hub can reconnect without asking a person again.
    /// <para>
    /// "Never rendered" is stronger than "rendered as dots", and that is the point: this console is Blazor
    /// Server, so a value written into an input's <c>value=</c> crosses the circuit to the browser whether
    /// or not the glyphs are masked. The hub sends <i>whether</i> something is stored instead.
    /// </para>
    /// </summary>
    Sensitive = 2,

    /// <summary>
    /// A credential the hub must not keep: a pairing PIN, a one-time code, a token you exchange at startup.
    /// It reaches <c>CreateDeviceAsync</c> and is not written to storage, so it does not survive a restart
    /// and is not in a backup bundle.
    /// <para>
    /// The cost is yours: a device configured with one comes back without it after a reboot. Declare it
    /// only where you can proceed without it, or where somebody re-supplying it is the intended flow.
    /// </para>
    /// </summary>
    WriteOnly = 3,
}

/// <summary>A configurable field — used for device config and for command parameters.</summary>
/// <param name="Options">
/// The values this field accepts, when they're known up front. A driver that knows a device's real list —
/// an AVR's renamed sources, a Hubitat driver's ENUM-constrained custom command — should say so here
/// rather than leaving someone to type a value that will be rejected.
/// </param>
/// <param name="OptionsKey">
/// For a list that can only be known by asking the device. The hub calls
/// <see cref="IOptionSourceDevice.ListOptionsAsync"/> with this key at the moment someone picks a value.
/// Use this when the list changes at runtime; use <paramref name="Options"/> when it doesn't.
/// </param>
/// <param name="Min">Lowest accepted value, for a <c>number</c> field.</param>
/// <param name="Max">Highest accepted value, for a <c>number</c> field.</param>
/// <param name="Advanced">
/// A field with a good default that most people should never touch — a port, a baud rate, an override.
/// Folded away behind "Advanced" so the form asks only what it has to.
/// </param>
/// <param name="Managed">
/// A field filled by a flow rather than by hand: a credential pairing earns, the entity id the bridge
/// picker chooses, a codeset the remote editor writes. <b>Never one of the questions a form asks</b> —
/// offering an empty box for a value the user can't know is worse than not asking.
/// <para>
/// That is a rule about where the field is put, not about whether it exists on screen. An add-device form
/// leaves it out entirely; an edit form may keep it, in a collapsed group named for whoever wrote the
/// value, because a flow that writes an id is not always a flow that can rewrite one — the reMaestro hub
/// keeps them for exactly that reason, and deleting the device is the only other repair.
/// </para>
/// </param>
/// <param name="Sensitivity">
/// How careful the hub has to be with the <i>value</i>, as distinct from what the form draws for it. See
/// <see cref="FieldSensitivity"/>; unset means "I didn't say", and the hub falls back to reading
/// <paramref name="Type"/> exactly as it did before this existed.
/// </param>
public sealed record ConfigField(
    string Key,
    string Label,
    // string | number | bool | secret | multiline, plus anything in HardwareFieldType — which the console
    // turns into a picker over what's actually plugged into the hub, rather than a box to type a path into.
    string Type = "string",
    bool Required = false,
    string? Default = null,
    string? Help = null,
    IReadOnlyList<FieldOption>? Options = null,
    string? OptionsKey = null,
    double? Min = null,
    double? Max = null,
    bool Advanced = false,
    bool Managed = false,
    string? ShowWhen = null,
    FieldSensitivity Sensitivity = FieldSensitivity.Unspecified)
{
    /// <summary>A number field with a range — worth a slider rather than a text box.</summary>
    public bool HasRange => Type == "number" && Min is not null && Max is not null;

    /// <summary>
    /// Whether this field's value is a credential, by the driver's own declaration or by its type.
    /// <para>
    /// <c>Type == "secret"</c> is still read, and still means the same thing, because forty drivers say it
    /// that way and a contract does not get to change its mind about a field that is already in the field.
    /// What <see cref="Sensitivity"/> adds is a way to say it about a field whose <i>widget</i> is something
    /// else — a picker, a number, a multiline block — and a way to say the opposite: a field genuinely
    /// called <c>publicKey</c> declares <see cref="FieldSensitivity.Normal"/> and stops being guessed at.
    /// </para>
    /// </summary>
    public bool IsSensitive =>
        Sensitivity is FieldSensitivity.Sensitive or FieldSensitivity.WriteOnly
        || (Sensitivity is FieldSensitivity.Unspecified && Type == "secret");

    /// <summary>
    /// Whether this field applies at all, given what's been filled in so far. <c>ShowWhen</c> reads
    /// <c>"key=value"</c>, or <c>"key=a|b"</c> for several — a field that belongs to one way of reaching
    /// the device, so the other ways don't have to explain why it's there.
    /// </summary>
    public bool AppliesTo(IReadOnlyDictionary<string, string> config)
    {
        if (ShowWhen is not { Length: > 0 } rule) return true;

        var split = rule.IndexOf('=');
        if (split <= 0) return true;               // malformed: show it rather than hide it forever

        var key = rule[..split].Trim();
        var current = config.GetValueOrDefault(key, "");
        return rule[(split + 1)..]
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(v => string.Equals(v, current, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The field this one depends on, if any — what a wizard should ask first.</summary>
    public string? DependsOn =>
        ShowWhen is { Length: > 0 } rule && rule.IndexOf('=') is > 0 and var i ? rule[..i].Trim() : null;

    /// <summary>Convenience for the common case: a fixed set of values with no separate labels.</summary>
    public static IReadOnlyList<FieldOption> Values(params string[] values) =>
        values.Select(v => new FieldOption(v)).ToList();
}

/// <summary>Describes a command a device exposes.</summary>
public sealed record CommandInfo(
    string Id,
    string Label,
    string? Description = null,
    IReadOnlyList<ConfigField>? Parameters = null);

/// <summary>
/// A tool your driver offers the assistant, and — the part that matters — which assistants may reach it.
///
/// <para>
/// <b>The rule: a tool that acts is offered on the console and nowhere else, unless you say otherwise.</b>
/// The console means Admin or Operator, typed, on a screen, with somebody looking. The remote — anything
/// spoken in the house, and the chat on a handset — is opt-in per tool, and the opt-in is simply naming
/// <see cref="AssistantSurface.Remote"/> in <see cref="Surfaces"/> beside <c>Acts = true</c>. There is no
/// second flag, on purpose: the combination is then legible in the descriptor, so the plugins page can
/// print it, a test can count it, and somebody reading your manifest can see it without running anything.
/// </para>
/// <para>
/// <b>Where the rule comes from.</b> The product already made this split for its own code. Three commands
/// take a free string and send it at hardware, and one of them can drop every Bluetooth bond a proxy holds
/// — reachable by a Viewer, sayable out loud, unconfirmed, undone only by walking to the far end. They were
/// taken off the remote and kept on the console. The fix was a deny-list of three names, and a deny-list
/// cannot close an open namespace: nothing can enumerate the tools plugins will invent. So the scope
/// travels with the tool, declared by the only party that knows what it does.
/// </para>
/// <para>
/// <b>A declared tool is called. It was not always, and this paragraph used to say so.</b> Until dispatch
/// landed the hub read a declaration, validated it, showed it on your plugin's page and in the read-only
/// prompt viewer, and stopped — a declared tool was visible and inert, and this comment said that plainly
/// so that a page could not imply otherwise. <b>It is now the opposite of the truth</b>, which is worse
/// than the thing it was written to prevent: it invites you to declare <c>Acts</c> and
/// <c>Surfaces = ["remote"]</c> on the understanding that nothing can reach it, and something can.
/// </para>
/// <para>
/// What actually happens: the hub adds your tools to the catalogue it gives the model, per assistant,
/// rebuilt each round; it filters them by <see cref="AssistantToolSpec.Surfaces"/> on the way out; and it
/// <b>refuses again at the call, before your process is started</b>, because a model can name a tool it was
/// never offered. Then it calls you. So the surface list is a real boundary and the only one — see
/// <see cref="AssistantToolSpec.Acts"/>, which is a claim you make to a person and not a gate the hub
/// enforces.
/// </para>
/// </summary>
/// <param name="Id">
/// Your bare name for it — <c>"scene_report"</c>, not <c>"acme.scene_report"</c>. <b>The hub namespaces it
/// as <c>&lt;type id&gt;.&lt;id&gt;</c></b> and there is no way to opt out. Lower-case letters, digits and
/// underscores; anything else is refused with a reason naming your plugin.
/// </param>
/// <param name="Label">What a person reads on your plugin's page. Never sent to a model.</param>
/// <param name="Description">
/// What the model reads, and it is a prompt rather than a caption — it rides in every request on every
/// surface you offer the tool on. Keep it inside <see cref="AssistantToolLimits.DescriptionChars"/>
/// characters; the hub refuses a longer one and says the number.
/// </param>
/// <param name="Surfaces">
/// Which assistants offer it — see <see cref="AssistantSurface"/>. <b>Empty means nowhere, and that is the
/// default.</b> Not for safety: a tool offered everywhere makes every prompt longer and every model's
/// choice harder, and that failure is diffuse and lands on somebody else's conversation.
/// </param>
/// <param name="Acts">
/// Whether calling it changes anything — turns something on, writes a setting, sends at hardware. False
/// means it only reads. <b>A claim, not a guarantee</b>: nothing can check it, because the doing happens
/// inside your process. The hub uses it to describe your plugin honestly, and presents it as your word.
/// </param>
/// <param name="Parameters">
/// The tool's arguments, as <see cref="ConfigField"/>s. Not a JSON Schema string, deliberately — the hub
/// renders these to JSON Schema in the one place it can also bound their size, and a schema carried as an
/// opaque string would go to the model vendor unvalidated and would defeat the descriptor cache's staleness
/// check. It is already how a command's parameters are declared.
/// </param>
public sealed record AssistantToolSpec(
    string Id,
    string Label,
    string Description,
    IReadOnlyList<string>? Surfaces = null,
    bool Acts = false,
    IReadOnlyList<ConfigField>? Parameters = null)
{
    /// <summary>Whether this is offered where anybody speaking in the house can trigger it.</summary>
    public bool OnTheRemote =>
        Surfaces is { } s && s.Contains(AssistantSurface.Remote, StringComparer.Ordinal);
}

/// <summary>
/// What one of your tools answers a model with.
///
/// <para>
/// <b><see cref="Text"/> is prose for a model, not a structure for a program.</b> The hub puts it into the
/// conversation unchanged and does not parse it, so whatever you want understood has to be in the words.
/// A JSON blob is a perfectly good thing to put here if that is what reads best; nothing on either side
/// treats it as one.
/// </para>
/// <para>
/// <b>Say why, even when it failed.</b> <see cref="Ok"/> false with a sentence in <see cref="Text"/> lets a
/// model tell somebody what went wrong or try another way. A bare failure leaves it guessing, and what it
/// guesses is usually that it should call your tool again.
/// </para>
/// <para>
/// <b>The hub treats every word of this as external data</b> — the same footing as a film's title or a
/// device's reported state, which is to say: something to read, never something to obey. Writing
/// instructions in here is not a way to steer the assistant, and a hub that starts obeying it would be a
/// bug in the hub.
/// </para>
/// </summary>
/// <param name="Ok">Whether the tool did what was asked.</param>
/// <param name="Text">
/// What the model reads. Bounded by the hub — see <c>AssistantToolLimits.ResultChars</c> — and truncated
/// with a line saying so rather than refused, so an answer that is too long still gets somebody an answer.
/// </param>
/// <param name="Error">
/// The technical half, for the hub's log. Never sent to a model, so nothing here has to be phrased for one.
/// </param>
public sealed record AssistantToolAnswer(bool Ok, string Text, string? Error = null)
{
    public static AssistantToolAnswer Says(string text) => new(true, text);

    /// <param name="text">What the model is told, which should say what a person could do about it.</param>
    /// <param name="error">What the log gets. Defaults to the same sentence.</param>
    public static AssistantToolAnswer Failed(string text, string? error = null) => new(false, text, error ?? text);
}

/// <summary>An event a device raises onto the hub's bus.</summary>
public sealed record DeviceEvent(string Type, IReadOnlyDictionary<string, string>? Data = null);

/// <summary>A field within an event's payload (drives token autocomplete in the rule editor).</summary>
public sealed record EventField(string Key, string Type = "string", string? Description = null);

/// <summary>Declares an event type a device emits, and the shape of its data.</summary>
public sealed record EventSchema(
    string Type,
    string? Description = null,
    IReadOnlyList<EventField>? Fields = null,
    bool HasExtraData = false);

/// <summary>Declares a state key a device keeps (drives token autocomplete).</summary>
public sealed record StateField(string Key, string Type = "string", string? Description = null);

/// <summary>The outcome of executing a command.</summary>
public sealed record CommandResult(bool Ok, string? Error = null, IReadOnlyDictionary<string, string>? Result = null)
{
    public static CommandResult Success(IReadOnlyDictionary<string, string>? result = null) => new(true, null, result);
    public static CommandResult Fail(string error) => new(false, error);
}

/// <summary>
/// One input/source a device can switch to. <paramref name="Id"/> is the value the device's input
/// command takes (e.g. a receiver's <c>MPLAY</c>); <paramref name="Label"/> is what the user sees.
/// </summary>
public sealed record InputSource(string Id, string Label, string Detail = "", bool Current = false);

/// <summary>
/// A device that knows the inputs it can switch to — an AV receiver listing its sources (including any
/// the owner renamed), a TV listing live HDMI ports. The hub asks at the moment the user picks an input;
/// devices that don't implement this fall back to the input capabilities their driver declares.
/// </summary>
public interface IInputSourceDevice
{
    Task<IReadOnlyList<InputSource>> ListInputsAsync(CancellationToken ct);
}

/// <summary>
/// One value an app accepts when launched — a stream URL for a player, say. Sent alongside the app id
/// through the same launch command. <paramref name="Kind"/> is a UI hint: <c>"url"</c> or <c>"text"</c>.
/// </summary>
public sealed record AppLaunchParam(string Key, string Label, string Kind = "text", bool Required = false);

/// <summary>
/// One app a device can launch. <paramref name="Id"/> is the value the device's launch command takes (a
/// Roku channel id, an Apple TV bundle id); <paramref name="Name"/> is what the user sees; <paramref
/// name="Icon"/> is an optional artwork URL for the app's logo. <paramref name="Params"/> is what the app can
/// be handed at launch (a stream to play) — usually none.
/// </summary>
public sealed record AppInfo(string Id, string Name, string Icon = "", string Detail = "", bool Current = false,
    IReadOnlyList<AppLaunchParam>? Params = null);

/// <summary>
/// A device that knows the apps it can launch — a streamer enumerating its installed apps with artwork
/// (Apple TV, Roku, an LG webOS TV). The hub asks at the moment the user opens the app picker; devices that
/// don't implement this fall back to whatever apps their driver declares statically (a smart TV's fixed set).
/// </summary>
public interface IAppListDevice
{
    Task<IReadOnlyList<AppInfo>> ListAppsAsync(CancellationToken ct);
}

/// <summary>
/// A device that can answer "what values will this parameter take?" at the moment someone's choosing one.
/// The key is whatever the parameter declared in its <see cref="ConfigField.OptionsKey"/>, so one device
/// can serve several lists.
/// <para>
/// Only needed for lists that change at runtime. A parameter whose values are fixed should carry them in
/// <see cref="ConfigField.Options"/> instead — no round trip, and they're visible without a live device.
/// </para>
/// </summary>
public interface IOptionSourceDevice
{
    Task<IReadOnlyList<FieldOption>> ListOptionsAsync(string optionsKey, CancellationToken ct);
}

/// <summary>
/// One controllable thing sitting behind a bridge — a bulb on a Hue bridge, a player in a Sonos household,
/// an entity on a Home Assistant server.
/// </summary>
/// <param name="Id">The bridge's own id for it, stable across restarts.</param>
/// <param name="Kind">What it is, loosely: light · switch · media · sensor · scene · speaker · tv.</param>
/// <param name="Config">Extra config to stamp on the device created for it, beyond its id.</param>
public sealed record BridgedDevice(
    string Id,
    string Name,
    string Kind = "",
    string Detail = "",
    IReadOnlyDictionary<string, string>? Config = null);

/// <summary>
/// A device that fronts a hub or bridge: one connection, many controllable things behind it. The hub asks
/// what's back there and offers to add each as a device of its own, so an activity can target "Living Room
/// Lamp" rather than the bridge plus an entity argument. Implementors are expected to pool the underlying
/// connection per host — every child shares the one the bridge opened.
/// </summary>
public interface IBridgeDevice
{
    Task<IReadOnlyList<BridgedDevice>> ListBridgedDevicesAsync(CancellationToken ct);
}

/// <summary>
/// A remote layout a driver ships for its devices. Coordinates are design-surface pixels on a
/// <see cref="Width"/>×<see cref="Height"/> canvas; each element binds to a canonical capability id
/// (<c>power.toggle</c>, <c>nav.up</c>…) so the hub resolves it to that device's real command.
/// </summary>
/// <param name="Icon">
/// How the layout is drawn in a list of remotes. A glyph spec, not free text: <c>ti:&lt;name&gt;</c> for one
/// of the console's bundled Tabler line icons — which is what a driver should send — and, because the same
/// field also carries what a person picked by hand, <c>img:&lt;id&gt;</c>, <c>gf:&lt;font&gt;:&lt;cp&gt;</c>
/// and a <c>data:</c> URI.
/// <para>
/// <b>Anything else is drawn as literal text, and that is deliberate rather than an oversight.</b> The field
/// stays an open string across this boundary: a driver we did not write can put a character here and it will
/// render as that character. It is the one arrangement where getting it wrong is <i>visible</i>. Narrowing it
/// to a closed set of names would make an unrecognised value draw nothing at all — a webfont has no error
/// path, so a name outside the set emits markup that matches no rule and leaves a hole indistinguishable
/// from a layout bug. A wrong glyph can be reported; a missing one gets lived with.
/// </para>
/// <para>
/// So the rule for a driver author is a convention, not a constraint: send a <c>ti:</c> name. The shipped
/// drivers all do, and the hub's own test suite fails if one stops.
/// </para>
/// </param>
public sealed record RemoteTemplateSpec(
    string Name,
    IReadOnlyList<RemoteElementSpec> Elements,
    string Id = "",
    string Description = "",
    string Icon = "ti:device-remote",
    string Category = "",
    string Brand = "",
    int Width = 340,
    int Height = 720);

/// <summary>One placed control on a <see cref="RemoteTemplateSpec"/>.</summary>
/// <param name="Args">
/// What the capability is sent with. <c>input.select</c> plus <c>{["input"] = "GAME"}</c> is one source key
/// on a receiver — which is the only way to draw the sources a real unit has, since the vocabulary names
/// barely a dozen of them discretely and no two receivers agree on which.
/// </param>
public sealed record RemoteElementSpec(
    double X, double Y, double W, double H,
    string Kind = "button",          // button | label | dpad | rocker
    string Capability = "",          // button: the canonical command it sends
    string Shape = "rounded",        // rounded | pill | circle
    string Label = "",               // overrides the vocabulary label; the text of a label element
    string Icon = "",                // overrides the vocabulary icon; a `ti:` spec, as on RemoteTemplateSpec
    string Fill = "",                // optional colour override
    string Variant = "",             // dpad: cross | ring | round | disc
    string Plus = "",                // rocker: capability for +
    string Minus = "",               // rocker: capability for −
    IReadOnlyDictionary<string, string>? Args = null,
    double FontSize = 0);            // px; 0 keeps the default. For labels wider than the key they sit on

/// <summary>
/// A device that draws its own remote — the layout this <i>unit</i> deserves, not the one its type does.
/// <para>
/// The difference is the whole point. A driver's <see cref="IRemaestroDriver.RemoteTemplates"/> are fixed at
/// build time and have to describe every model the driver fronts, so they end up describing none of them:
/// the receiver template carries twelve source keys named after the protocol codes, and the streaming-box
/// template carries volume keys a stick cannot send. Asked of a device, the same driver can answer with the
/// sources this receiver reported and the names its owner gave them, or leave the volume rocker off the
/// stick that has no volume.
/// </para>
/// <para>
/// Not implementing this is the ordinary case and stays a first-class answer — a plug, a lamp, a screen has
/// no remote worth drawing, and offering one anyway is worse than offering nothing. Returning null from a
/// device that usually does answer is equally fine: it means "not this one", or "not yet, I haven't talked
/// to the hardware".
/// </para>
/// </summary>
public interface IRemoteSurfaceDevice
{
    /// <summary>The remote for this device, or null when it hasn't got one.</summary>
    Task<RemoteTemplateSpec?> GetRemoteAsync(CancellationToken ct);
}

/// <summary>
/// The <see cref="ConfigField.Type"/> values that name a piece of hardware attached to the hub, rather than
/// a value the user types. The console renders each as a picker over what it can actually see plugged in.
/// <para>
/// This list is the contract between the two halves. Drivers pick from it; the console's field renderer has
/// to handle every entry, and a test holds it to that — a type only one side knows about renders as a plain
/// text box asking for <c>/dev/ttyUSB0</c>, which is the exact problem the pickers exist to remove, and it
/// fails silently.
/// </para>
/// </summary>
public static class HardwareFieldType
{
    /// <summary>A serial port — a USB-to-RS-232 adapter or an on-board UART.</summary>
    public const string Serial = "serial";

    /// <summary>An evdev node: a remote, a keyboard, a gamepad.</summary>
    public const string Input = "input";

    /// <summary>A LIRC device for sending or receiving IR.</summary>
    public const string Lirc = "lirc";

    /// <summary>An ALSA capture device — a microphone, including one a remote's dongle presents.</summary>
    public const string AudioIn = "audio.in";

    public static readonly IReadOnlyList<string> All = [Serial, Input, Lirc, AudioIn];

    public static bool IsHardware(string? type) => type is not null && All.Contains(type);
}
