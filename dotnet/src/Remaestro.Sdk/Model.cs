namespace Remaestro.Sdk;

/// <summary>One value a field will accept, as offered to whoever's filling it in.</summary>
/// <param name="Value">What actually gets sent.</param>
/// <param name="Label">What the user reads. Falls back to <paramref name="Value"/> when blank.</param>
/// <param name="Detail">Optional extra — a note, a source's real id, whatever helps someone choose.</param>
/// <param name="Current">This is the value the device reports it's on right now.</param>
public sealed record FieldOption(string Value, string Label = "", string Detail = "", bool Current = false);

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
/// picker chooses, a codeset the remote editor writes. Never shown as something to type into — offering
/// an empty box for a value the user can't know is worse than not asking.
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
    string? ShowWhen = null)
{
    /// <summary>A number field with a range — worth a slider rather than a text box.</summary>
    public bool HasRange => Type == "number" && Min is not null && Max is not null;

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
