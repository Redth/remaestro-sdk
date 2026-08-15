namespace Remaestro.Sdk;

/// <summary>
/// What a device type is <b>for</b>, as opposed to what it can be told to do.
/// <para>
/// The command vocabulary already answers "can this thing change volume?" — but only once a device exists
/// and has told us its commands. A trait answers "would this kind of thing ever be an IR blaster?", which
/// is a question you have to answer <i>before</i> anything exists: the IR wizard offering to add a blaster
/// was listing all thirty-six device types, including a Jellyfin server and a light bulb.
/// </para>
/// <para>
/// Traits are deliberately few and about role, not category. "Would I pick this for that job?" is the test.
/// A driver declares the roles its devices can genuinely serve; anything scoping a list to a role gets a
/// short, correct list instead of everything.
/// </para>
/// </summary>
public static class DeviceTrait
{
    /// <summary>Can transmit infrared. An IR blaster, or anything with an emitter attached.</summary>
    public const string IrEmitter = "ir.emitter";

    /// <summary>Can learn infrared — capture a code from a physical remote.</summary>
    public const string IrReceiver = "ir.receiver";

    /// <summary>Produces button presses for reMaestro to act on: a remote, a gamepad, a keyboard.</summary>
    public const string InputSource = "input.source";

    /// <summary>Fronts other devices — a hub, a bridge, a multiroom system, a power controller.</summary>
    public const string Bridge = "bridge";

    /// <summary>Has a browsable library of content (see docs/navigation-spec.md).</summary>
    public const string MediaLibrary = "media.library";

    /// <summary>Can play a stream handed to it — the thing an activity sends a film to.</summary>
    public const string MediaPlayer = "media.player";

    /// <summary>Shows a picture: a TV, a projector, a monitor.</summary>
    public const string Display = "display";

    /// <summary>Makes sound and takes a volume: a receiver, an amp, a speaker.</summary>
    public const string Audio = "audio";

    /// <summary>Switches what's on a display or through an amp — an AVR, a matrix.</summary>
    public const string InputSwitcher = "input.switcher";

    /// <summary>Controls mains power to something else.</summary>
    public const string Power = "power";

    /// <summary>A light, or a group of them.</summary>
    public const string Lighting = "lighting";

    /// <summary>Relays commands on another device's behalf — an ESP32 satellite, a Global Caché.</summary>
    public const string Proxy = "proxy";

    /// <summary>
    /// Something that moves out of the way: a projection screen, a projector lift, masking, a blind, a
    /// shade. They share a shape — open, close, stop — and most of them can't say where they are.
    /// </summary>
    public const string Cover = "cover";

    static readonly string[] LightWords =
        ["light", "lamp", "bulb", "sconce", "chandelier", "pendant", "downlight", "lantern", "led", "dimmer"];

    /// <summary>
    /// Whether a plain on/off thing is a light, going by what it's called.
    /// <para>
    /// Hubs are the reason this exists. A Hubitat or Home Assistant switch is a lamp, a fan, or a coffee
    /// maker, and nothing in the capability list distinguishes them — but the owner named it "Kitchen
    /// Lamp", and that's the word they'll use when they ask for the lights to come on.
    /// </para>
    /// </summary>
    /// <param name="names">The label, and anything else that describes it — a driver or model name.</param>
    public static bool LooksLikeLighting(params string?[] names)
        => names.Any(n => n is { Length: > 0 } && LightWords.Any(w => n.Contains(w, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Readable names, for a picker that has scoped itself to one and wants to say so.</summary>
    public static string Label(string trait) => trait switch
    {
        IrEmitter => "IR blaster",
        IrReceiver => "IR receiver",
        InputSource => "input source",
        Bridge => "hub or bridge",
        MediaLibrary => "media library",
        MediaPlayer => "media player",
        Display => "display",
        Audio => "audio device",
        InputSwitcher => "input switcher",
        Power => "power controller",
        Lighting => "light",
        Proxy => "proxy",
        Cover => "screen or blind",
        _ => trait,
    };
}
