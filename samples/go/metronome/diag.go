package metronome

import (
	"encoding/hex"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"context"

	pb "example.com/metronome/gen/maestro"
)

// capture is this driver's diagnostic buffer: the conversation with the device, as it went past, which the
// hub otherwise never sees.
//
// **Redaction happens here and nowhere else.** There is no wire-level equivalent — the hub cannot know
// which of your bytes are a password — so a plugin that skips this ships the device's credential inside
// every support bundle. This is the single obligation a C# author gets invisibly (DriverHost registers
// declared secrets for redaction on its own) and every other author gets nothing at all for.
type capture struct {
	mu      sync.Mutex
	on      bool
	records []*pb.DiagnosticRecord
	seq     atomic.Int64

	// Every value this driver has been handed that it must never print. Registered at the moment a config
	// arrives rather than looked up at the moment of printing, because the printing site is the one that
	// does not know.
	secrets []string
}

func newCapture() *capture { return &capture{} }

const maxRecords = 500

func (c *capture) record(deviceID, direction, endpoint, text, secret string) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if secret != "" {
		c.secrets = append(c.secrets, secret)
	}
	if !c.on {
		return
	}

	// **Blot the bytes, then render.** A DiagnosticRecord carries the same moment twice — `text` and `hex`
	// — and `endpoint` is a third place a credential can sit. Masking the readable column and passing the
	// payload through leaves the password in full, one column to the right, in a `trace.json` the hub
	// writes into a support bundle somebody then emails. The .NET SDK shipped exactly that bug and
	// samples/python/lamp/diag.py was ported from it faithfully enough to inherit it.
	//
	// And blot *before* truncating for rendering: half a password is a shorter password, not a redacted
	// one.
	clean := c.blot(text)

	c.records = append(c.records, &pb.DiagnosticRecord{
		Seq:             c.seq.Add(1),
		TimestampUnixMs: time.Now().UnixMilli(),
		DeviceId:        deviceID,
		Transport:       "tcp",
		Direction:       direction,
		Endpoint:        c.blot(endpoint),
		Text:            clean,
		Detail:          "",
		// The hex is of the blotted bytes, not of the original. It is the same fact twice and both copies
		// have to be redacted or neither is.
		Hex: strings.ToUpper(hex.EncodeToString([]byte(clean))),
	})
	if len(c.records) > maxRecords {
		c.records = c.records[len(c.records)-maxRecords:]
	}
}

// blot replaces every registered secret wherever it appears. Whole-value replacement rather than a
// heuristic: this driver knows exactly which strings it was given, which is the one thing the hub cannot.
func (c *capture) blot(s string) string {
	for _, secret := range c.secrets {
		if secret == "" {
			continue
		}
		s = strings.ReplaceAll(s, secret, "***")
	}
	return s
}

func (d *Driver) SetDiagnostics(_ context.Context, req *pb.SetDiagnosticsRequest) (*pb.SetDiagnosticsResponse, error) {
	d.diag.mu.Lock()
	d.diag.on = req.GetEnabled()
	if !req.GetEnabled() {
		d.diag.records = nil
	}
	d.diag.mu.Unlock()
	return &pb.SetDiagnosticsResponse{Ok: true}, nil
}

func (d *Driver) GetDiagnostics(_ context.Context, req *pb.GetDiagnosticsRequest) (*pb.DiagnosticsMessage, error) {
	d.diag.mu.Lock()
	defer d.diag.mu.Unlock()
	out := &pb.DiagnosticsMessage{Enabled: d.diag.on}
	for _, r := range d.diag.records {
		if r.GetSeq() <= req.GetAfterSeq() {
			continue
		}
		if id := req.GetDeviceId(); id != "" && r.GetDeviceId() != id {
			continue
		}
		out.Records = append(out.Records, r)
	}
	return out, nil
}
