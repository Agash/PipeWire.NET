#!/usr/bin/env bash
#
# The full verification run for a Linux machine with a desktop session, as opposed to CI's
# headless runner: every test that can run here, and what the run left behind.
#
#   build/verify-linux.sh                 # both frameworks, the whole suite, both legs
#   build/verify-linux.sh '<filter>'      # a targeted run: that filter, private session only
#
#   PWNET_VERIFY_OUT     where logs and snapshots go (default ~/pwnet-verify, wiped first)
#   PWNET_VERIFY_TFMS    frameworks to run (default "net10.0 net11.0"), one after the other -
#                        in parallel both publish same-named nodes to one daemon
#   PWNET_VERIFY_HANG    seconds without progress before a test host counts as hung (default 240)
#   PWNET_VERIFY_LIVE    "full" (default) runs the whole suite on the desktop session too; "cards"
#                        runs only the tests that need its sound card
#   PWNET_PATCHED_MODULES a module directory whose libpipewire-module-metadata carries the
#                        fixes in repro/module-metadata.patch (default ~/pw-mods-patched); used by the private
#                        sessions when its version matches the installed daemon
#   PWNET_PATCHED_LIB    a directory holding a libpipewire-0.3.so.0 built with
#                        repro/libpipewire-permissions.patch (default ~/pw-lib-patched): the KillsTheDaemon
#                        legs' daemons load it, and so does the desktop stack during the live leg
#   PWNET_VERIFY_LIVE_PATCHED "1" (default) runs the live leg on the desktop's own daemon with the
#                        patched library and modules swapped in for its duration and restored after
#                        (on any exit), so only the sound-card split is excluded there; "0" keeps it
#                        stock and excludes what a stock 1.6.8 daemon cannot survive
#
# Three upstream bugs in PipeWire 1.6.8 decide how the legs are set up (HANDOFF: "The session
# wedge", the bind-window section after it, and the update_permissions crash). The private sessions
# load module-metadata with repro/module-metadata.patch, so a store withdrawn while WirePlumber's
# bind is pending cannot freeze the session and a change a served store makes while another client
# binds it still reaches the consumers already bound. The KillsTheDaemon category runs against a
# daemon of its own loading libpipewire with repro/libpipewire-permissions.patch. The live leg runs the
# desktop's own stack with both swapped in for its duration (PWNET_VERIFY_LIVE_PATCHED), so it runs
# everything; kept stock, it leaves out the tests a stock daemon cannot survive (LIVE_EXCLUDE,
# KillsTheDaemon), which the private legs still run.
# Every leg is probed while it runs; a session that stops answering ends the leg with its evidence.
#
# Every test host is watched. One that stops making progress is dumped, both halves of every
# stack are read from the dump, and it is killed so the run goes on; one that crashes has its dump
# read the same way; a daemon that dies has its core read with symbols. See build/verify-diagnose.sh.
# Nothing here attaches to a live process - the box's ptrace_scope forbids it - so every conclusion
# comes from a dump, and the test that was running is named from the test trace.
#
# The legs:
#
#   private  A throwaway session (build/session.sh) per framework, without the sound cards: a
#            private WirePlumber that fights the desktop's for them retries the open in a loop
#            that starves the whole session - metadata relays, routing, everything. Null audio
#            devices stand in, so default-device tests run. A snapshot is taken before and after,
#            and anything the tests left - nodes, ports, links, clients, daemon descriptors - is a
#            leak (build/session-snapshot.py).
#
#   live     The desktop session, which owns the cards: its real hardware, its real WirePlumber
#            and every node the desktop has. By default the whole suite runs here too, not only
#            the tests that need a card, because a private session with null devices cannot show
#            how the library behaves among the nodes and policy a user actually has. Tests that
#            change anything (a profile, a route volume, a default) put it back; the snapshot
#            comparison shows whether they did.
#
#   own      Per framework, PenTest and KillsTheDaemon each in a private session of their own:
#            PenTest's churn would be what anything sharing its session fails on, and
#            KillsTheDaemon aborts a stock daemon, so it gets one loading the patched library.
#
# At the end every discovered test (--list-tests) has to have a result in some leg, or the run
# fails naming it: zero failures and zero skips say nothing about tests no filter selected.
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

