// Package hello is the Hello World driver: one fictional device — a rubber duck — written from the
// published `driver.proto` and nothing else.
//
// **"Does not do much" is a constraint here, not an absence.** A plugin that does *nothing* cannot tell
// installed-and-working apart from installed-and-broken, which is the only question a hello-world exists
// to answer. So this one has exactly one observable effect and it is deliberately boring: the duck says
// hello on a fixed interval while it is awake, and `hellos` counts how many times. A number that is
// going up is a proof; a device card that merely exists is not.
//
// Read it beside `samples/go` (the metronome) in the SDK, which is the same language doing the opposite
// job: that one exercises every optional corner of the contract, and this one is the floor.
package hello

import (
	"context"
	"strconv"
	"strings"
	"sync"
	"time"

	pb "example.com/helloworld/gen/maestro"
)

// How often an awake duck says hello. Slow enough to be boring, fast enough that a person watching a
// device card sees the number move before they lose interest.
const helloEvery = 10 * time.Second

// How often the heartbeat frame goes out. Declared on every frame, because a hub's own default is 30 s
// and a plugin that beats more slowly than that and says nothing is reported as stopped while working.
const beatEvery = 5 * time.Second

// The greeting a duck uses when nobody set one. The default lives here and *also* in the ConfigField's
// `default_value` below, and the two have to agree: nothing hub-side applies a declared default before
// `CreateDevice`, so a field the person never touched arrives absent rather than defaulted.
const defaultGreeting = "Hello, world!"

// currentProtocol is the highest value in the proto's `Protocol` enum, computed rather than written down.
// The proto states that this *is* the definition of the current version, so a plugin generated from it
// can work the number out and cannot drift away from the file it generated from.
func currentProtocol() uint32 {
	var highest int32
	for v := range pb.Protocol_name {
		if v > highest {
			highest = v
		}
	}
	return uint32(highest)
}

type duck struct {
	id       string
	name     string
	awake    bool
	greeting string
	hellos   int64
}

// Driver is one process hosting however many ducks a hub has been told about. Every device lives in
// `ducks`, and the map is rebuilt from scratch on every launch — a hub replays `CreateDevice` for each
// stored device each time it starts the process, so nothing here is persisted and nothing needs to be.
type Driver struct {
	pb.UnimplementedDriverServer // every rpc this plugin does not implement answers UNIMPLEMENTED

	typeID string

	mu    sync.Mutex
	ducks map[string]*duck

	subs   map[chan *pb.DeviceEventMessage]struct{}
	subsMu sync.Mutex
}

func NewDriver(typeID string) *Driver {
	d := &Driver{
		typeID: typeID,
		ducks:  map[string]*duck{},
		subs:   map[chan *pb.DeviceEventMessage]struct{}{},
	}
	go d.beat()
	go d.sayHello()
	return d
}

// ---- Describe -------------------------------------------------------------------------------------

// Describe is the **only** rpc a plugin is required to implement. A hub launches the process, calls this
// in a retry loop for about ten seconds, records the answer and kills the process; everything else on
// this service is called on demand, and a hub that never gets an answer here never gets any further.
//
// So it must answer cold, with no setup done and nothing connected. Do not read a device here.
func (d *Driver) Describe(ctx context.Context, req *pb.DescribeRequest) (*pb.DriverDescriptor, error) {
	return &pb.DriverDescriptor{
		TypeId:      d.typeID,
		DisplayName: "Hello World",
		Description: "A rubber duck that is not there. It says hello on a timer and counts how many " +
			"times, so that \"is this working?\" has a number for an answer.",

		ConfigSchema: []*pb.ConfigField{
			{
				Key: "greeting", Label: "Greeting", Type: "string",
				DefaultValue: defaultGreeting,
				Help: "What the duck says. It comes back out in the device's state, which is how you " +
					"can tell your value made it all the way through.",
			},
		},

		// **Both from `CommandVocabulary`, and that is not a detail.** A command id the hub's canonical
		// vocabulary does not know still works — it appears on the device's own toolbox and nowhere else
		// — but it is invisible to the assistant, to remotes and to activity generation, silently. This
		// is the sample every stranger will clone, so it uses two ids the vocabulary actually resolves.
		Commands: []*pb.CommandDescriptor{
			{Id: "power.on", Label: "Say hello", Description: "The duck wakes up and starts greeting the room."},
			{Id: "power.off", Label: "Hush", Description: "The duck stops. It keeps the count."},
		},

		Events: []*pb.EventDescriptor{
			{
				Type: "hello.said", Description: "The duck said hello.",
				Fields: []*pb.EventField{
					{Key: "greeting", Type: "string", Description: "What it said."},
					{Key: "count", Type: "number", Description: "How many times it has now said it."},
				},
			},
		},

		StateSchema: []*pb.StateField{
			{Key: "awake", Type: "bool"},
			{Key: "greeting", Type: "string"},
			{Key: "hellos", Type: "number"},
		},

		// **"power", and the argument for it is in the hub's own source rather than in taste.** `traits`
		// is a closed vocabulary of thirteen and the contract publishes three of them and an ellipsis; an
		// unknown one is accepted, labelled as itself, and does nothing anywhere. Of the thirteen,
		// `power` is the one whose shelf is described by `DeviceCategories` as holding "the only things
		// left here … the ones that do nothing else", and it is the only trait that draws **no** generic
		// remote template — `audio` would hand a rubber duck a full AV-receiver layout, and `display`
		// would put it into activity generation. A joke device must not be load-bearing anywhere.
		Traits: []string{"power"},

		// The highest `Protocol` value this driver was built against. The same integer as `abi` in both
		// `plugin.json` files.
		ProtocolVersion: currentProtocol(),

		// `min_hub_protocol` is left unset on purpose: unset means "the floor is protocol_version", which
		// is the safe reading, and a floor may only ever move *down* over a plugin's life.

		// **Left empty, deliberately, and this is the other half of a rule worth knowing.** `capabilities`
		// is authoritative and all-or-nothing: a non-empty list turns the three `supports_*` booleans off
		// entirely, so anything a plugin does has to be in it. This plugin does nothing optional, so it
		// says nothing — which is a different statement from the metronome's `["diagnostics"]` and is the
		// case a stranger reaches first.
		SupportsNavigation:    false,
		SupportsEpg:           false,
		SupportsDeviceRemotes: false,
	}, nil
}

