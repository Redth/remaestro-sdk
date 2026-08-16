namespace Remaestro.Sdk;

/// <summary>
/// A device type. Implement this and hand it to <see cref="DriverHost.RunAsync"/> to expose your
/// device type to the hub as an out-of-process driver.
/// </summary>
public interface IRemaestroDriver
{
    /// <summary>Stable id for this device type, e.g. "http".</summary>
    string TypeId { get; }
    string DisplayName { get; }
    string Description { get; }

    /// <summary>Config needed to create a device instance of this type.</summary>
    IReadOnlyList<ConfigField> ConfigSchema { get; }

    /// <summary>Commands common to every device of this type (instances may add more).</summary>
    IReadOnlyList<CommandInfo> Commands { get; }

    /// <summary>The events devices of this type emit, with their payload schema.</summary>
    IReadOnlyList<EventSchema> Events => [];

    /// <summary>The state keys devices of this type keep.</summary>
    IReadOnlyList<StateField> StateSchema => [];

    /// <summary>mDNS/Bonjour service types this device advertises as, e.g. "_xbmc-jsonrpc-h._tcp".</summary>
    IReadOnlyList<string> DiscoveryServices => [];

    /// <summary>
    /// What devices of this type are <b>for</b> — see <see cref="DeviceTrait"/>. Lets a list scope itself
    /// to the job at hand: the IR wizard asking for a blaster wants the two types that can transmit, not
    /// all thirty-six. Declared rather than inferred, because only the driver knows.
    /// </summary>
    IReadOnlyList<string> Traits => [];

    /// <summary>
    /// True when this type's devices implement <see cref="INavigableDevice"/> — a browsable content
    /// library (see docs/navigation-spec.md). The host then serves the navigation surface.
    /// </summary>
    bool SupportsNavigation => false;

    /// <summary>
    /// True when this type's devices implement <see cref="IEpgSource"/> — a TV guide of channels and timed
    /// programmes. The host then serves the guide surface and the hub folds it into the grid.
    /// </summary>
    bool SupportsEpg => false;

    /// <summary>
    /// Remotes this driver ships for its devices. They join the hub's template gallery and are offered
    /// when a device of this type is added, so a driver can bring the remote its hardware deserves
    /// instead of relying on a generic archetype.
    /// </summary>
    IReadOnlyList<RemoteTemplateSpec> RemoteTemplates => [];

    /// <summary>
    /// True when this type's devices implement <see cref="IRemoteSurfaceDevice"/> — each one draws the
    /// remote it deserves rather than sharing the type's.
    /// <para>
    /// Declared as well as implemented because the console has to know which of thirty device cards opens a
    /// remote in order to draw them, and asking thirty devices over gRPC on every redraw is not a way to
    /// answer that. The call itself is made when someone taps one. A type that says true and a device that
    /// then answers null is fine and expected — the flag is about the type, the answer is about the unit.
    /// </para>
    /// </summary>
    bool SupportsDeviceRemotes => false;

    /// <summary>
    /// What this driver's devices can do, declared rather than left to be discovered by calling — see
    /// <see cref="DriverCapability"/> for the vocabulary.
    /// <para>
    /// <b>The three <c>Supports*</c> flags above are folded in for you</b>, so a driver that already sets
    /// them need not repeat them here. What this adds is everything they cannot express: a device that
    /// enumerates its inputs, apps or options, or fronts a bridge, was previously knowable only by making
    /// the call and reading a boolean that meant three different things.
    /// </para>
    /// <para>
    /// <b>Declare what you implement.</b> Declaring a capability whose rpc is missing is worse than
    /// declaring nothing — the hub will call it, and the navigation surfaces in particular have no
    /// exception handling at all, so the user gets an error where an undeclared driver would have degraded.
    /// </para>
    /// </summary>
    IReadOnlyList<string> Capabilities => [];

    /// <summary>
    /// Whether this driver's heartbeat keeps beating while it is handling a command.
    /// <para>
    /// <b>True here, and true in fact, for anything built on <see cref="DriverHost"/>.</b> The beat is its
    /// own task writing into the event channel, drained on the <c>StreamEvents</c> stream — a different
    /// HTTP/2 stream from the one a command blocks — and it has been measured going on beating at its
    /// normal cadence with a device stuck for ever inside <c>ExecuteAsync</c>.
    /// </para>
    /// <para>
    /// <b>It is overridable because the protocol asks rather than requires.</b> A driver that takes the
    /// beat into its own hands and couples it to its command loop must say so by returning false; the hub
    /// then never reads that driver's silence as meaning anything. Lying in the true direction is the one
    /// mistake with a cost — it invites a reader to conclude that a busy driver has stopped.
    /// </para>
    /// </summary>
    bool HeartbeatIndependent => true;

    /// <summary>
    /// The oldest hub protocol version this driver will work against, or null — the default — for "as new
    /// as the contract I was built from".
    /// <para>
    /// Null is the safe reading and is almost always right. Override it only once you know your driver uses
    /// nothing newer than some earlier version, which <i>widens</i> the set of hubs it runs on. The floor
    /// can only ever move that way: a driver that raised it would be breaking hubs that already ran it.
    /// </para>
    /// </summary>
    uint? MinHubProtocol => null;

    Task<IRemaestroDevice> CreateDeviceAsync(string deviceId, string name, IReadOnlyDictionary<string, string> config, CancellationToken ct);
}

/// <summary>A live device instance: exposes commands, keeps state, and raises events.</summary>
public interface IRemaestroDevice : IAsyncDisposable
{
    IReadOnlyList<CommandInfo> Commands { get; }

    /// <summary>
    /// What this particular device is for — see <see cref="DeviceTrait"/>. Empty means "whatever my
    /// driver said", which is right for a driver that makes one kind of thing.
    /// <para>
    /// It exists for bridges. Every device behind a Hubitat has TypeId "hubitat", so taking the driver's
    /// traits made a table lamp announce itself as a hub — and anything scoping a list to lighting found
    /// nothing, because the lamp never claimed to be a light.
    /// </para>
    /// </summary>
    IReadOnlyList<string> Traits => [];

    /// <summary>
    /// What this device can be handed to play, if anything. Null — the default — means it can't, which is
    /// the honest answer for a projector, an amplifier or a light. See <see cref="MediaPlayback"/>.
    /// <para>
    /// Per-device rather than per-driver because the same driver can front both: a Zidoo on the network
    /// takes a URL and the same model on an RS-232 cable cannot.
    /// </para>
    /// </summary>
    MediaPlayback? Playback => null;

    bool Online { get; }
    IReadOnlyDictionary<string, string> GetState();
    Task<CommandResult> ExecuteAsync(string commandId, IReadOnlyDictionary<string, string> args, CancellationToken ct);
    event Action<DeviceEvent>? EventRaised;
}
