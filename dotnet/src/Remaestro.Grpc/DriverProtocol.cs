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

/// <summary>
/// The assistant surfaces a tool may be offered on — the slugs that go in
/// <c>AssistantToolDescriptor.surfaces</c>.
/// <para>
/// <b>Here rather than in the C# SDK for the same reason <see cref="DriverCapability"/> is.</b> These are
/// the hub's own prompt-catalogue ids, and naming one means naming a page somebody can open and read. A
/// plugin in any language sends the string; this class exists so the two .NET ends cannot mistype it.
/// </para>
/// <para>
/// <b>Naming a surface is now an external promise.</b> These slugs were internal — for anchors, and for
/// naming a surface in a test failure. Renaming one after this would break every plugin that named it, so
/// they are as fixed as a field number.
/// </para>
/// </summary>
public static class AssistantSurface
{
    /// <summary>
    /// Anything spoken in the house, and the chat on a handset. <b>The one that is opt-in for a tool that
    /// acts</b>, because nobody is necessarily looking — see <c>AssistantToolDescriptor</c>.
    /// </summary>
    public const string Remote = "remote";

    /// <summary>
    /// The assistant panel in the web console: Admin or Operator, typed, on a screen. This is where a tool
    /// that acts belongs by default.
    /// </summary>
    public const string Console = "console";

    /// <summary>The rule editor's assistant.</summary>
    public const string Rules = "rules";

    /// <summary>The activity builder's assistant.</summary>
    public const string Activities = "activities";

    /// <summary>The assistant that repairs a running activity.</summary>
    public const string ActivityDoctor = "activity-doctor";

    /// <summary>
    /// Every surface a tool may name, in the order the hub lists them.
    /// <para>
    /// The hub has two further prompt surfaces — the photo-to-remote reader and the HTTP device advisor —
    /// and they are deliberately absent: both are one-shot prompts with no tool loop at all, so offering a
    /// tool on one would be a declaration nothing could ever honour.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> All = [Remote, Console, Rules, Activities, ActivityDoctor];

    /// <summary>Whether <paramref name="surface"/> is one a tool can actually be offered on.</summary>
    public static bool IsKnown(string? surface) =>
        surface is { Length: > 0 } && All.Contains(surface, StringComparer.Ordinal);
}

/// <summary>
/// How much text a plugin's declared tools may add to what a model is told.
///
/// <para>
/// <b>Why there is a limit at all.</b> A tool description is a prompt: it is the one piece of text a model
/// is meant to act on, and it rides in <i>every</i> request on every surface the tool is offered on, before
/// anybody has said anything. Twenty plugins at five hundred characters each doubles the system prompt on
/// every spoken turn, and on the remote the cost of a longer prompt is a person standing in a room waiting.
/// </para>
/// <para>
/// <b>Why a ceiling and not a fingerprint.</b> The obvious alternative — hash the tool text and notice when
/// it moves — was considered and rejected. The hub's own prompts change rarely and deliberately, which is
/// what makes a moved fingerprint mean something; a plugin's text moves on every plugin update, which is an
/// ordinary and frequent event. A tripwire that fires on ordinary events is worse than no tripwire, because
/// its silence stops meaning anything. A ceiling is the only check here that fails without a person
/// choosing to look.
/// </para>
/// <para>
/// <b>These are floors the hub guarantees, not quotas it hands out.</b> A hub may accept more; it may not
/// accept less. So a plugin that stays inside them is portable, and lowering one later would be a breaking
/// change to this contract rather than a policy tweak.
/// </para>
/// </summary>
public static class AssistantToolLimits
{
    /// <summary>Characters in one tool's description. Roughly a long paragraph.</summary>
    public const int DescriptionChars = 600;

    /// <summary>
    /// Characters one plugin may contribute in total — every tool's name and description together.
    /// <para>
    /// Per plugin as well as per tool, because the per-tool limit alone is defeated by declaring forty
    /// tools, and because a total that names a culprit is the whole point: without it, "which plugin made
    /// the assistant weird" is unanswerable.
    /// </para>
    /// </summary>
    public const int PerPluginChars = 3_000;

    /// <summary>Tools one plugin may declare.</summary>
    public const int ToolsPerPlugin = 20;

    /// <summary>
    /// Characters of answer the hub will carry back from one tool call. Longer answers are <b>truncated
    /// rather than refused</b>, with a line saying so.
    ///
    /// <para>
    /// <b>The arithmetic, because a result is not paid for once.</b> A tool result goes into the
    /// conversation and is re-sent with every later round of the same turn, and a turn runs to about ten
    /// rounds — the assistant's own loop is exactly ten, and the activity doctor keeps a slightly longer
    /// one of its own. So four thousand characters — call it a thousand tokens — is on the order of ten
    /// thousand tokens if it lands in the first round and the model keeps working. That is already the
    /// expensive end of what a single tool ought to say, and it is why this is smaller than a reader
    /// expects rather than larger.
    /// </para>
    /// <para>
    /// <b>Truncated and not refused</b>, which is the opposite of how this codebase treats an over-long
    /// <i>description</i> — and deliberately. A description is read before anybody has spoken, so refusing
    /// it costs a support question and nothing else. An answer is read by a person standing in a room
    /// waiting, and half of one beats an error: a tool that replies with a database is a bug worth
    /// surviving. The truncation says the number so the plugin's author can find out from a screenshot.
    /// </para>
    /// </summary>
    public const int ResultChars = 4_000;

    /// <summary>
    /// How long the hub waits for one tool call before giving up on it.
    ///
    /// <para>
    /// <b>Shorter than an ordinary driver call, and that is the point.</b> A command sent at hardware gets
    /// a minute because a projector really can take that long to warm up, and nobody is necessarily
    /// waiting. A tool call is made <i>inside a turn</i>, with a person who has just said something out
    /// loud, and the hub cannot answer them until it comes back. Ten rounds of a minute each is a hub that
    /// has stopped answering, with nothing on fire.
    /// </para>
    /// <para>
    /// <b>It is no longer the only clock, and it is still the one this contract promises.</b> When this
    /// number was chosen the assistant loop was bounded at ten rounds and four thousand output tokens and
    /// by nothing at all in seconds — which was fine while every arm of the dispatcher was in-process, and
    /// stopped being fine the moment one of them crossed to another process. A hub now also puts a wall
    /// clock on the whole turn (ninety seconds in this one, which is a hub's own policy rather than part of
    /// this contract). The two bound different things and both are wanted: a turn budget ends a turn that
    /// is going nowhere, and this deadline is what stops a single slow tool from spending the whole of that
    /// budget in the first round.
    /// </para>
    /// <para>
    /// <b>Running out is not an answer, at either level, and neither one recalls the work.</b> A deadline
    /// here means the hub stopped waiting — not that the plugin refused, and not that it stopped. Whatever
    /// it began may still be running on the far side, may still take effect, and may still produce a reply
    /// nobody will ever hear; nothing on the hub's side can cancel work already begun. A hub is expected to
    /// say that rather than report a timeout as a result, both in the tool result a model reads and in what
    /// a person is told. So a tool with side effects should assume that a call it never finished answering
    /// was made all the same.
    /// </para>
    /// </summary>
    public static readonly TimeSpan ResultDeadline = TimeSpan.FromSeconds(20);
}