// ---- Devices --------------------------------------------------------------------------------------

func (d *Driver) CreateDevice(ctx context.Context, req *pb.CreateDeviceRequest) (*pb.CreateDeviceResponse, error) {
	greeting := strings.TrimSpace(req.GetConfig()["greeting"])
	if greeting == "" {
		// Nothing hub-side applies the `default_value` this driver declared, and nothing hub-side checks
		// `required`, a range or an options list either: the console's form is the only thing that asks,
		// and the HTTP API takes an arbitrary dictionary. So every driver defaults and validates its own
		// config, every time.
		greeting = defaultGreeting
	}
	if len([]rune(greeting)) > 120 {
		// A refusal the *device* makes, as `ok: false` with a sentence a person can act on. Returning a
		// gRPC error here instead would be reported as the driver having failed rather than as the device
		// having declined, and those are different facts shown with different words.
		return &pb.CreateDeviceResponse{Ok: false, Error: "a greeting longer than 120 characters is more of a speech"}, nil
	}

	d.mu.Lock()
	d.ducks[req.GetDeviceId()] = &duck{id: req.GetDeviceId(), name: req.GetName(), greeting: greeting}
	d.mu.Unlock()
	return &pb.CreateDeviceResponse{Ok: true}, nil
}

func (d *Driver) DisposeDevice(ctx context.Context, ref *pb.DeviceRef) (*pb.DisposeResponse, error) {
	d.mu.Lock()
	delete(d.ducks, ref.GetDeviceId())
	d.mu.Unlock()
	return &pb.DisposeResponse{Ok: true}, nil
}

// GetState answers with the **whole** state map, every time. A hub replaces what it holds with what this
// returns — it does not merge — so a key left out is a key that stops existing. The two fields beside
// `state` on this message, `commands_changed` and `traits_changed`, have the opposite rule and say so in
// their own comments, which is what makes this one easy to get backwards.
func (d *Driver) GetState(ctx context.Context, ref *pb.DeviceRef) (*pb.DeviceStateMessage, error) {
	d.mu.Lock()
	defer d.mu.Unlock()
	dk, ok := d.ducks[ref.GetDeviceId()]
	if !ok {
		// `online` is a plain proto3 bool, so an unknown id answered with a zero message reads as a
		// device that exists and is offline. That is the true statement here anyway.
		return &pb.DeviceStateMessage{Online: false}, nil
	}
	return &pb.DeviceStateMessage{
		Online: true,
		State: map[string]string{
			"awake":    strconv.FormatBool(dk.awake),
			"greeting": dk.greeting,
			"hellos":   strconv.FormatInt(dk.hellos, 10),
		},
	}, nil
}

func (d *Driver) ExecuteCommand(ctx context.Context, req *pb.ExecuteCommandRequest) (*pb.ExecuteCommandResponse, error) {
	d.mu.Lock()
	dk, ok := d.ducks[req.GetDeviceId()]
	if !ok {
		d.mu.Unlock()
		return &pb.ExecuteCommandResponse{Ok: false, Error: "no such device on this driver"}, nil
	}

	var said *pb.DeviceEventMessage
	switch req.GetCommandId() {
	case "power.on":
		// **Waking up says hello at once, and then goes on saying it on the timer.** Two observables from
		// one duck, on purpose: the immediate one answers "did my command reach the far side of the gRPC
		// call", which is what somebody bringing a hub up wants and wants *now*; the timed one answers
		// "is this process still alive and still being read", which nobody can ask by pressing a button.
		dk.awake = true
		said = dk.say()
	case "power.off":
		dk.awake = false
	default:
		d.mu.Unlock()
		return &pb.ExecuteCommandResponse{Ok: false, Error: "the duck does not know how to " + req.GetCommandId()}, nil
	}
	count := dk.hellos
	d.mu.Unlock()

	if said != nil {
		d.publish(said)
	}
	// `result` is the command's own answer, and it is the cheapest possible proof for a caller that does
	// not want to go and read the state afterwards.
	return &pb.ExecuteCommandResponse{Ok: true, Result: map[string]string{"hellos": strconv.FormatInt(count, 10)}}, nil
}

