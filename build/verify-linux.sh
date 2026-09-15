#!/usr/bin/env bash
#
# The full verification run for a Linux machine with a desktop session, as opposed to CI's
# headless runner: every test that can run here, and what the run left behind.
#
#   build/verify-linux.sh                 # both frameworks, the whole suite, plus the live leg
#   build/verify-linux.sh '<filter>'      # a targeted run: that filter, private session only
#
#   PWNET_VERIFY_OUT     where logs and snapshots go (default ~/pwnet-verify, wiped first)
#   PWNET_VERIFY_TFMS    frameworks to run (default "net10.0 net11.0"), one after the other -
#                        in parallel both publish same-named nodes to one daemon
#
# Two legs:
#
#   private  A throwaway session (build/session.sh) per framework, without the sound cards: a
#            private WirePlumber that fights the desktop's for them retries the open in a loop
#            that starves the whole session - metadata relays, routing, everything. Null audio
#            devices stand in, so default-device tests run. A snapshot is taken before and after,
#            and anything the tests left - nodes, ports, links, clients, daemon descriptors - is a
#            leak (build/session-snapshot.py).
#
#   live     The tests that need a real card - its profiles, routes and mixer - against the
#            desktop session, which owns the cards. The ones that change anything (a profile, a
#            route volume) put it back.
#
# KillsTheDaemon tests are never run: they take an upstream daemon down by design, and every
# test after them would fail for that reason.
#
# On a box whose logind has KillUserProcesses=yes, anything started from an ssh session dies with
# it. Start this as a transient user unit instead:
#   systemd-run --user --unit=pwnet-verify bash -c 'build/verify-linux.sh > ~/pwnet-verify.log 2>&1'

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${PWNET_VERIFY_OUT:-$HOME/pwnet-verify}"
TFMS="${PWNET_VERIFY_TFMS:-net10.0 net11.0}"
FILTER="${1:-}"

# The tests that need the desktop session's real card. By name, not by category: they share their
# classes with tests that belong in the private leg. Both filters are built from this one list, so
# a test cannot end up in neither leg or in both.
LIVE_TESTS=(
  ReapplyingTheActiveRoute_IsAcceptedAndChangesNothing
  SwitchingACardProfileAndPuttingItBack_ReplacesItsNodesBothTimes
  ADevicesRoutes_CarryTheirOwnVolumeAndSurviveBeingReadBack
  ADevicesProfiles_AreEnumerableAndTheCurrentOneIsAmongThem
  SettingARouteVolumeAndRestoringIt_ChangesTheHardwareMixer
  ADevice_ReportsTheProfilesAndRoutesItsCardOffers
  ADeviceDescribesItsOwnParameters_Too
  ADeviceItsNodesAndTheDefaultSink_AgreeWithEachOther
)
LIVE_ONLY="$(printf 'FullyQualifiedName~%s|' "${LIVE_TESTS[@]}")"; LIVE_ONLY="${LIVE_ONLY%|}"
NOT_LIVE="$(printf 'FullyQualifiedName!~%s&' "${LIVE_TESTS[@]}")"; NOT_LIVE="${NOT_LIVE%&}"

export DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1 MSBUILDDISABLENODEREUSE=1
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"

rm -rf "$OUT"
mkdir -p "$OUT/dumps"
cd "$ROOT"

# A crash leaves a managed dump to read instead of a bare exit code.
ulimit -c unlimited
export DOTNET_DbgEnableMiniDump=1 DOTNET_DbgMiniDumpType=4 DOTNET_DbgMiniDumpName="$OUT/dumps/pwnet.%p.dmp"

# MinVer derives the version from git and warns when there is no work tree - a copy synced to a
# test box has none. The version is irrelevant to a test run, so skip it there rather than read a
# warning that says nothing about the code.
MINVER=()
git -C "$ROOT" rev-parse --is-inside-work-tree >/dev/null 2>&1 || MINVER=(-p:MinVerSkip=true)

# The local-v4l2 tests load a loopback camera and leave it loaded, by design. Loading it before the
# first snapshot keeps the camera's device and node out of the leak comparison, where they would
# otherwise read as something the suite left behind.
if ! [ -e /dev/video42 ]; then
  sudo -n modprobe v4l2loopback devices=1 video_nr=42 card_label=pwnet-virtual-cam exclusive_caps=0 \
    || echo "could not preload v4l2loopback; the camera tests will load it themselves"
