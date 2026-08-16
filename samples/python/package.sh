#!/usr/bin/env bash
# Build, package and sign the Example Lamp plugin.
#
#   ./package.sh linux-arm64            # build for a hub
#   ./package.sh linux-arm64 key.pem    # …and sign it with your publisher key
#
# What comes out is one `.tar.gz` per architecture and the three values a hub needs to install it by URL:
# the SHA-256, the signature, and your public key. There is no step that talks to a registry, because
# install-by-URL has to work without one.
set -euo pipefail

RID="${1:?usage: package.sh <rid> [publisher-key.pem]}"
KEY="${2:-}"
ID="com.example.lamp"
VERSION="1.0.0"
ABI=1

HERE="$(cd "$(dirname "$0")" && pwd)"
PROTO="$HERE/../../proto/driver.proto"
OUT="$HERE/dist"
STAGE="$OUT/$RID"

rm -rf "$STAGE" && mkdir -p "$STAGE"

# 1. Stock codegen. No plugins, no options, nothing from this repository.
#
#    Note where the stubs land: **the package root, beside main.py**. `grpc_tools.protoc` emits
#    `import driver_pb2` — a top-level import — into `driver_pb2_grpc.py`, so putting the two files inside
#    a package directory produces `ModuleNotFoundError: No module named 'driver_pb2'` on the first import.
#    Flat is the layout that works with the generator's own output.
python3 -m grpc_tools.protoc -I "$(dirname "$PROTO")" \
    --python_out="$STAGE" --grpc_python_out="$STAGE" "$PROTO"

cp "$HERE/main.py" "$STAGE/"
cp -R "$HERE/lamp" "$STAGE/lamp"
find "$STAGE" -name '__pycache__' -type d -prune -exec rm -rf {} +

# 2. Vendor the dependencies.
#
#    `"runtime": "python3"` promises an interpreter and **nothing else**. `grpcio` is on neither the
#    appliance nor in the hub container, and it is a compiled wheel — so every Python plugin ships a
#    platform-specific binary in `_vendor/`, which makes a `python3` plugin exactly as architecture-bound
#    as a `native` one, and about three times the size of an equivalent trimmed .NET build.
python3 -m pip install --quiet --target "$STAGE/_vendor" grpcio protobuf
find "$STAGE/_vendor" -name '__pycache__' -type d -prune -exec rm -rf {} +

# 3. The manifest the hub reads. Not the one the registry reads — that one lists every version and every
#    architecture, lives in the registry repo, and never comes near a box.
cat > "$STAGE/plugin.json" <<JSON
{
  "id": "$ID",
  "version": "$VERSION",
  "abi": $ABI,
  "kind": "driver",
  "runtime": "python3",
  "rid": "$RID",
  "exec": ["python3", "main.py"]
}
JSON

# 4. One gzipped tar, with plugin.json at its root.
#
#    `--format=ustar` and a fixed owner because a tar carrying your username is a tar whose bytes differ
#    from the one your colleague built from the same source, and the SHA-256 is what the hub checks.
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
