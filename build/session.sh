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

  pipewire    > "$PWNET_SESSION_LOG_DIR/pipewire.log"    2>&1 &
  PWNET_PW_PID=$!
  wireplumber > "$PWNET_SESSION_LOG_DIR/wireplumber.log" 2>&1 &
  PWNET_WP_PID=$!

  local i
  for ((i = 1; i <= attempts; i++)); do
    if pw-cli info 0 >/dev/null 2>&1; then
      echo "session up after $i attempt(s): $(pipewire --version 2>&1 | head -1)"
      return 0
    fi

    # A daemon that died is not going to answer, so stop waiting for it.
    if ! kill -0 "$PWNET_PW_PID" 2>/dev/null; then
      echo "::error::pipewire exited while starting up"
      pwnet_session_dump
      return 1
    fi

    sleep 0.25
  done

  echo "::error::pipewire did not answer pw-cli within $attempts attempts"
  pwnet_session_dump
  pwnet_session_stop
  return 1
}

pwnet_session_dump() {
  echo "::group::pipewire.log";    tail -200 "$PWNET_SESSION_LOG_DIR/pipewire.log"    2>/dev/null || true; echo "::endgroup::"
  echo "::group::wireplumber.log"; tail -200 "$PWNET_SESSION_LOG_DIR/wireplumber.log" 2>/dev/null || true; echo "::endgroup::"
}

pwnet_session_stop() {
  kill "$PWNET_WP_PID" "$PWNET_PW_PID" 2>/dev/null || true
  unset PWNET_WP_PID PWNET_PW_PID
}
