#!/usr/bin/env bash
# Smoke-test the TomYum pp sidecar end-to-end: build, boot, /health, a vanilla + a relax pp calc,
# and confirm the L2 on-disk .osu cache actually persists a file. Exits non-zero on any failure.
#
#   Usage: ./smoke-test.sh [PORT]   (default port 9101, kept off the operator's live 9000)
set -euo pipefail

PORT="${1:-9101}"
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CACHE_DIR="$(mktemp -d)"
BIN="$DIR/target/release/tomyum-pp-sidecar"
BEATMAP=1801936   # "Yuri's Normal", 1★ — a known-good, low-pp catchable map

cleanup() { [[ -n "${PID:-}" ]] && kill "$PID" 2>/dev/null || true; rm -rf "$CACHE_DIR"; }
trap cleanup EXIT

echo "==> building (release)"
( cd "$DIR" && cargo build --release --quiet )

echo "==> starting sidecar on :$PORT  (OSU_CACHE_DIR=$CACHE_DIR)"
PORT="$PORT" OSU_CACHE_DIR="$CACHE_DIR" "$BIN" &
PID=$!

echo "==> waiting for /health"
for _ in $(seq 1 50); do
  if [[ "$(curl -fsS "http://localhost:$PORT/health" 2>/dev/null || true)" == "ok" ]]; then break; fi
  sleep 0.2
done
[[ "$(curl -fsS "http://localhost:$PORT/health")" == "ok" ]] || { echo "FAIL: /health never returned ok"; exit 1; }

req() { # mods -> pp calc JSON for $BEATMAP
  curl -fsS -X POST "http://localhost:$PORT/pp" -H 'Content-Type: application/json' \
    -d "{\"beatmapId\":$BEATMAP,\"mode\":0,\"mods\":$1,\"n300\":100,\"n100\":0,\"n50\":0,\"nGeki\":0,\"nKatu\":0,\"nMiss\":0,\"maxCombo\":100,\"passed\":true}"
}

echo "==> vanilla pp"
VN="$(req 0)"; echo "    $VN"
echo "$VN" | grep -q '"pp"' || { echo "FAIL: no pp in vanilla response"; exit 1; }

echo "==> relax pp (mods=128)"
RX="$(req 128)"; echo "    $RX"
echo "$RX" | grep -q '"pp"' || { echo "FAIL: no pp in relax response"; exit 1; }

echo "==> L2 disk cache populated?"
[[ -s "$CACHE_DIR/$BEATMAP.osu" ]] || { echo "FAIL: $CACHE_DIR/$BEATMAP.osu was not written"; exit 1; }
echo "    cached: $(wc -c < "$CACHE_DIR/$BEATMAP.osu") bytes at $CACHE_DIR/$BEATMAP.osu"

echo "PASS: sidecar healthy, pp computed (vanilla+relax), .osu cached to disk"
