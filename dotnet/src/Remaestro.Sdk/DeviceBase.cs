using System.Collections.Concurrent;

namespace Remaestro.Sdk;

/// <summary>Event types the hub itself acts on, rather than passing to rules.</summary>
public static class DeviceEvents
{
    /// <summary>
    /// This device's command list has changed. The hub answers by re-reading it — the only thing that
    /// prompts it to, since it takes the list it was given at creation and keeps it.
    /// </summary>
    public const string CommandsChanged = "device.commands_changed";

    /// <summary>This device has worked out what it is. Same deal as <see cref="CommandsChanged"/>.</summary>
    public const string TraitsChanged = "device.traits_changed";

    /// <summary>
    /// This device has learned something about itself worth keeping — see
    /// <see cref="DeviceBase.LearnConfig"/>. The data is the config keys and their new values; the hub
    /// saves them and leaves the device running.
    /// </summary>
    public const string ConfigLearned = "device.config_learned";

    /// <summary>
    /// The driver process's periodic account of its own runtime — see <see cref="DriverRuntime"/>. Unlike
    /// every other event here this one is about <b>no device</b>: it carries an empty device id, because it
    /// describes the process hosting them all. The hub takes it off the stream before the event bus ever
    /// sees it, so it reaches neither rules nor a device's state refresh.
    /// </summary>
    public const string DriverHeartbeat = "driver.heartbeat";

    /// <summary>
    /// The driver saying it is deliberately waiting, and until when — see <see cref="DeviceBase.Hold"/>.
    /// Like the heartbeat it is taken off the stream before the event bus sees it, so no rule can be
    /// written against it and none fires because a bridge is waiting to be paired.
    /// </summary>
    public const string DriverHold = "driver.hold";

    /// <summary>
    /// Whether an event is the hub talking to itself. These travel the same bus as real device events —
    /// that's how they prompt a re-read — so anything a user aimed at "any event" has to skip them, or a
    /// rule fires because a lamp finished describing itself.
    /// </summary>
    public static bool IsInternal(string type) =>
        type is CommandsChanged or TraitsChanged or ConfigLearned or DriverHeartbeat or DriverHold;

    /// <summary>
    /// The data keys a <see cref="DriverHold"/> event carries. They exist because a device raises events
    /// through one string-keyed channel; <c>DriverHost</c> lifts them straight back into the typed
    /// <c>DriverHoldMessage</c> on the wire, so nothing string-shaped ever reaches the hub.
    /// </summary>
    public static class HoldKeys
    {
        public const string Id = "id";
        public const string Reason = "reason";
        public const string UntilUnixMs = "untilUnixMs";
        public const string Released = "released";
    }
}

/// <summary>Convenience base for devices: manages a state bag and event raising.</summary>
public abstract class DeviceBase : IRemaestroDevice
{
    private readonly ConcurrentDictionary<string, string> _state = new();

    protected DeviceBase(string deviceId, string name)
    {
        DeviceId = deviceId;
        Name = name;
    }

    public string DeviceId { get; }
    public string Name { get; }

    public abstract IReadOnlyList<CommandInfo> Commands { get; }