// say records one hello and builds the frame for it. **The caller must hold `d.mu` and must publish the
// frame after releasing it** — publishing under the device lock is how a slow subscriber becomes a stall
// in every command on the driver.
func (dk *duck) say() *pb.DeviceEventMessage {
	dk.hellos++
	return &pb.DeviceEventMessage{
		DeviceId: dk.id, Type: "hello.said", TimestampUnixMs: nowMs(),
		Data: map[string]string{
			"greeting": dk.greeting,
			"count":    strconv.FormatInt(dk.hellos, 10),
		},
	}
}

// ---- The event stream -----------------------------------------------------------------------------

// StreamEvents is opened once, when a hub connects, and **must not end**. A hub reads it until *it*
// cancels.
//
// This is the sharpest trap in the whole contract, because returning is the natural shape in most
// languages — a `for` over a channel that closes, a generator that runs out, a callback loop whose
// condition goes false — and **returning is completely silent**. Measured in `#427`: with the stream
// ended, every unary call still answers, the liveness reading stays green, diagnostics still record, and
// every device event and every hold is gone for the life of the process. Nothing reconnects and nothing
// is logged.
//
// So there is exactly one way out of the loop below and it is the hub's own cancellation.
func (d *Driver) StreamEvents(_ *pb.StreamEventsRequest, out pb.Driver_StreamEventsServer) error {
	ch := make(chan *pb.DeviceEventMessage, 32)
	d.subsMu.Lock()
	d.subs[ch] = struct{}{}
	d.subsMu.Unlock()
	defer func() {
		d.subsMu.Lock()
		delete(d.subs, ch)
		d.subsMu.Unlock()
	}()

	// The first beat goes out immediately, before any wait. That is what lets a hub tell a driver too old
	// to beat at all from one that has simply not ticked yet.
	if err := out.Send(d.heartbeat()); err != nil {
		return err
	}

	for {
		select {
		case <-out.Context().Done():
			return nil
		case msg := <-ch:
			if err := out.Send(msg); err != nil {
				return err
			}
		}
	}
}

func (d *Driver) publish(msg *pb.DeviceEventMessage) {
	d.subsMu.Lock()
	defer d.subsMu.Unlock()
	for ch := range d.subs {
		select {
		case ch <- msg:
		default: // a subscriber that cannot keep up loses frames rather than blocking the duck
		}
	}
}

func (d *Driver) heartbeat() *pb.DeviceEventMessage {
	interval := uint32(beatEvery / time.Millisecond)
	independent := true
	return &pb.DeviceEventMessage{
		// `device_id` is empty: the frame is about the process, not about any device it hosts.
		Type: "driver.heartbeat",
		// A hub takes the *age* of a beat from this field and not from when the frame arrived. Left at
		// proto3's zero it reads as 1970 — measured as a silence of fifty-six years from a process two
		// seconds old — and the plugin is reported stopped for ever while beating happily.
		TimestampUnixMs: nowMs(),
		// The submessage is what routes the frame. A frame carrying the type string and no `runtime` is
		// not a heartbeat that got lost; it is an ordinary device event with an empty device id.
		Runtime: &pb.DriverRuntimeMessage{
			HeartbeatIntervalMs: &interval,
			// True, and true by construction: the beat is its own goroutine writing into the channel the
			// stream drains, so nothing a device does can stop it.
			HeartbeatIndependent: &independent,
			// Every numeric field on this message has explicit presence, and unset means "not taken". A Go
			// plugin has no equivalent of the .NET GC counters, so it sends none of them rather than
			// sending zeroes that a screen would draw as measurements.
		},
	}
}

func (d *Driver) beat() {
	for range time.Tick(beatEvery) {
		d.publish(d.heartbeat())
	}
}

// sayHello is the one observable effect, and it is on a wall clock rather than on anything a person did
// — so a hub that has installed this plugin and launched it produces a number that moves without anyone
// touching it. That is the whole diagnostic value of a hello world.
func (d *Driver) sayHello() {
	for range time.Tick(helloEvery) {
		d.mu.Lock()
		var due []*pb.DeviceEventMessage
		for _, dk := range d.ducks {
			if !dk.awake {
				continue
			}
			due = append(due, dk.say())
		}
		d.mu.Unlock()
		for _, m := range due {
			d.publish(m)
		}
	}
}

func nowMs() int64 { return time.Now().UnixMilli() }
