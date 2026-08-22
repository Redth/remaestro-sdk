#!/usr/bin/env bash
# Build, package and sign the Hello World plugin.
#
#   ./package.sh linux-arm64                 # build for a hub
#   ./package.sh linux-arm64 publisher.pem   # …and sign it with your publisher key
#
# Out comes one `.tar.gz` per architecture and the three values a hub needs to install it by URL — the
# SHA-256, the signature and the public key — plus the two values a registry submission needs. Nothing
# here talks to a registry, because install-by-URL has to work without one.
#
# This is a near-copy of `package.sh` in the SDK's own Go sample, on purpose: those two scripts are the
# mould, and a plugin that packages itself some other way is a plugin nobody can check.
set -euo pipefail

RID="${1:?usage: package.sh <rid> [publisher-key.pem]   (rid: linux-arm64 | linux-x64 | osx-arm64 | osx-x64)}"
KEY="${2:-}"

# The identity. Overridable because **this one plugin is published under two publisher identities** — the
# project's own and an outside one — from a single source tree, and neither of them is more real than the
# other. A stranger cloning this sets these once and forgets about them.
#
# `PLUGIN_ID` is the registry's name for the package. `TYPE_ID` is the hub's name for the *device type*
# the package installs, and they are different names with different protection: see README
# §"A plugin id and a type id are different names, and only one of them is protected".
ID="${PLUGIN_ID:-app.remaestro.helloworld}"
TYPE_ID="${TYPE_ID:-remaestro-hello-duck}"
VERSION="${VERSION:-1.0.0}"

# `abi` is the `driver.proto` protocol version this build was made against. **An integer.** Quoted —
# `"abi": "1"` — it reads as 0 on a hub, silently, with the install-time compatibility check switched
# off; a registry catches that and install-by-URL does not.
ABI="${ABI:-1}"

HERE="$(cd "$(dirname "$0")" && pwd)"
# The contract, two directories up. **This is the one real gain of living inside the SDK**: the sample
# builds against the same file the hub compiles against, so there is no vendored copy to drift and no
# provenance note to keep true. A stranger who clones only this directory sets `DRIVER_PROTO` instead.
PROTO="${DRIVER_PROTO:-$HERE/../../proto/driver.proto}"
OUT="$HERE/dist"
STAGE="$OUT/$ID/$RID"

# `rid` is a **.NET runtime identifier**, and Go has never heard of one. A hub compares it against
# `RuntimeInformation.RuntimeIdentifier` on the box, so the manifest has to speak .NET's vocabulary
# whatever the compiler's is — `linux-x64`, not `linux/amd64`, not `x86_64-unknown-linux-gnu`.
case "$RID" in
    linux-arm64) GOOS=linux;   GOARCH=arm64 ;;
    linux-x64)   GOOS=linux;   GOARCH=amd64 ;;
    osx-arm64)   GOOS=darwin;  GOARCH=arm64 ;;
    osx-x64)     GOOS=darwin;  GOARCH=amd64 ;;
    win-x64)     GOOS=windows; GOARCH=amd64 ;;
    *) echo "unknown rid '$RID' — see the schema's \`rid\` description" >&2; exit 2 ;;
esac

[ -f "$PROTO" ] || { echo "no driver.proto at $PROTO — set DRIVER_PROTO to a copy of the contract" >&2; exit 2; }

rm -rf "$STAGE" && mkdir -p "$STAGE"

# 1. Stock codegen. No plugins of ours, no options of ours, nothing from any private repository —
#    `driver.proto` is published and this is the whole of what it takes to read it.
#
#    The `M…` flags are **layout**, not a workaround. `driver.proto` declares
#    `option go_package = ".../proto;maestro"`, so plain `--go_out` generates under the SDK's own import
#    path; these put the stubs inside *this* module instead. Any plugin with a module path of its own
#    needs the same two flags whatever the proto says. Without the `go_package` option — which the proto
#    did not carry until `#427` — `protoc-gen-go` fails outright rather than defaulting, which is the one
#    generator that does.
rm -rf "$HERE/gen" && mkdir -p "$HERE/gen"
MODULE="$(awk '/^module /{print $2; exit}' "$HERE/go.mod")"
protoc -I "$(dirname "$PROTO")" \
    --go_out="$HERE/gen"      --go_opt=module="$MODULE/gen" \
    --go_opt=Mdriver.proto="$MODULE/gen/maestro" \
    --go-grpc_out="$HERE/gen" --go-grpc_opt=module="$MODULE/gen" \
    --go-grpc_opt=Mdriver.proto="$MODULE/gen/maestro" \
    "$(basename "$PROTO")"

