#!/usr/bin/env bash
# Run a packaged build on the architecture it claims, and drive it.
#
#   ./verify.sh linux-arm64      # the appliance's shape
#   ./verify.sh linux-x64        # the cloud's shape
#
# **Why this exists.** A plugin that has only ever been run on the machine that compiled it has not been
# shown to run on a hub. The hub is arm64 Linux on an appliance whose root filesystem is read-only, and
# amd64 Linux in the cloud. This unpacks the archive `package.sh` produced, runs it inside a Linux container
# of that architecture with **no interpreter, no shell and a read-only root**, and points `cmd/verify` at it
# from outside.
#
# It needs Docker. On an arm64 host `linux-x64` runs under emulation and is slower; on an amd64 host it is
# the other way round.
set -euo pipefail

RID="${1:?usage: verify.sh <linux-arm64|linux-x64>}"
case "$RID" in
    linux-arm64) PLATFORM=linux/arm64 ;;
    linux-x64)   PLATFORM=linux/amd64 ;;
    *) echo "verify.sh runs Linux builds; '$RID' is not one" >&2; exit 2 ;;
esac

HERE="$(cd "$(dirname "$0")" && pwd)"
STAGE="$HERE/dist/$RID"
PORT="${PORT:-19999}"
NAME="metronome-verify-$$"

[ -x "$STAGE/metronome" ] || { echo "build it first: ./package.sh $RID" >&2; exit 2; }

cleanup() { docker rm -f "$NAME" >/dev/null 2>&1 || true; }
trap cleanup EXIT

# **`FROM scratch` is the whole point of the check.** The image below has no libc, no shell, no CA bundle
# and no /etc — nothing but this one file. A binary that starts in it cannot be depending on anything the
# appliance might not have, which is the claim `CGO_ENABLED=0` makes and which nothing else here proves.
# `--read-only` is the appliance's root filesystem.
IMAGE="metronome-verify:$RID"
printf 'FROM scratch\nCOPY metronome /metronome\nENTRYPOINT ["/metronome"]\n' > "$STAGE/Dockerfile.verify"
docker build --platform "$PLATFORM" -q -f "$STAGE/Dockerfile.verify" -t "$IMAGE" "$STAGE" >/dev/null

docker run -d --name "$NAME" --platform "$PLATFORM" \
    --read-only \
    -p "127.0.0.1:$PORT:$PORT" \
    -e "REMAESTRO_DRIVER_URL=http://0.0.0.0:$PORT" \
    "$IMAGE" >/dev/null

echo "== container: FROM scratch, $PLATFORM, read-only root, one file in it =="

go run "$HERE/cmd/verify" "http://127.0.0.1:$PORT"

echo "== the plugin's own stdout, which on a hub goes to the hub's =="
docker logs "$NAME" 2>&1 | sed 's/^/   /'
