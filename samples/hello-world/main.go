// Hello World — a reMaestro plugin that installs a rubber duck.
//
// The duck is not there. It says hello, it counts how many times it has said hello, and that is the
// whole of it. It exists so that anybody bringing up a hub, writing their first plugin, or standing up a
// registry has **one moving part they can point at and be certain about** — and so that the answer to
// "did that work?" is a number rather than an impression.
//
// Nothing here imports anything of reMaestro's. The only reMaestro artefact in this module is
// `gen/maestro/`, which `package.sh` generates from the published `driver.proto` with stock `protoc` and
// which is not committed.
//
// Run it on your laptop, without a hub anywhere:
//
//	REMAESTRO_DRIVER_URL=http://127.0.0.1:5199 go run .
package main

import (
	"errors"
	"log"
	"net"
	"net/url"
	"os"
	"strings"

	"google.golang.org/grpc"

	"example.com/helloworld/gen/maestro"
	"example.com/helloworld/hello"
)

// typeID is stamped at build time by package.sh, and the reason it is a variable rather than a constant
// is the sharpest trap in the whole packaging story.
//
// A plugin `id` that collides with one already published is refused, loudly, by the registry. **A
// `type_id` that collides with another driver's is not checked anywhere at all** — not by the registry,
// not by the hub, not at install. Two plugins claiming `hello-duck` produce one device type and the
// person who installed the second one is never told which of them they got.
//
// So a `type_id` has to be obviously, unmistakably its publisher's, and this module is published under
// two publisher identities, so it cannot hard-code one. See README §"A plugin id and a type id are
// different names, and only one of them is protected".
var typeID = "hello-duck-dev"

func main() {
	// The hub hands the address on two variables holding one value. `REMAESTRO_DRIVER_URL` is the one a
	// plugin author can guess; `ASPNETCORE_URLS` is the one every driver in the field already reads and
	// is still set. Read the neutral one first and fall back, so this works on an older hub too.
	raw := firstSet("REMAESTRO_DRIVER_URL", "ASPNETCORE_URLS")
	if raw == "" {
		log.Fatal("neither REMAESTRO_DRIVER_URL nor ASPNETCORE_URLS is set — a hub sets both; " +
			"pass one yourself to run this by hand")
	}

	addr, err := listenAddress(raw)
	if err != nil {
		log.Fatalf("could not read %q as an address: %v", raw, err)
	}

	lis, err := net.Listen("tcp", addr)
	if err != nil {
		// The hub picked this port by binding it, closing it and handing us the number. Losing that race
		// is possible, and dying loudly is the honest response: the hub's own guard notices we exited
		// while something else answered on the address, and refuses to take that descriptor as ours.
		log.Fatalf("could not listen on %s: %v", addr, err)
	}

	// Cleartext h2c. `grpc.NewServer` with no credentials serves HTTP/2 with prior knowledge, which is
	// what the hub's `GrpcChannel.ForAddress("http://…")` speaks. Adding TLS here would break it, and the
	// only place that is stated is the `http://` scheme on the variable above.
	srv := grpc.NewServer()
	maestro.RegisterDriverServer(srv, hello.NewDriver(typeID))

	log.Printf("hello-world listening on %s (from %s), serving device type %q", addr, raw, typeID)

	// There is no graceful-shutdown path below, and that is not an oversight. The hub ends a driver with
	// `Process.Kill(entireProcessTree: true)` — SIGKILL — with no rpc, no signal and no grace period, so
	// a handler here would be dead code that implies a contract nobody offers. Measured in `#427`: a
	// build carrying SIGTERM, SIGINT, SIGHUP and SIGQUIT handlers, through a full install → introspect →
	// launch → drive → stop cycle, fired **not one of them**. Anything a plugin wants to survive has to
	// be durable at the moment it is true, not at the moment the process ends.
	if err := srv.Serve(lis); err != nil {
		log.Fatalf("serve: %v", err)
	}
}

func firstSet(names ...string) string {
	for _, n := range names {
		if v := strings.TrimSpace(os.Getenv(n)); v != "" {
			return v
		}
	}
	return ""
}

// The variables carry a **URL** — `http://127.0.0.1:53412` — and not a `host:port`, because the name they
// were modelled on is ASP.NET Core's. `net.Listen` wants the second. Semicolon-separated lists are legal
// in `ASPNETCORE_URLS` and a hub never writes one; the first entry is taken if a person does.
func listenAddress(raw string) (string, error) {
	first := strings.TrimSpace(strings.Split(raw, ";")[0])
	if !strings.Contains(first, "//") {
		return first, nil // already a host:port
	}
	u, err := url.Parse(first)
	if err != nil {
		return "", err
	}
	host := u.Hostname()
	if host == "" || host == "*" || host == "+" {
		host = "127.0.0.1"
	}
	port := u.Port()
	if port == "" {
		return "", errNoPort
	}
	return net.JoinHostPort(host, port), nil
}

var errNoPort = errors.New("no port in the address the hub gave us")
