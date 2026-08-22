// Package metronome is the Example Metronome driver: one fake device kind, no hardware, written from
// `driver.proto` alone.
//
// It exists to be read next to `samples/python/`, and it is deliberately the *other* shape in three
// places: its heartbeat is genuinely independent where the lamp's is not, it declares `diagnostics` as a
// capability, and it takes a hold on a command that waits.
package metronome

import (
	"context"
	"fmt"
	"log"
	"strconv"
	"strings"
	"sync"
	"time"

	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"

	pb "example.com/metronome/gen/maestro"
)

const (
	// The device type this driver hosts. It is **not** the plugin id: `plugin.json` says
	// `com.example.metronome` and this says `example-metronome`, and the two are used for different
	// things — see README §"A plugin id and a type id are different names".
	typeID = "example-metronome"

	// How often the heartbeat frame goes out. Declared on every frame, because the hub's own default is 30 s
	// and a plugin that beats more slowly than that and says nothing is reported as stopped while working.
	beatEvery = 5 * time.Second
)

// currentProtocol is the highest value in the proto's `Protocol` enum, computed rather than written down.
// The proto states that this *is* the definition of the current version, so a generated plugin can work it
// out and cannot drift away from the file it generated from.
func currentProtocol() uint32 {
	var highest int32
	for v := range pb.Protocol_name {
		if v > highest {
			highest = v
		}
	}
	return uint32(highest)
}

type device struct {
	id      string
	name    string
	running bool
	bpm     int
	beats   int64
}

// Driver is one process hosting many devices. Every device this driver has been told about lives in
// `devices`, and it is rebuilt from scratch on every hub start — the hub replays `CreateDevice` for each
// stored device on every launch, so nothing here is persisted and nothing needs to be.
type Driver struct {
	pb.UnimplementedDriverServer // every rpc this plugin does not implement answers UNIMPLEMENTED

	mu      sync.Mutex
	devices map[string]*device

	subs   map[chan *pb.DeviceEventMessage]struct{}
	subsMu sync.Mutex

	diag *capture
}

func NewDriver() *Driver {
	d := &Driver{
		devices: map[string]*device{},
		subs:    map[chan *pb.DeviceEventMessage]struct{}{},
		diag:    newCapture(),
	}
	go d.beat()
	go d.tick()
	return d
}

// ---- Describe -----------------------------------------------------------------------------------------

func (d *Driver) Describe(ctx context.Context, req *pb.DescribeRequest) (*pb.DriverDescriptor, error) {
	// The hub says how new it is. A driver may use this to leave out fields the hub cannot read, or to log
	// a line its author will understand. **It must not refuse.** Refusing is the hub's job, because the hub
	// is the party with a screen to explain it on.
	if req.GetHubProtocol() < currentProtocol() {
		log.Printf("this hub speaks protocol %d and I was built against %d — answering anyway",
			req.GetHubProtocol(), currentProtocol())
	}

	return &pb.DriverDescriptor{
		TypeId:      typeID,
		DisplayName: "Example Metronome",
		Description: "A metronome that does not exist, so that a plugin written in Go can be seen to work.",

		ConfigSchema: []*pb.ConfigField{
			{
				Key: "bpm", Label: "Tempo", Type: "number",
				DefaultValue: "90",
				Help:         "Beats per minute. Every config value crosses the wire as a string; parsing and ranging are yours.",
			},
			{
				Key: "studio_token", Label: "Studio token", Type: "secret",
				// Declared rather than left to the hub's word list. `studio_token` happens to contain a
				// word the heuristics know, which is exactly why declaring is worth doing: a field called
				// `cue` or `handshake` would not be, and the heuristics cannot tell.
				Sensitivity: pb.Sensitivity_SENSITIVITY_SENSITIVE,
				Help:        "Pretend credential. It is sent on the fake wire so a captured diagnostic has something to redact.",
			},
		},

		Commands: []*pb.CommandDescriptor{
			{Id: "power.on", Label: "Start"},
			{Id: "power.off", Label: "Stop"},
			{
				Id: "metronome.tempo", Label: "Set tempo",
				Description: "A command id the hub's CommandVocabulary does not know: it is on the device's own toolbox and invisible to the assistant, to remotes and to activities.",
				Parameters: []*pb.ConfigField{
					{Key: "bpm", Label: "Beats per minute", Type: "number", Required: true, DefaultValue: "90"},
				},
			},
			{Id: "metronome.calibrate", Label: "Calibrate", Description: "Waits, and says on the wire that it is waiting."},
		},

		Events: []*pb.EventDescriptor{
			{
				Type: "metronome.tick", Description: "One beat.",
				Fields: []*pb.EventField{{Key: "beat", Type: "number", Description: "Beats since this device was created."}},
			},
		},

		StateSchema: []*pb.StateField{
			{Key: "running", Type: "bool"},
			{Key: "bpm", Type: "number"},
			{Key: "beats", Type: "number"},
		},

		Traits: []string{"speaker"},

		// The highest `Protocol` value this driver was built against. Same integer as `abi` in plugin.json.
		ProtocolVersion: currentProtocol(),

		// `min_hub_protocol` is left unset on purpose. Unset means "the floor is protocol_version", which is
		// the safe reading; a floor may only ever move *down* over a plugin's life.

		// **Authoritative, and therefore complete.** A non-empty list turns the three `supports_*` booleans
		// off entirely, so everything this driver does has to be in it — including anything a boolean would
		// have covered. This driver does one optional thing.
		Capabilities: []string{"diagnostics"},

		// Sent anyway. They are not deprecated on the wire and a hub too old to read `capabilities` still
		// reads these.
		SupportsNavigation:    false,
		SupportsEpg:           false,
		SupportsDeviceRemotes: false,
	}, nil
}