# Used only when the live leg runs the stock desktop stack (PWNET_VERIFY_LIVE_PATCHED=0 or no
# patched build to swap in). The tests that export and withdraw metadata stores. On a stock 1.6.8
# daemon they reach the upstream module-metadata wedge (a withdrawal while WirePlumber's bind ping is pending leaves
# WirePlumber busy for ever), which on the desktop session takes the machine's audio down with it.
# The last is the regression test for the bind-window bug and fails on a stock daemon by design.
# They run in the private legs, whose daemon loads the fixed module.
LIVE_EXCLUDE=(
  AStoreThisProcessServes_IsOrderedByTheBarrier
  EverythingBuiltDeliberatelyLeftBehind_CanStillBeTornDown
  AStoreClearedByItsServer_EmptiesEveryBoundConsumer
  AnExternalToolClearingAnExportedStore_EmptiesOurConsumer
  AStoreClearedLocally_EmptiesItsOwnCache
  AKeyWithAnEmbeddedNul_IsRefusedBeforeItCanDesyncTheStore
  AStoreWeServe_LeavesTheSessionResponsive
  AStoreWeServe_ReportsEveryChangeItAccepts
  ClearingAStoreWeServe_EmptiesEverySubjectInOneCallEach
  ClearingAnEmptyStore_IsNotAnError
  AUnexportedStore_StaysInsideThisProcess
  ClearingAStoreWeServeThroughItsBinding_EmptiesIt
  AChangeWhileAnotherClientBinds_ReachesTheConsumersAlreadyBound
  BindingAStoreThroughTheConnectionThatServesIt_IsRefusedRatherThanHanging
)
NOT_LIVE_EXCLUDED="$(printf 'FullyQualifiedName!~%s&' "${LIVE_EXCLUDE[@]}")"; NOT_LIVE_EXCLUDED="${NOT_LIVE_EXCLUDED%&}"
NOT_LIVE="$(printf 'FullyQualifiedName!~%s&' "${LIVE_TESTS[@]}")"; NOT_LIVE="${NOT_LIVE%&}"

export DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1 MSBUILDDISABLENODEREUSE=1
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"

HANG_SECONDS="${PWNET_VERIFY_HANG:-240}"
LIVE_MODE="${PWNET_VERIFY_LIVE:-full}"
PATCHED_MODULES="${PWNET_PATCHED_MODULES:-$HOME/pw-mods-patched}"
PATCHED_LIB="${PWNET_PATCHED_LIB:-$HOME/pw-lib-patched}"
LIVE_PATCHED="${PWNET_VERIFY_LIVE_PATCHED:-1}"

# The desktop session's environment, saved before build/session.sh points XDG_* at its private
# session and leaves them there.
ORIG_XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
ORIG_XDG_CONFIG_HOME="${XDG_CONFIG_HOME:-$HOME/.config}"
ORIG_XDG_STATE_HOME="${XDG_STATE_HOME:-$HOME/.local/state}"
ORIG_XDG_DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"

rm -rf "$OUT"
mkdir -p "$OUT/dumps"
cd "$ROOT"

# shellcheck source=build/verify-diagnose.sh
source "$ROOT/build/verify-diagnose.sh"

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
WEDGED=0

summarise() {
  local log="$1"
  grep -E "^  (total|failed|succeeded|skipped):" "$log"
  if grep -qE "^  failed: [1-9]" "$log"; then FAILED=1; fi
  # A skip is a test that did not check anything here, so it fails the run as well.
  if grep -qE "^  skipped: [1-9]" "$log"; then echo "::error::tests skipped in $(basename "$log" .log)"; FAILED=1; fi
  grep -aE "^failed " "$log" | sed 's/^/  /'
  # A skip is a test that could not run here, with its reason. Listed so none goes unread.
  grep -aA1 "^skipped " "$log" | grep -v '^--' | sed 's/^/  /'
}

