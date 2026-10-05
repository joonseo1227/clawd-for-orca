#!/bin/sh
# Runs Clawd on demo data: made-up Orca agents and a Claude Code transcript, nothing real.
# Useful for screenshots and for trying the UI without Orca.
#
#   Scripts/demo.sh [extra environment, e.g. CLAWD_OPEN_CHAT=1]
set -eu
cd "$(dirname "$0")/.."
DIR="${TMPDIR:-/tmp}/clawd-demo"
python3 Scripts/demo/make_demo.py "$DIR" "${CLAWD_DEMO_LANG:-ko}" >/dev/null
[ -x Clawd.app/Contents/MacOS/Clawd ] || ./build.sh
pkill -x Clawd 2>/dev/null || true
sleep 0.5
env CLAWD_FAKE_ORCA="$DIR/agents.json" CLAWD_FAKE_TRANSCRIPTS="$DIR/transcripts" CLAWD_NO_LIVE=1 "$@" \
  Clawd.app/Contents/MacOS/Clawd >/dev/null 2>&1 &
echo "Clawd is running on demo data from $DIR"