// ---- Devices ------------------------------------------------------------------------------------------

func (d *Driver) CreateDevice(ctx context.Context, req *pb.CreateDeviceRequest) (*pb.CreateDeviceResponse, error) {
	bpm := atoiOr(req.GetConfig()["bpm"], 90)
	if bpm < 20 || bpm > 300 {
		// Nothing hub-side validates a config value against the schema this driver declared — not
		// `required`, not a range, not membership in `options`. The form is the only thing that asks and
		// the HTTP API takes an arbitrary dictionary. So every driver checks its own, every time.
		return &pb.CreateDeviceResponse{Ok: false, Error: fmt.Sprintf("bpm %d is outside 20–300", bpm)}, nil
	}

	// The fake wire, so that a captured diagnostic has something in it — and something to redact.
	d.diag.record(req.GetDeviceId(), "tx", "studio.example:9000",
		fmt.Sprintf("HELLO %s %s", req.GetName(), req.GetConfig()["studio_token"]),
		req.GetConfig()["studio_token"])

	d.mu.Lock()
	d.devices[req.GetDeviceId()] = &device{id: req.GetDeviceId(), name: req.GetName(), bpm: bpm}
	d.mu.Unlock()

	return &pb.CreateDeviceResponse{Ok: true}, nil
}

func (d *Driver) DisposeDevice(ctx context.Context, ref *pb.DeviceRef) (*pb.DisposeResponse, error) {
	d.mu.Lock()
	delete(d.devices, ref.GetDeviceId())
	d.mu.Unlock()
	return &pb.DisposeResponse{Ok: true}, nil
}

func (d *Driver) GetState(ctx context.Context, ref *pb.DeviceRef) (*pb.DeviceStateMessage, error) {
	d.mu.Lock()
	defer d.mu.Unlock()
	dev, ok := d.devices[ref.GetDeviceId()]
	if !ok {
		// `online` is a plain proto3 bool, so an unknown device answered with a zero message reads as a
		// device that exists and is offline. That is the true statement here anyway; a driver that wanted
		// to say "I have never heard of this id" has `Availability` on the six rpcs that carry it and
		// nothing at all on this one.
		return &pb.DeviceStateMessage{Online: false}, nil
	}
	return &pb.DeviceStateMessage{
		Online: true,
		State: map[string]string{
			"running": strconv.FormatBool(dev.running),
			"bpm":     strconv.Itoa(dev.bpm),
			"beats":   strconv.FormatInt(dev.beats, 10),
		},
	}, nil
}

func (d *Driver) ExecuteCommand(ctx context.Context, req *pb.ExecuteCommandRequest) (*pb.ExecuteCommandResponse, error) {
	d.mu.Lock()
	dev, ok := d.devices[req.GetDeviceId()]
	d.mu.Unlock()
	if !ok {
		return &pb.ExecuteCommandResponse{Ok: false, Error: "no such device on this driver"}, nil
	}

	switch req.GetCommandId() {
	case "power.on":
		d.mu.Lock()
		dev.running = true
		d.mu.Unlock()
	case "power.off":
		d.mu.Lock()
		dev.running = false
		d.mu.Unlock()
	case "metronome.tempo":
		bpm := atoiOr(req.GetArgs()["bpm"], 0)
		if bpm < 20 || bpm > 300 {
			// A refusal the *device* made, reported as `ok: false` with a sentence. Returning a gRPC error
			// instead would be reported to the person as the driver having failed rather than as the
			// device having declined, and those are different facts.
			return &pb.ExecuteCommandResponse{Ok: false, Error: fmt.Sprintf("bpm must be 20–300, got %q", req.GetArgs()["bpm"])}, nil
		}
		d.mu.Lock()
		dev.bpm = bpm
		d.mu.Unlock()
	case "metronome.calibrate":
		return d.calibrate(ctx, dev)
	default:
		return &pb.ExecuteCommandResponse{Ok: false, Error: "unknown command " + req.GetCommandId()}, nil
	}

	d.diag.record(req.GetDeviceId(), "tx", "studio.example:9000", "CMD "+req.GetCommandId(), "")
	return &pb.ExecuteCommandResponse{Ok: true}, nil
}