# Tests whose subject is a refusal the daemon logs as an error. Anything else the daemon logs at
# error level during a leg fails the run: a count alone says nothing, and a real error hides easily
# among expected ones.
#   EndsWithNoDeviceInCommon_DoNotSettleOnOne  two DMA-BUF ends with no device in common; the link
#                                              failing to negotiate ("no more output formats") is
#                                              the answer the test asserts
#   *=protocol-native connection_data EIO      a client closing with data still unread, logged as
#                                              an error by a kernel/daemon race rather than as the
#                                              info-level "disconnected" the same close gets
#                                              otherwise: unix_release_sock writes the daemon
#                                              socket's sk_shutdown and then sk_err = ECONNRESET,
#                                              unix_poll reads them in the same order, so a poll
#                                              concurrent with the close can see the error without
#                                              the hangup; protocol-native tests HUP first, maps a
#                                              bare ERR to -EIO and logs it at error. Measured: 1 in
#                                              ~410k context closes (PenHarness.Contexts soak, daemon
#                                              at debug), the client having closed with the registry
#                                              dump unread exactly like the clean ones around it.
#   AStreamThatCannotGoOn_...=a.test.said.so   the subject of the test is pw_stream_set_error, so
#   EveryStreamMember_...=a.deliberate.error   the daemon logging that error is what proves the call
#                                              reached it. Keyed to the message each test sends, so
#                                              any other error during the same test still fails.
#   AnAllocatorThatMisbehaves_...=invalid.     an application allocator that declines, backs too few
#   memory.type                                planes, or hands back a bad descriptor leaves the
#                                              buffer unbacked, and the library publishes the pool
#                                              anyway so the whole pool fails rather than one buffer
#                                              reaching a consumer half-backed. The daemon refusing
#                                              do_port_use_buffers is that design, seen from its end.
# Entries are "Test" (anything logged during it), or "Test=regex" / "*=regex" for matching lines
# only. An entry needs the error explained, not merely seen: an allowance for something not
# understood is how a real defect gets waved through.
export PWNET_EXPECTED_DAEMON_ERRORS='EndsWithNoDeviceInCommon_DoNotSettleOnOne *=^mod\.protocol-native:.*connection_data:.client.*error.-N.\(Input/output.error\)$ AStreamThatCannotGoOn_CanSaySoAndDisposeEitherWay=a.test.said.so EveryStreamMember_WorksConnectedAndIsQuietAfterDisposal=a.deliberate.error,.to.prove.the.call.reaches.the.daemon AnAllocatorThatMisbehaves_LeavesTheBufferUnbacked=invalid.memory.type'

report_daemon_errors() {
  local label="$1" log="$2" trace="$3" out unexpected
  out="$(pwnet_attribute_errors "$log" "$trace")"
  unexpected="$(printf '%s\n' "$out" | tail -1)"
  echo "daemon errors ($label), by the test running when each was logged:"
  printf '%s\n' "$out" | sed '$d'
  if [ "${unexpected:-0}" -gt 0 ]; then
    echo "::error::$unexpected unexpected daemon error(s) during the $label leg"
    FAILED=1
  fi
}

# Runs one test leg with a watchdog. Progress is the test log or the test trace growing; a host
# that shows neither for HANG_SECONDS, or outlives the leg's budget, is diagnosed and killed.
# Returns dotnet test's exit code.
run_watched() {
  local label="$1" tfm="$2" filter="$3" log="$4" budget="$5" runtime="$6"
  shift 6
  local trace="$OUT/$label-trace.log"
  : > "$trace"

  env "$@" PWNET_TEST_TRACE="$trace" \
    dotnet test PipeWire.NET.slnx -c Debug --nologo --no-build -f "$tfm" --output Detailed --settings tests.runsettings \
    --filter "$filter" > "$log" 2>&1 &
  local runner=$! last="" still=0 started=$SECONDS host unanswered=0 probed=$SECONDS

  while kill -0 "$runner" 2>/dev/null; do
    sleep 15

    # The session, not just the test host: a wedged daemon makes every later test wait out its
    # own timeout, which reads as a slow run rather than a dead one.
    if [ $((SECONDS - probed)) -ge 60 ]; then
      probed=$SECONDS
      if pwnet_session_answers "$runtime"; then unanswered=0; else unanswered=$((unanswered + 1)); fi
      if [ "$unanswered" -ge 2 ]; then
        echo "::error::the $label session stopped answering while the suite ran"
        pwnet_wedge_signature "$runtime"
        echo "  running test(s): $(pwnet_running_test "$trace" | tr '\n' ' ')"
        host="$(pgrep -f "bin/Debug/$tfm/PipeWire.NET.Tests" | head -1)"
        [ -n "$host" ] && pwnet_diagnose_hang "$host" "$label" "$trace" "$OUT/dumps"
        FAILED=1
        WEDGED=1
        unanswered=0
      fi
    fi

    local now
    now="$(stat -c %s "$log" 2>/dev/null || echo 0):$(stat -c %s "$trace" 2>/dev/null || echo 0)"
    if [ "$now" = "$last" ]; then still=$((still + 15)); else still=0; last="$now"; fi

    if [ "$still" -ge "$HANG_SECONDS" ] || [ $((SECONDS - started)) -ge "$budget" ]; then
      host="$(pgrep -f "bin/Debug/$tfm/PipeWire.NET.Tests" | head -1)"
      if [ -n "$host" ]; then
        pwnet_diagnose_hang "$host" "$label" "$trace" "$OUT/dumps"
      fi
      still=0
      FAILED=1
    fi
  done

  wait "$runner"
  local rc=$?
  if [ "$rc" -ge 128 ]; then
    echo "::error::the $label test host crashed (exit $rc); the counts below are partial"
    FAILED=1
  fi
  pwnet_diagnose_crash_dumps "$OUT/dumps" "$ROOT/tests/PipeWire.NET.Tests/bin/Debug/$tfm/PipeWire.NET.Tests" "$trace"
  return "$rc"
}