fi

dotnet build PipeWire.NET.slnx -c Debug -v q --nologo -m:1 "${MINVER[@]}" > "$OUT/build.log" 2>&1
BUILD_RC=$?
echo "build exit=$BUILD_RC errors=$(grep -c ': error ' "$OUT/build.log") warnings=$(grep -c ': warning ' "$OUT/build.log")"
[ "$BUILD_RC" -eq 0 ] || exit 1

FAILED=0

summarise() {
  local log="$1"
  grep -E "^  (total|failed|succeeded|skipped):" "$log"
  if grep -qE "^  failed: [1-9]" "$log"; then FAILED=1; fi
  grep -aE "^failed " "$log" | sed 's/^/  /'
  # A skip is a test that could not run here, with its reason. Listed so none goes unread.
  grep -aA1 "^skipped " "$log" | grep -v '^--' | sed 's/^/  /'
}

# shellcheck source=build/session.sh
source "$ROOT/build/session.sh"

for TFM in $TFMS; do
  echo "########## private: $TFM ##########"
  export PWNET_SESSION_LOG_DIR="$OUT/$TFM-session"
  mkdir -p "$PWNET_SESSION_LOG_DIR"
  PWNET_SESSION_NO_HARDWARE=1 PWNET_SESSION_NULL_AUDIO=1 pwnet_session_start || { FAILED=1; continue; }

  # Settled first: WirePlumber is still creating its own objects for a moment after it appears.
  sleep 3
  python3 "$ROOT/build/session-snapshot.py" snap "$OUT/$TFM-before.json" "$PWNET_PW_PID" "$PWNET_WP_PID"

  if [ -n "$FILTER" ]; then
    LEG_FILTER="$FILTER"
  else
    LEG_FILTER="TestCategory!=KillsTheDaemon&$NOT_LIVE"
  fi

  PIPEWIRE_DEBUG=2 PIPEWIRE_LOG="$OUT/$TFM-client.log" \
    timeout 2700 dotnet test PipeWire.NET.slnx -c Debug --nologo --no-build -f "$TFM" \
    --filter "$LEG_FILTER" > "$OUT/$TFM.log" 2>&1
  TEST_RC=$?
  echo "$TFM exit=$TEST_RC"
  # 128 and up is a signal: the test host died, so the counts below cover only what ran before it.
  if [ "$TEST_RC" -ge 128 ]; then
    echo "::error::the $TFM test host crashed (exit $TEST_RC); the counts below are partial"
    FAILED=1
  fi
  summarise "$OUT/$TFM.log"

  if kill -0 "$PWNET_PW_PID" 2>/dev/null; then
    sleep 2
    python3 "$ROOT/build/session-snapshot.py" snap "$OUT/$TFM-after.json" "$PWNET_PW_PID" "$PWNET_WP_PID"
    python3 "$ROOT/build/session-snapshot.py" compare "$OUT/$TFM-before.json" "$OUT/$TFM-after.json" || FAILED=1
  else
    echo "::error::the daemon did not survive the $TFM leg"
    FAILED=1
  fi

  echo "daemon errors: $(grep -c '^\[E\]\|^E ' "$PWNET_SESSION_LOG_DIR/pipewire.log" 2>/dev/null || echo 0)" \
       "wireplumber errors: $(grep -c '^\[E\]\|^E ' "$PWNET_SESSION_LOG_DIR/wireplumber.log" 2>/dev/null || echo 0)"
  pwnet_session_stop
done

if [ -z "$FILTER" ]; then
  echo "########## live: desktop session ##########"
  LIVE_RUNTIME="/run/user/$(id -u)"
  if [ -S "$LIVE_RUNTIME/pipewire-0" ]; then
    for TFM in $TFMS; do
      XDG_RUNTIME_DIR="$LIVE_RUNTIME" timeout 600 dotnet test PipeWire.NET.slnx -c Debug --nologo --no-build \
        -f "$TFM" --filter "$LIVE_ONLY" > "$OUT/live-$TFM.log" 2>&1
      echo "live $TFM exit=$?"
      summarise "$OUT/live-$TFM.log"
    done
  else
    echo "no desktop session at $LIVE_RUNTIME, so the live leg did not run"
    FAILED=1
  fi
fi

echo "dumps: $(find "$OUT/dumps" -type f | wc -l)"
echo "VERIFY-DONE failed=$FAILED"
exit "$FAILED"