# 2. One static binary. `CGO_ENABLED=0` is what makes it static: a Go binary that links libc will not
#    start on the appliance, whose root filesystem is read-only and whose libc is not the one on your
#    laptop. Nothing vendors, nothing is downloaded at install time, and the result runs on a box with no
#    interpreter, no runtime and no shared library of anybody's.
#
#    `-X main.typeID` stamps the device type the build claims. It is a build-time value here only because
#    this source is published twice under two identities; hard-coding yours is the ordinary thing to do.
CGO_ENABLED=0 GOOS="$GOOS" GOARCH="$GOARCH" \
    go build -trimpath -ldflags "-s -w -X main.typeID=$TYPE_ID" -o "$STAGE/hello-world" "$HERE"

# 3. The manifest a **hub** reads. Not the one a registry reads — that one lists every version and every
#    architecture, lives in the registry repository, has its own schema, and **refuses unknown fields
#    where this one ignores them.** Two files, one name, opposite rules.
#
#    The normative contract for this one is the SDK's `docs/plugin-manifest.schema.json`, with
#    `docs/driver-protocol.md` §6 as the prose beside it.
#
#    `exec[0]` is `./hello-world`: a path that exists inside the package, which is how a hub tells "a file
#    I shipped" from "something the box is expected to have on PATH".
cat > "$STAGE/plugin.json" <<JSON
{
  "id": "$ID",
  "version": "$VERSION",
  "abi": $ABI,
  "kind": "driver",
  "runtime": "native",
  "rid": "$RID",
  "exec": ["./hello-world"]
}
JSON

# 4. One gzipped tar, with plugin.json at its root.
#
#    **The execute bit has to survive.** A hub unpacks with `TarReader` and applies the Unix mode from the
#    entry; a tar built without it produces a plugin that installs, is found, is launched, and fails at
#    `Process.Start` with a message about permissions and nothing at all about a tar. Measured.
chmod +x "$STAGE/hello-world"

#    **And the bytes are made reproducible on purpose**, which the SDK sample's script describes and does
#    not quite do — it says "a fixed owner" and passes no ownership flags, so an archive carries whoever
#    built it. Once a version is published the bytes at its URL may never change, so being able to rebuild
#    them and get the same SHA-256 is the difference between "here is the source" and "here is the source,
#    check me". Four things vary and all four are pinned: owner, group, timestamps, and the name and mtime
#    gzip writes into its own header (`-n`).
find "$STAGE" -exec touch -h -t 202601010000.00 {} +
ARCHIVE="$OUT/$ID-$VERSION-$RID.tar.gz"
tar --format=ustar --uid 0 --gid 0 --uname '' --gname '' \
    -cf - -C "$STAGE" ./plugin.json ./hello-world | gzip -9 -n > "$ARCHIVE"

echo "archive:   $ARCHIVE"
echo "id:        $ID"
echo "typeId:    $TYPE_ID"
echo "version:   $VERSION"
echo "rid:       $RID"
echo "sha256:    $(shasum -a 256 "$ARCHIVE" | cut -d' ' -f1)"
echo "size:      $(wc -c < "$ARCHIVE" | tr -d ' ')"

# 5. Sign the **archive bytes** — not the digest, not the manifest, not a file list.
if [ -n "$KEY" ]; then
    openssl dgst -sha256 -sign "$KEY" -out "$ARCHIVE.sig.der" "$ARCHIVE"
    echo "signature: $(base64 < "$ARCHIVE.sig.der" | tr -d '\n')"
    echo "publicKey: $(openssl ec -in "$KEY" -pubout -outform DER 2>/dev/null | base64 | tr -d '\n')"
fi