# shellcheck source=build/session.sh
source "$ROOT/build/session.sh"

# One category in a private session of its own: PenTest, whose churn would otherwise be what
# everything sharing its session fails on (docs/running-tests.md), and KillsTheDaemon, whose tests
# abort a stock 1.6.8 daemon and so get a daemon that loads the patched library ($4). The daemon
# is checked to have mapped that library, so a run cannot pass on a stock one by mistake.
own_session_leg() {
  local tfm="$1" label="$2" filter="$3" lib="${4:-}"
  echo "########## private: $tfm $label (a session of its own${lib:+, patched libpipewire}) ##########"
  export PWNET_SESSION_LOG_DIR="$OUT/$tfm-$label-session"
  mkdir -p "$PWNET_SESSION_LOG_DIR"
  local started
  if [ -n "$lib" ]; then
    LD_LIBRARY_PATH="$lib" PWNET_SESSION_NO_HARDWARE=1 PWNET_SESSION_NULL_AUDIO=1 pwnet_session_start; started=$?
  else
    PWNET_SESSION_NO_HARDWARE=1 PWNET_SESSION_NULL_AUDIO=1 pwnet_session_start; started=$?
  fi
  if [ "$started" -ne 0 ]; then FAILED=1; return; fi
  sleep 3
  DAEMON_PID="$PWNET_PW_PID"
  if [ -n "$lib" ] && ! grep -q "$lib/libpipewire" "/proc/$DAEMON_PID/maps"; then
    echo "::error::the $tfm $label daemon did not load $lib/libpipewire-0.3.so.0"
    FAILED=1
  fi
  run_watched "$tfm-$label" "$tfm" "$filter" "$OUT/$tfm-$label.log" 600 "$XDG_RUNTIME_DIR" \
    PIPEWIRE_DEBUG=2 PIPEWIRE_LOG="$OUT/$tfm-$label-client.log"
  echo "$tfm $label exit=$?"
  summarise "$OUT/$tfm-$label.log"
  if ! kill -0 "$DAEMON_PID" 2>/dev/null; then
    echo "::error::the daemon did not survive the $tfm $label leg"
    pwnet_diagnose_daemon "$DAEMON_PID" "$tfm-$label" "$OUT/dumps"
    FAILED=1
  fi
  report_daemon_errors "$tfm-$label" "$PWNET_SESSION_LOG_DIR/pipewire.log" "$OUT/$tfm-$label-trace.log"
  pwnet_session_stop
}

