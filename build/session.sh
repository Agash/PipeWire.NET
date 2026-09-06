#!/usr/bin/env bash
#
# Starts a private PipeWire session and waits for it to answer, or fails saying why.
#
#   source build/session.sh
#   pwnet_session_start          # exports XDG_*, starts the daemons, waits
#   ...run tests...
#   pwnet_session_stop           # kills them; safe to call twice
#
# A private session, not just a private socket. WirePlumber persists default nodes, saved routes
# and profiles under XDG_STATE_HOME, so sharing it lets one run's writes decide the next run's
# starting state.
#
# The daemons' own output goes to files from the moment they start. Collecting it only after a
# failure misses everything they said while coming up, which is exactly when it matters.

PWNET_SESSION_LOG_DIR="${PWNET_SESSION_LOG_DIR:-${RUNNER_TEMP:-/tmp}}"

pwnet_session_start() {
  local attempts="${1:-40}"

  export XDG_RUNTIME_DIR="$(mktemp -d)"
  export XDG_CONFIG_HOME="$(mktemp -d)"
  export XDG_STATE_HOME="$(mktemp -d)"
  export XDG_DATA_HOME="$(mktemp -d)"

  # WirePlumber 0.4.x treats a missing D-Bus session bus as fatal and exits within
  # milliseconds, leaving a session that answers pw-cli but links nothing (every streaming
  # test then hangs). Headless runners have no bus, so bring a private one when none is set.
  # No --fork: this dbus-daemon vintage rejects --print-pid, and a backgrounded foreground
  # process hands out a usable pid either way.
  if [ -z "${DBUS_SESSION_BUS_ADDRESS:-}" ] && command -v dbus-daemon >/dev/null 2>&1; then
    local bus_addr_file="$PWNET_SESSION_LOG_DIR/dbus-address"
    rm -f "$bus_addr_file"
    dbus-daemon --session --print-address > "$bus_addr_file" 2>/dev/null &
    PWNET_DBUS_PID=$!

    local bus_wait
    for ((bus_wait = 1; bus_wait <= 40; bus_wait++)); do
      if [ -s "$bus_addr_file" ]; then break; fi
      sleep 0.25
    done

    DBUS_SESSION_BUS_ADDRESS="$(head -1 "$bus_addr_file" 2>/dev/null)"
    if [ -n "$DBUS_SESSION_BUS_ADDRESS" ]; then
      export DBUS_SESSION_BUS_ADDRESS
    else
      echo "::warning::could not start a private D-Bus session bus; WirePlumber 0.4.x will not stay up"
      kill "$PWNET_DBUS_PID" 2>/dev/null || true
      unset PWNET_DBUS_PID
    fi
  fi

  pipewire    > "$PWNET_SESSION_LOG_DIR/pipewire.log"    2>&1 &
  PWNET_PW_PID=$!
  wireplumber > "$PWNET_SESSION_LOG_DIR/wireplumber.log" 2>&1 &
  PWNET_WP_PID=$!

  local i
  for ((i = 1; i <= attempts; i++)); do
    if pw-cli info 0 >/dev/null 2>&1; then
      echo "session up after $i attempt(s): $(pipewire --version 2>&1 | head -1)"
      break
    fi

    # A daemon that died is not going to answer, so stop waiting for it.
    if ! kill -0 "$PWNET_PW_PID" 2>/dev/null; then
      echo "::error::pipewire exited while starting up"
      pwnet_session_dump
      pwnet_session_stop
      return 1
    fi

    sleep 0.25
  done

  if ! pw-cli info 0 >/dev/null 2>&1; then
    echo "::error::pipewire did not answer pw-cli within $attempts attempts"
    pwnet_session_dump
    pwnet_session_stop
    return 1
  fi

  # The suite quarantines a few tests on daemons with known bugs (CrashesOldDaemons runs
  # only where the daemon survives it). The version comes from here because the registry
  # never sees it: the daemon reports it in core info, not in global props.
  PWNET_DAEMON_VERSION="$(pipewire --version 2>&1 | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' | head -1)"
  export PWNET_DAEMON_VERSION

  # pipewire answering is only half the session: the tests need WirePlumber managing it
  # (linking streams, default metadata), and a WirePlumber that died on startup looks
  # exactly like a healthy one to pw-cli. Wait for its client global instead.
  for ((i = 1; i <= 80; i++)); do
    if pw-dump 2>/dev/null | grep -q '"application.name": "WirePlumber"'; then
      echo "wireplumber up after $i attempt(s)"
      return 0
    fi

    if ! kill -0 "$PWNET_WP_PID" 2>/dev/null; then
      echo "::error::wireplumber exited while starting up"
      pwnet_session_dump
      pwnet_session_stop
      return 1
    fi

    sleep 0.25
  done

  echo "::error::wireplumber never appeared in pw-dump within 80 attempts"
  pwnet_session_dump
  pwnet_session_stop
  return 1
}

pwnet_session_dump() {
  echo "::group::pipewire.log";    tail -200 "$PWNET_SESSION_LOG_DIR/pipewire.log"    2>/dev/null || true; echo "::endgroup::"
  echo "::group::wireplumber.log"; tail -200 "$PWNET_SESSION_LOG_DIR/wireplumber.log" 2>/dev/null || true; echo "::endgroup::"
}

pwnet_session_stop() {
  kill "$PWNET_WP_PID" "$PWNET_PW_PID" "$PWNET_DBUS_PID" 2>/dev/null || true
  # A second session in the same shell must bring its own bus: the socket this one used
  # is gone with it. Only ours, never a pre-existing address we did not set.
  if [ -n "${PWNET_DBUS_PID:-}" ]; then
    unset DBUS_SESSION_BUS_ADDRESS
  fi
  unset PWNET_WP_PID PWNET_PW_PID PWNET_DBUS_PID
}

# Runs one test leg inside an already-started session, stops the session afterwards, and
# leaves the exit code in PWNET_LAST_RC. Always returns success itself, so callers under
# `set -e` need no set +e dance: `pwnet_session_run dotnet test ...` then read the variable.
pwnet_session_run() {
  set +e
  "$@"
  PWNET_LAST_RC=$?
  set -e
  pwnet_session_stop
}