// calibrate waits, and says so. Twelve seconds is under every hub deadline and is here to be watched
// rather than to be useful: the hold is the point.
func (d *Driver) calibrate(ctx context.Context, dev *device) (*pb.ExecuteCommandResponse, error) {
	holdID := dev.id + ":calibrate"
	until := time.Now().Add(12 * time.Second)

	d.publish(&pb.DeviceEventMessage{
		Type: "driver.hold", TimestampUnixMs: nowMs(),
		Hold: &pb.DriverHoldMessage{
			Id: holdID, DeviceId: dev.id,
			Reason:      "counting a reference minute against the studio clock",
			UntilUnixMs: until.UnixMilli(),
		},
	})
	// Released on every path out, including the ones that failed. A hold that is never released is
	// indistinguishable from the wedge it exists to rule out.
	defer d.publish(&pb.DeviceEventMessage{
		Type: "driver.hold", TimestampUnixMs: nowMs(),
		Hold: &pb.DriverHoldMessage{Id: holdID, DeviceId: dev.id, Released: true},
	})

	select {
	case <-time.After(12 * time.Second):
		return &pb.ExecuteCommandResponse{Ok: true, Result: map[string]string{"drift_ms": "3"}}, nil
	case <-ctx.Done():
		// The hub puts a deadline on every unary call — 60 s for an ordinary command — and gRPC delivers it
		// as this context's deadline. Nothing in the proto says the number; the context is where a plugin
		// in any language can read it.
		return nil, status.Error(codes.DeadlineExceeded, "the hub stopped waiting before calibration finished")
	}
}

// ---- The event stream ---------------------------------------------------------------------------------

// StreamEvents is opened once, when the hub connects, and must not end. The hub reads it until *it*
// cancels; a driver that returns from here — cleanly, at the end of a loop, on a timer — takes its own
// events off the bus for the life of the process, and nothing reconnects and nothing is logged.
func (d *Driver) StreamEvents(_ *pb.StreamEventsRequest, out pb.Driver_StreamEventsServer) error {
	ch := make(chan *pb.DeviceEventMessage, 64)
	d.subsMu.Lock()
	d.subs[ch] = struct{}{}
	d.subsMu.Unlock()
	defer func() {
		d.subsMu.Lock()
		delete(d.subs, ch)
		d.subsMu.Unlock()
	}()

	// The first beat goes out immediately, before any wait. That is what lets the hub tell a driver too old
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
		default: // a subscriber that cannot keep up loses frames rather than blocking the device loop
		}
	}
}

func (d *Driver) heartbeat() *pb.DeviceEventMessage {
	interval := uint32(beatEvery / time.Millisecond)
	independent := true
	return &pb.DeviceEventMessage{
		// `device_id` is empty: the frame is about the process, not about any device it hosts.
		Type: "driver.heartbeat",
		// The hub takes the *age* of a beat from this field and not from when the frame arrived. Left at
		// proto3's zero it reads as 1970, and the plugin is reported as stopped for ever while beating.
		TimestampUnixMs: nowMs(),
		// The submessage is what routes the frame. A frame carrying the type string and no `runtime` is not
		// a heartbeat that got lost — it is an ordinary device event with an empty device id.
		Runtime: &pb.DriverRuntimeMessage{
			HeartbeatIntervalMs: &interval,
			// True, and measured rather than claimed: the beat is its own goroutine writing into a channel
			// the stream drains, so `metronome.calibrate` blocking for twelve seconds does not stop it.
			// The lamp in samples/python declares `false` and demonstrates the other half of this rule.
			HeartbeatIndependent: &independent,
			// Every numeric field on this message has explicit presence and unset means "not taken". A Go
			// plugin has no equivalent of the .NET GC counters, so it sends none of them rather than
			// sending zeroes that would be drawn as measurements.
		},
	}
}

func (d *Driver) beat() {
	for range time.Tick(beatEvery) {
		d.publish(d.heartbeat())
	}
}

func (d *Driver) tick() {
	for range time.Tick(100 * time.Millisecond) {
		now := time.Now()
		d.mu.Lock()
		var due []*pb.DeviceEventMessage
		for _, dev := range d.devices {
			if !dev.running {
				continue
			}
			period := time.Duration(float64(time.Minute) / float64(dev.bpm))
			if now.UnixNano()%int64(period) < int64(100*time.Millisecond) {
				dev.beats++
				due = append(due, &pb.DeviceEventMessage{
					DeviceId: dev.id, Type: "metronome.tick", TimestampUnixMs: nowMs(),
					Data: map[string]string{"beat": strconv.FormatInt(dev.beats, 10)},
				})
			}
		}
		d.mu.Unlock()
		for _, m := range due {
			d.publish(m)
		}
	}
}

// ---- helpers ------------------------------------------------------------------------------------------

func nowMs() int64 { return time.Now().UnixMilli() }

func atoiOr(s string, fallback int) int {
	n, err := strconv.Atoi(strings.TrimSpace(s))
	if err != nil {
		return fallback
	}
	return n
}