# The desktop stack on the patched library and modules for the live leg, and back afterwards. Set
# in the user manager's environment and applied by restarting the three services, the same restart
# a wedge recovery does; restored by the EXIT trap too, so an interrupted run does not leave the
# machine's audio on a build of ours.
LIVE_SWAPPED=0
live_stack_restart() {
  env "${LIVE_ENV[@]}" systemctl --user restart pipewire pipewire-pulse wireplumber
  local i
  for i in $(seq 1 30); do pwnet_session_answers "$ORIG_XDG_RUNTIME_DIR" && return 0; sleep 1; done
  return 1
}
live_swap_in() {
  env "${LIVE_ENV[@]}" systemctl --user set-environment \
    "PIPEWIRE_MODULE_DIR=$PATCHED_MODULES" "LD_LIBRARY_PATH=$PATCHED_LIB"
  LIVE_SWAPPED=1
  live_stack_restart
}
live_swap_out() {
  [ "$LIVE_SWAPPED" = 1 ] || return 0
  env "${LIVE_ENV[@]}" systemctl --user unset-environment PIPEWIRE_MODULE_DIR LD_LIBRARY_PATH
  LIVE_SWAPPED=0
  live_stack_restart
}
trap live_swap_out EXIT

for TFM in $TFMS; do
  echo "########## private: $TFM ##########"
  export PWNET_SESSION_LOG_DIR="$OUT/$TFM-session"
  mkdir -p "$PWNET_SESSION_LOG_DIR"
  # The patched module-metadata, when it matches the installed daemon: the private legs exist to
  # check this library, and an upstream wedge under them would fail everything after it.
  if pwnet_patched_modules_usable "$PATCHED_MODULES"; then
    export PIPEWIRE_MODULE_DIR="$PATCHED_MODULES"
    echo "private session modules: $PATCHED_MODULES (module-metadata built with repro/module-metadata.patch)"
  else
    unset PIPEWIRE_MODULE_DIR
    echo "private session modules: stock (no matching patched module-metadata at $PATCHED_MODULES)"
  fi

  PWNET_SESSION_NO_HARDWARE=1 PWNET_SESSION_NULL_AUDIO=1 pwnet_session_start || { FAILED=1; continue; }

  # Settled first: WirePlumber is still creating its own objects for a moment after it appears.
  sleep 3
  python3 "$ROOT/build/session-snapshot.py" snap "$OUT/$TFM-before.json" "$PWNET_PW_PID" "$PWNET_WP_PID"

  if [ -n "$FILTER" ]; then
    # A targeted filter still never reaches KillsTheDaemon: a class-level filter such as
    # "FullyQualifiedName~ParameterAndMetadataTests" otherwise pulls the category's tests in, the
    # daemon aborts, and every test after it fails for that reason. The live-card tests are kept out
    # for the same reason as in a full run: this leg has no sound card, so they could only skip.
    LEG_FILTER="($FILTER)&TestCategory!=KillsTheDaemon&$NOT_LIVE"
  else
    # PenTest gets a session of its own below (docs/running-tests.md): its churn would otherwise
    # be what everything sharing the session fails on.
    LEG_FILTER="TestCategory!=KillsTheDaemon&TestCategory!=PenTest&$NOT_LIVE"
  fi

  DAEMON_PID="$PWNET_PW_PID"
  run_watched "$TFM" "$TFM" "$LEG_FILTER" "$OUT/$TFM.log" 2700 "$XDG_RUNTIME_DIR" \
    PIPEWIRE_DEBUG=2 PIPEWIRE_LOG="$OUT/$TFM-client.log"
  echo "$TFM exit=$?"
  summarise "$OUT/$TFM.log"

  if kill -0 "$PWNET_PW_PID" 2>/dev/null; then
    sleep 2
    python3 "$ROOT/build/session-snapshot.py" snap "$OUT/$TFM-after.json" "$PWNET_PW_PID" "$PWNET_WP_PID"
    python3 "$ROOT/build/session-snapshot.py" compare "$OUT/$TFM-before.json" "$OUT/$TFM-after.json" || FAILED=1
  else
    echo "::error::the daemon did not survive the $TFM leg"
    pwnet_diagnose_daemon "$DAEMON_PID" "$TFM" "$OUT/dumps"
    FAILED=1
  fi

  report_daemon_errors "$TFM" "$PWNET_SESSION_LOG_DIR/pipewire.log" "$OUT/$TFM-trace.log"
  echo "wireplumber errors: $(grep -c '^\[E\]\|^E ' "$PWNET_SESSION_LOG_DIR/wireplumber.log" 2>/dev/null || echo 0)"
  pwnet_session_stop

  if [ -z "$FILTER" ]; then
    own_session_leg "$TFM" pen "TestCategory=PenTest"
    if pwnet_patched_lib_usable "$PATCHED_LIB"; then
      own_session_leg "$TFM" ktd "TestCategory=KillsTheDaemon" "$PATCHED_LIB"
    else
      echo "::error::KillsTheDaemon not run for $TFM: it needs a daemon with repro/libpipewire-permissions.patch at $PATCHED_LIB"
      FAILED=1
    fi
  fi
  unset PIPEWIRE_MODULE_DIR
