#!/usr/bin/env bash
# Build, package and sign the Example Metronome plugin.
#
#   ./package.sh linux-arm64            # build for a hub
#   ./package.sh linux-arm64 key.pem    # …and sign it with your publisher key
#
# What comes out is one `.tar.gz` per architecture and the three values a hub needs to install it by URL:
# the SHA-256, the signature, and your public key. There is no step that talks to a registry, because
# install-by-URL has to work without one.
set -euo pipefail

RID="${1:?usage: package.sh <rid> [publisher-key.pem]   (rid: linux-arm64 | linux-x64 | osx-arm64 | osx-x64)}"
KEY="${2:-}"
ID="com.example.metronome"
VERSION="1.0.0"
ABI=1

HERE="$(cd "$(dirname "$0")" && pwd)"
PROTO_DIR="$HERE/../../proto"
OUT="$HERE/dist"
STAGE="$OUT/$RID"

# `rid` is a **.NET runtime identifier**, and Go has never heard of one. The hub compares it against
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

rm -rf "$STAGE" && mkdir -p "$STAGE"

# 1. Stock codegen. No plugins, no options, nothing from this repository.
#
#    **`protoc -I ../../proto --go_out=. --go-grpc_out=. driver.proto` now works on its own**, and until
#    `#427` it did not: `protoc-gen-go` treats a missing Go import path as *fatal* where every other
#    generator defaults something, so a file carrying `option csharp_namespace` and nothing else stopped a
#    Go author at step one with
#
#        protoc-gen-go: unable to determine Go import path for "driver.proto"
#
#    `driver.proto` now declares `option go_package`, so the stock command generates. The `M…` flags below
#    are **layout**, not a workaround: they put the stubs inside *this* module at
#    `example.com/metronome/gen/maestro` rather than under the SDK's own import path. Any plugin with a
#    module path of its own needs the same two flags, whatever the proto says.
rm -rf "$HERE/gen" && mkdir -p "$HERE/gen"
protoc -I "$PROTO_DIR" \
    --go_out="$HERE/gen"      --go_opt=module=example.com/metronome/gen \
    --go_opt=Mdriver.proto=example.com/metronome/gen/maestro \
    --go-grpc_out="$HERE/gen" --go-grpc_opt=module=example.com/metronome/gen \
    --go-grpc_opt=Mdriver.proto=example.com/metronome/gen/maestro \
    driver.proto

# 2. One static binary. `CGO_ENABLED=0` is what makes it static: a Go binary that links libc will not start
#    on the appliance, whose root filesystem is read-only and whose libc is not the one on your laptop.
#    Nothing vendors, nothing is downloaded at install time, and the result runs on a box with no
#    interpreter, no runtime and no shared library of ours.
CGO_ENABLED=0 GOOS="$GOOS" GOARCH="$GOARCH" \
    go build -trimpath -ldflags "-s -w" -o "$STAGE/metronome" "$HERE"

# 3. The manifest the hub reads. Not the one the registry reads — that one lists every version and every
#    architecture, lives in the registry repo, and never comes near a box.
#
#    `../../docs/plugin-manifest.schema.json` is what this has to satisfy, and `docs/driver-protocol.md` §6
#    is the prose. `exec[0]` is `./metronome`: a path that exists inside the package, which is how the hub
#    tells "a file I shipped" from "something the box is expected to have on PATH".
cat > "$STAGE/plugin.json" <<JSON
{
  "id": "$ID",
  "version": "$VERSION",
  "abi": $ABI,
  "kind": "driver",
  "runtime": "native",
  "rid": "$RID",
  "exec": ["./metronome"]
}
JSON

# 4. One gzipped tar, with plugin.json at its root.
#
#    `--format=ustar` and a fixed owner because a tar carrying your username is a tar whose bytes differ
#    from the one your colleague built from the same source, and the SHA-256 is what the hub checks.
#
#    **The execute bit has to survive.** The hub unpacks with `TarReader` and applies the Unix mode from the
#    entry; a tar built without it produces a plugin that installs, is found, is launched, and fails at
#    `Process.Start` with a message about permissions and nothing at all about a tar.
chmod +x "$STAGE/metronome"
ARCHIVE="$OUT/$ID-$VERSION-$RID.tar.gz"
tar --format=ustar -czf "$ARCHIVE" -C "$STAGE" .

echo "archive: $ARCHIVE"
echo "sha256:  $(shasum -a 256 "$ARCHIVE" | cut -d' ' -f1)"
echo "size:    $(wc -c < "$ARCHIVE" | tr -d ' ')"

# 5. Sign the archive bytes — not the digest, not the manifest.
if [ -n "$KEY" ]; then
    openssl dgst -sha256 -sign "$KEY" -out "$ARCHIVE.sig.der" "$ARCHIVE"
    echo "signature: $(base64 < "$ARCHIVE.sig.der" | tr -d '\n')"
    echo "publisher: $(openssl ec -in "$KEY" -pubout -outform DER 2>/dev/null | base64 | tr -d '\n')"
fi
