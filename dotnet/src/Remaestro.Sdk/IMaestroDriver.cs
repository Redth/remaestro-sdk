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