done

if [ -z "$FILTER" ]; then
  echo "########## live: desktop session ($LIVE_MODE) ##########"
  if [ -S "$ORIG_XDG_RUNTIME_DIR/pipewire-0" ]; then
    # The desktop's session bus too: systemctl --user reaches the user manager over it, and a
    # private session may have left a bus of its own in the environment.
    LIVE_ENV=(XDG_RUNTIME_DIR="$ORIG_XDG_RUNTIME_DIR" XDG_CONFIG_HOME="$ORIG_XDG_CONFIG_HOME"
              XDG_STATE_HOME="$ORIG_XDG_STATE_HOME" XDG_DATA_HOME="$ORIG_XDG_DATA_HOME"
              DBUS_SESSION_BUS_ADDRESS="unix:path=$ORIG_XDG_RUNTIME_DIR/bus")
    LIVE_ON_PATCHED=0
    if [ "$LIVE_PATCHED" = 1 ] && pwnet_patched_modules_usable "$PATCHED_MODULES" && pwnet_patched_lib_usable "$PATCHED_LIB"; then
      if live_swap_in && grep -q "$PATCHED_LIB/libpipewire" "/proc/$(pgrep -u "$(id -u)" -x pipewire | head -1)/maps" \
         && grep -q "$PATCHED_MODULES/libpipewire-module-metadata" "/proc/$(pgrep -u "$(id -u)" -x pipewire | head -1)/maps"; then
        LIVE_ON_PATCHED=1
        echo "desktop stack: patched library and modules loaded for the live leg"
      else
        echo "::error::the desktop stack did not come up on the patched library and modules; running it stock"
        live_swap_out
        FAILED=1
      fi
    fi

    # On the patched stack everything runs here that has a reason to; on a stock one the tests a
    # 1.6.8 daemon cannot survive stay in the private legs. PenTest gets its own pass below either
    # way, after the suite, so its churn is nobody else's problem.
    if [ "$LIVE_MODE" != "full" ]; then
      LIVE_FILTER="$LIVE_ONLY"
    elif [ "$LIVE_ON_PATCHED" = 1 ]; then
      LIVE_FILTER="TestCategory!=PenTest"
    else
      LIVE_FILTER="TestCategory!=KillsTheDaemon&TestCategory!=PenTest&$NOT_LIVE_EXCLUDED"
    fi

    for TFM in $TFMS; do
      LIVE_PW="$(pgrep -u "$(id -u)" -x pipewire | head -1)"
      LIVE_WP="$(pgrep -u "$(id -u)" -x wireplumber | head -1)"
      LIVE_SINCE="$(date '+%Y-%m-%d %H:%M:%S')"
      env "${LIVE_ENV[@]}" python3 "$ROOT/build/session-snapshot.py" snap "$OUT/live-$TFM-before.json" "$LIVE_PW" "$LIVE_WP"

      WEDGED=0
      run_watched "live-$TFM" "$TFM" "$LIVE_FILTER" "$OUT/live-$TFM.log" 2700 "$ORIG_XDG_RUNTIME_DIR" "${LIVE_ENV[@]}"
      echo "live $TFM exit=$?"
      summarise "$OUT/live-$TFM.log"

      # A wedged desktop stack stays wedged until it is restarted, and it is the machine's audio.
      if [ "$WEDGED" = 1 ] || ! pwnet_session_answers "$ORIG_XDG_RUNTIME_DIR"; then
        echo "::error::the desktop session was left unanswering; restarting its audio stack"
        pwnet_wedge_signature "$ORIG_XDG_RUNTIME_DIR"
        env "${LIVE_ENV[@]}" systemctl --user restart pipewire pipewire-pulse wireplumber
        sleep 5
        LIVE_PW="$(pgrep -u "$(id -u)" -x pipewire | head -1)"
        LIVE_WP="$(pgrep -u "$(id -u)" -x wireplumber | head -1)"
        FAILED=1
      fi

      if kill -0 "$LIVE_PW" 2>/dev/null; then
        sleep 2
        env "${LIVE_ENV[@]}" python3 "$ROOT/build/session-snapshot.py" snap "$OUT/live-$TFM-after.json" "$LIVE_PW" "$LIVE_WP"
        # A desktop session opens real cards: resuming a device and switching a profile take
        # descriptors the session manager then keeps, which is not what a leak per test looks like.
        env "${LIVE_ENV[@]}" PWNET_FD_SLACK=64 python3 "$ROOT/build/session-snapshot.py" compare "$OUT/live-$TFM-before.json" "$OUT/live-$TFM-after.json" || FAILED=1
      else
        echo "::error::the desktop daemon did not survive the live $TFM leg"
        pwnet_diagnose_daemon "$LIVE_PW" "live-$TFM" "$OUT/dumps"
        FAILED=1
      fi

      # The desktop daemons log to the journal, not to a file of ours; short-precise for the
      # sub-second stamps that tie each error to a test.
      journalctl --user --since "$LIVE_SINCE" -p err -u pipewire -u wireplumber --no-pager -q -o short-precise \
        2>/dev/null > "$OUT/live-$TFM-daemon-errors.log"
      report_daemon_errors "live-$TFM" "$OUT/live-$TFM-daemon-errors.log" "$OUT/live-$TFM-trace.log"

      if [ "$LIVE_MODE" = "full" ]; then
        echo "########## live: $TFM PenTest ##########"
        LIVE_SINCE="$(date '+%Y-%m-%d %H:%M:%S')"
        run_watched "live-$TFM-pen" "$TFM" "TestCategory=PenTest" "$OUT/live-$TFM-pen.log" 600 "$ORIG_XDG_RUNTIME_DIR" "${LIVE_ENV[@]}"
        echo "live $TFM PenTest exit=$?"
        summarise "$OUT/live-$TFM-pen.log"
        journalctl --user --since "$LIVE_SINCE" -p err -u pipewire -u wireplumber --no-pager -q -o short-precise \
          2>/dev/null > "$OUT/live-$TFM-pen-daemon-errors.log"
        report_daemon_errors "live-$TFM-pen" "$OUT/live-$TFM-pen-daemon-errors.log" "$OUT/live-$TFM-pen-trace.log"
        if ! pwnet_session_answers "$ORIG_XDG_RUNTIME_DIR"; then
          echo "::error::the desktop session stopped answering after PenTest; restarting its audio stack"
          live_stack_restart
          FAILED=1
        fi
      fi
    done
    live_swap_out
  else
    echo "no desktop session at $ORIG_XDG_RUNTIME_DIR, so the live leg did not run"
    FAILED=1
  fi
