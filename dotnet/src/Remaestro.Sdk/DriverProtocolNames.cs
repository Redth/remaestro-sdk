namespace Remaestro.Sdk;

/// <summary>
/// The capability strings, under the namespace a driver author already has imported.
///
/// <para>
/// <b>Every value here is the corresponding constant from <see cref="Remaestro.Grpc.DriverCapability"/>,
/// not a copy of its text.</b> They are compile-time constants, so the two cannot drift: change the wire
/// vocabulary and this stops compiling rather than quietly disagreeing with it.
/// </para>
/// <para>
/// It exists because <c>Remaestro.Grpc</c> and <c>Remaestro.Sdk</c> both declare a <c>ConfigField</c> and a
/// <c>StateField</c> — the generated message and the author-facing record — so a driver cannot import both
/// namespaces. The vocabulary belongs with the contract, because a Go plugin sends these strings having
/// read them off <c>driver.proto</c>; this is how a C# author reaches it without an ambiguous using.
/// </para>
/// </summary>
public static class DriverCapability
{
    /// <inheritdoc cref="Remaestro.Grpc.DriverCapability.Inputs"/>
    public const string Inputs = Remaestro.Grpc.DriverCapability.Inputs;

    /// <inheritdoc cref="Remaestro.Grpc.DriverCapability.Epg"/>
    public const string Epg = Remaestro.Grpc.DriverCapability.Epg;

    /// <inheritdoc cref="Remaestro.Grpc.DriverCapability.Apps"/>
    public const string Apps = Remaestro.Grpc.DriverCapability.Apps;

    /// <inheritdoc cref="Remaestro.Grpc.DriverCapability.DeviceRemotes"/>
    public const string DeviceRemotes = Remaestro.Grpc.DriverCapability.DeviceRemotes;

    /// <inheritdoc cref="Remaestro.Grpc.DriverCapability.Bridge"/>
    public const string Bridge = Remaestro.Grpc.DriverCapability.Bridge;

    /// <inheritdoc cref="Remaestro.Grpc.DriverCapability.Options"/>
    public const string Options = Remaestro.Grpc.DriverCapability.Options;

    /// <inheritdoc cref="Remaestro.Grpc.DriverCapability.Navigation"/>
    public const string Navigation = Remaestro.Grpc.DriverCapability.Navigation;

    /// <inheritdoc cref="Remaestro.Grpc.DriverCapability.Diagnostics"/>
    public const string Diagnostics = Remaestro.Grpc.DriverCapability.Diagnostics;
}
