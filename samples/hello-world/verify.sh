#!/usr/bin/env bash
# Run a **packaged archive** on the architecture it claims, and drive it.
#
#   ./verify.sh linux-arm64      # the appliance's shape
#   ./verify.sh linux-x64        # the cloud's shape
#
# **Why this exists.** A plugin that has only ever been run on the machine that compiled it has not been
# shown to run on a hub. A hub is arm64 Linux on an appliance whose root filesystem is read-only, and
# amd64 Linux in the cloud. This unpacks the `.tar.gz` `package.sh` produced, runs the binary out of it
# inside a Linux container of that architecture with **no interpreter, no shell and a read-only root**,
# and points `cmd/verify` at it from outside.
#
# It reads the **archive** rather than the staging directory on purpose: the archive is what gets
# published, and the execute bit surviving `tar` is one of the two things that has actually gone wrong
# here. The other is below.
#
# It needs Docker. On an arm64 host `linux-x64` runs under emulation and is slower; on an amd64 host it is
# the other way round. Emulation proves the binary is correct and says nothing about its speed.
set -euo pipefail

RID="${1:?usage: verify.sh <linux-arm64|linux-x64>}"
case "$RID" in
    linux-arm64) PLATFORM=linux/arm64 ;;
    linux-x64)   PLATFORM=linux/amd64 ;;
    *) echo "verify.sh runs Linux builds; '$RID' is not one" >&2; exit 2 ;;
esac

ID="${PLUGIN_ID:-app.remaestro.helloworld}"
TYPE_ID="${TYPE_ID:-remaestro-hello-duck}"
VERSION="${VERSION:-1.0.0}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ARCHIVE="$HERE/dist/$ID-$VERSION-$RID.tar.gz"
PORT="${PORT:-19998}"
NAME="hello-verify-$$"

[ -f "$ARCHIVE" ] || { echo "no archive at $ARCHIVE — build it first: PLUGIN_ID=$ID ./package.sh $RID" >&2; exit 2; }

# **A fresh context directory per run, with fresh timestamps, and this is not tidiness.**
#
# `package.sh` pins every staged file's mtime so the archive's bytes are reproducible. That is right, and
# it has a consequence nobody would guess: **BuildKit decides whether it can reuse a cached snapshot of a
# build context from the files' *metadata* — name, size, mode, mtime — and not from their contents.** Two
# builds of this plugin under two publisher identities differ only in a linker-stamped string, so they are
# the same size, the same mode, and (because of the pinning) the same mtime. BuildKit read the second one
# as the first, and the container served the *other* identity's binary.
#
# Measured, because it is worth knowing exactly how far this goes: **`--no-cache` does not fix it.** That
# flag invalidates the instruction cache and not the context snapshot, so a `--no-cache` build still
# served the wrong binary. Moving the mtime by one second fixed it immediately.
#
# It fails **green**, which is the only reason it matters: `verify.sh` passed, printed VERIFIED, and had
# verified somebody else's artefact. Hence both halves of the fix — a unique context here, and the
# `--expect-type` assertion below that would have caught it whatever the cause.
CONTEXT="$(mktemp -d)"
cleanup() {
    docker rm -f "$NAME" >/dev/null 2>&1 || true
    rm -rf "$CONTEXT"
}
trap cleanup EXIT

tar -xzf "$ARCHIVE" -C "$CONTEXT"
[ -x "$CONTEXT/hello-world" ] || {
    echo "the binary came out of the archive without its execute bit. A hub installs this successfully" >&2
    echo "and then fails at Process.Start with a message about permissions and nothing about a tar." >&2
    exit 1
}
touch "$CONTEXT/hello-world"   # a mtime of *now*, so the context digest is this run's

# **`FROM scratch` is the whole point of the check.** The image below has no libc, no shell, no CA bundle
# and no /etc — nothing but this one file. A binary that starts in it cannot be depending on anything the
# appliance might not have, which is the claim `CGO_ENABLED=0` makes and which nothing else here proves.
# `--read-only` is the appliance's root filesystem.
IMAGE="hello-verify:$ID-$RID"
printf 'FROM scratch\nCOPY hello-world /hello-world\nENTRYPOINT ["/hello-world"]\n' > "$CONTEXT/Dockerfile"
docker build --platform "$PLATFORM" -q -t "$IMAGE" "$CONTEXT" >/dev/null

docker run -d --name "$NAME" --platform "$PLATFORM" \
    --read-only \
    -p "127.0.0.1:$PORT:$PORT" \
    -e "REMAESTRO_DRIVER_URL=http://0.0.0.0:$PORT" \
    "$IMAGE" >/dev/null

echo "== $ID $VERSION $RID: FROM scratch, $PLATFORM, read-only root, one file in it =="
echo "== archive $(shasum -a 256 "$ARCHIVE" | cut -d' ' -f1) =="

go run "$HERE/cmd/verify" --expect-type "$TYPE_ID" "http://127.0.0.1:$PORT"

echo "== the plugin's own stdout, which on a hub goes to the hub's =="
docker logs "$NAME" 2>&1 | sed 's/^/   /'