fi

# Every test the suite has must have a result in some leg: a filter that removes a test from every
# leg, or a category nothing selects, is otherwise invisible - a run of zero failures and zero
# skips says nothing about tests it never picked. Names come from --list-tests and from the
# per-test result lines of --output Detailed.
if [ -z "$FILTER" ]; then
  for TFM in $TFMS; do
    dotnet test PipeWire.NET.slnx -c Debug --nologo --no-build -f "$TFM" --list-tests 2>/dev/null \
      | grep -a '^  ' | sed 's/^  //' | sort -u > "$OUT/$TFM-inventory.txt"
    cat "$OUT"/$TFM*.log "$OUT"/live-$TFM*.log 2>/dev/null \
      | grep -aE '^(passed|failed|skipped) ' \
      | sed -E 's/^(passed|failed|skipped) //; s/ \(([0-9]+h )?([0-9]+m )?([0-9]+s )?[0-9]+ms\)$//' \
      | sort -u > "$OUT/$TFM-ran.txt"
    never="$(comm -23 "$OUT/$TFM-inventory.txt" "$OUT/$TFM-ran.txt")"
    echo "accounting $TFM: $(wc -l < "$OUT/$TFM-inventory.txt") discovered, $(wc -l < "$OUT/$TFM-ran.txt") with a result"
    if [ -n "$never" ]; then
      echo "::error::tests that ran in no leg ($TFM):"
      printf '%s\n' "$never" | sed 's/^/  /'
      FAILED=1
    fi
  done
fi

echo "dumps: $(find "$OUT/dumps" -type f | wc -l)"
echo "VERIFY-DONE failed=$FAILED"
exit "$FAILED"
