namespace Remaestro.Grpc;

/// <summary>
/// Which revision of <c>driver.proto</c> this build speaks, and how two parties compare theirs.
/// <para>
/// <b>Read from the contract rather than written down beside it.</b> <see cref="Current"/> is the highest
/// value in the proto's own <c>Protocol</c> enum, which is what the file says the current version is — so
/// bumping the protocol is one edit in one place and this cannot drift from it. A constant here would be a
/// second copy of a number, and a second copy of a number is a number that will disagree.
/// </para>
/// <para>
/// Both the hub and a driver read it from here: it is a fact about the contract, not about either end.
/// </para>
/// </summary>
public static class DriverProtocol
{
    /// <summary>
    /// The highest protocol version this build knows, taken from the generated <c>Protocol</c> enum.
    /// <para>
    /// A driver puts this in <c>DriverDescriptor.protocol_version</c>; the hub puts its own in
    /// <c>DescribeRequest.hub_protocol</c>. Both are the same integer the registry manifest calls
    /// <c>abi</c>, deliberately — see the field comments in <c>driver.proto</c>.
    /// </para>
    /// </summary>
    public static readonly uint Current = (uint)Enum.GetValues<Protocol>().Max(v => (int)v);

    /// <summary>
    /// Whether a hub speaking <paramref name="hubProtocol"/> is new enough for a driver built against
    /// <paramref name="driverProtocol"/> that declares <paramref name="minHubProtocol"/> as its floor.
    /// <para>
    /// <b>A null floor is not a zero floor.</b> Unset means "as new as the contract I was built from",
    /// which is the safe reading and the one the registry manifest's single <c>abi</c> integer already
    /// documents. A driver that later works out it only ever used older features declares a lower floor and
    /// widens what it runs on; the floor can only ever move that way.
    /// </para>
    /// <para>
    /// Pure and static so the whole table — old hub, old driver, future plugin, a plugin that widened —
    /// is assertable without a process on either end.
    /// </para>
    /// </summary>
    public static bool HubIsNewEnough(uint hubProtocol, uint driverProtocol, uint? minHubProtocol) =>
        hubProtocol >= (minHubProtocol ?? driverProtocol);
}

/// <summary>
/// The capability strings a driver declares in <c>DriverDescriptor.capabilities</c> — the answer to "what
/// does this driver implement?", given before anything is called instead of inferred from what comes back.
/// <para>
/// <b>Here rather than in the C# SDK, because it is wire vocabulary rather than a C# convenience.</b> The
/// hub reads these strings, a Go or Python plugin sends them having read them off <c>driver.proto</c>, and
/// this class is the same list generated into the same package as the contract itself.
/// </para>
/// <para>
/// An unknown string is ignored rather than refused, so a later hub can name something this build has never
/// heard of; but inventing one buys nothing today.
/// </para>
/// </summary>
public static class DriverCapability
{
    /// <summary>Devices answer <c>ListInputs</c> with a real source list (<c>IInputSourceDevice</c>).</summary>
    public const string Inputs = "inputs";

    /// <summary>Devices answer <c>GetEpg</c> (<c>IEpgSource</c>). Supersedes <c>supports_epg</c>.</summary>
    public const string Epg = "epg";

    /// <summary>Devices answer <c>ListApps</c> (<c>IAppListDevice</c>).</summary>
    public const string Apps = "apps";

    /// <summary>
    /// Devices answer <c>GetRemote</c> (<c>IRemoteSurfaceDevice</c>). Supersedes
    /// <c>supports_device_remotes</c>.
    /// </summary>
    public const string DeviceRemotes = "device-remotes";

    /// <summary>Devices answer <c>ListBridgedDevices</c> (<c>IBridgeDevice</c>).</summary>
    public const string Bridge = "bridge";

    /// <summary>Devices answer <c>ListOptions</c> for a config field's options key (<c>IOptionSourceDevice</c>).</summary>
    public const string Options = "options";

    /// <summary>
    /// Devices answer <c>Browse</c>/<c>GetNode</c>/<c>SearchNodes</c>/<c>InvokeItem</c>
    /// (<c>INavigableDevice</c>). Supersedes <c>supports_navigation</c>.
    /// <para>
    /// <b>Declaring this one is a promise with teeth.</b> The hub's navigation service has no exception
    /// handling around browse or search, so a driver that declares navigation and does not implement all
    /// four surfaces an error to the user rather than degrading quietly. That is the correct behaviour and
    /// it is why this is worth saying out loud rather than leaving to be discovered.
    /// </para>
    /// </summary>
    public const string Navigation = "navigation";

    /// <summary>The driver answers <c>SetDiagnostics</c>/<c>GetDiagnostics</c> with real captured traffic.</summary>
    public const string Diagnostics = "diagnostics";
}