    /// <summary>
    /// Whether this device is answering — the driver's own <c>online</c> state key, when it keeps one.
    /// <para>
    /// There are two ways a driver says this and they used to be able to disagree. Most drivers report
    /// reachability by writing <c>online</c> into their state; a handful override this property. Only the
    /// property reaches the hub as <c>DeviceStateMessage.Online</c> — which is the dot on a device's card,
    /// the "'X' is back" event, <c>device.*.online</c> in a rule, and the answer the connection test reads —
    /// so for the two dozen drivers that only wrote the state key, all of that said <i>answering</i>
    /// permanently, whatever the device was actually doing. A webOS television sat on the Devices page with
    /// a green dot and a red "there's no route to it" chip beside it, and pressing Test said "Connected"
    /// before the driver had opened a socket, because the default here was an unconditional <c>true</c>.
    /// </para>
    /// <para>
    /// So the state key is the answer. A driver whose reachability isn't a state key still overrides this,
    /// and its override wins.
    /// </para>
    /// <para>
    /// <b>Absent is not yes.</b> This used to answer <c>true</c> when the key was missing, on the reading
    /// that a device which never states one is assumed there. That reading cannot tell "this device has no
    /// reachability to report" from "this device has one and has not reported it yet", and the second is
    /// the common case: a driver that writes <c>online</c> from its poll loop has an empty state bag for
    /// the second between construction and the first answer. Kodi and Zidoo spent that second with a green
    /// dot, a "Ready" chip and a connection test saying <i>Connected</i>, for a box nothing had spoken to.
    /// </para>
    /// <para>
    /// So the two meanings are separated by making devices say it: every driver in the tree writes the key
    /// in its constructor or overrides this property, including the two that genuinely have nothing to
    /// reach — an activity and a webhook both declare <c>online=true</c> outright, which is a claim someone
    /// can read and disagree with rather than one inherited by saying nothing.
    /// </para>
    /// <para>
    /// The default is now the safe direction rather than the convenient one. A driver that forgets reports
    /// itself unreachable, which shows up on its own card the first time anyone looks; the old default let
    /// it report itself reachable, which shows up nowhere. That asymmetry is the whole of why
    /// "don't let <c>Online</c> be unconditionally true" is a rule.
    /// </para>
    /// </summary>
    public virtual bool Online =>
        _state.TryGetValue("online", out var online)
        && !string.IsNullOrWhiteSpace(online)
        && !online.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);

    public event Action<DeviceEvent>? EventRaised;

    public IReadOnlyDictionary<string, string> GetState() => new Dictionary<string, string>(_state);

    protected void SetState(string key, string? value)
    {
        if (value is null) _state.TryRemove(key, out _);
        else _state[key] = value;
    }

    protected void Emit(string type, IReadOnlyDictionary<string, string>? data = null)
        => EventRaised?.Invoke(new DeviceEvent(type, data));

    /// <summary>
    /// The one way to publish a command list that isn't known until the device has talked to its hardware
    /// — a Hubitat child learns what it can do from the hub, and has nothing to say at construction.
    /// <para>
    /// Store the list here and return it from <see cref="Commands"/>. The hub asked once, at creation,
    /// and kept that answer; nothing prompts it to ask again except an event from the device. So this
    /// raises one when — and only when — the list actually changed. Assigning a field instead leaves the
    /// device permanently empty in the UI, with nothing anywhere reporting a problem.
    /// </para>
    /// </summary>
    protected void SetCommands(IReadOnlyList<CommandInfo> commands)
    {
        // Compare on what the hub renders. A fresh list object every poll is normal and must stay quiet.
        var signature = string.Join(";", commands.Select(c => $"{c.Id}:{string.Join(",", c.Parameters?.Select(p => p.Key) ?? [])}"));
        if (signature == _commandSignature) return;

        _commandSignature = signature;
        _commands = commands;
        Emit(DeviceEvents.CommandsChanged);
    }

    /// <summary>What <see cref="SetCommands"/> last stored. Empty until a device says otherwise.</summary>
    protected IReadOnlyList<CommandInfo> DynamicCommands => _commands;

    volatile IReadOnlyList<CommandInfo> _commands = [];
    string _commandSignature = "";

    public virtual IReadOnlyList<string> Traits => _traits;

    /// <summary>
    /// What this device can be handed to play. Null — the default — means it can't, which is the honest
    /// answer for most devices. Overridden by the ones that can. See <see cref="MediaPlayback"/>.
    /// </summary>
    public virtual MediaPlayback? Playback => null;

    /// <summary>
    /// Say what this device is for — see <see cref="DeviceTrait"/> — once it knows. Same reason as
    /// <see cref="SetCommands"/>: a bridge's child learns whether it's a lamp or a lock from the hub,
    /// long after the hub asked, and every child otherwise inherits the bridge's own "I am a hub".
    /// </summary>
    protected void SetTraits(params string[] traits) => SetTraits((IReadOnlyList<string>)traits);

    /// <inheritdoc cref="SetTraits(string[])"/>
    protected void SetTraits(IReadOnlyList<string> traits)
    {
        var signature = string.Join(",", traits.OrderBy(t => t, StringComparer.Ordinal));
        if (signature == _traitSignature) return;

        _traitSignature = signature;
        _traits = traits;
        Emit(DeviceEvents.TraitsChanged);
    }

    volatile IReadOnlyList<string> _traits = [];
    string _traitSignature = "";

    /// <summary>
    /// Save something this device found out about itself back into its own saved configuration — the
    /// address it turned out to be at, the identity it answers to. The hub writes the values into the
    /// stored device and leaves it running; nothing is torn down and restarted.
    /// <para>
    /// Only for values a device can establish more reliably than a person can type. Anything a person
    /// chose on purpose is theirs, and a driver overwriting it is a driver arguing with its owner. Pass
    /// only the keys that actually changed — the hub ignores the rest, but the event is cheaper unsent.
    /// </para>
    /// </summary>
    protected void LearnConfig(IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == 0) return;
        Emit(DeviceEvents.ConfigLearned, values);
    }

    /// <summary>
    /// Say that this device is deliberately waiting, and roughly for how long. Returns a token; disposing
    /// it — or letting a <c>using</c> scope end — releases the hold.
    /// <para>
    /// <b>What it buys.</b> The hub can see that a call has been outstanding for ten minutes; it cannot see
    /// whether that is a wedge or a pairing wait for somebody to walk over and press a button. Only this
    /// process knows, and without this it has no way to say. With it, the sentence in front of the user
    /// stops being "ExecuteCommand unanswered for 10 min" and becomes what the wait is actually for.
    /// </para>
    /// <para>
    /// <b>Release every hold, including the ones that failed.</b> The token does that on dispose and on a
    /// throw, which is why it is a token and not a pair of methods: a hold left open is indistinguishable
    /// from the wedge it existed to explain, so the field would end up hiding what it was added to reveal.
    /// </para>
    /// </summary>
    /// <param name="reason">
    /// One phrase, for whoever is looking at the screen — "waiting for the button on the bridge". It
    /// replaces the hub's own sentence, so it has to say what the wait is <i>for</i>.
    /// </param>
    /// <param name="until">When the wait is expected to end, or null when that genuinely isn't knowable.</param>
    protected IDisposable Hold(string reason, DateTimeOffset? until = null)
    {
        var id = $"{DeviceId}:{Interlocked.Increment(ref _holdSeq)}";
        Emit(DeviceEvents.DriverHold, new Dictionary<string, string>
        {
            [DeviceEvents.HoldKeys.Id] = id,
            [DeviceEvents.HoldKeys.Reason] = reason,
            [DeviceEvents.HoldKeys.UntilUnixMs] = (until?.ToUnixTimeMilliseconds() ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        return new HoldToken(this, id);
    }

    int _holdSeq;

    sealed class HoldToken(DeviceBase device, string id) : IDisposable
    {
        int _done;

        public void Dispose()
        {
            // Idempotent: a `using` inside a retry loop, or a dispose after an explicit release, must not
            // send a second end for a hold the hub has already closed.
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            device.Emit(DeviceEvents.DriverHold, new Dictionary<string, string>
            {
                [DeviceEvents.HoldKeys.Id] = id,
                [DeviceEvents.HoldKeys.Released] = "true",
            });
        }
    }

    public abstract Task<CommandResult> ExecuteAsync(string commandId, IReadOnlyDictionary<string, string> args, CancellationToken ct);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
