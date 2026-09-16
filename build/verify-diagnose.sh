#!/usr/bin/env bash
#
# Evidence collection for build/verify-linux.sh: what a hung or crashed test host, or a dead daemon,
# was doing - read from dumps, never from a live attach. The lab box runs with
# kernel.yama.ptrace_scope=1, so gdb, eu-stack and friends cannot attach to a running process; a core
# file needs no ptrace, and gdb reads createdump's and systemd-coredump's cores alike. Native frames
# are named through debuginfod (Arch and CachyOS both serve libpipewire and libspa).
#
# Sourced, not run. Each function writes its findings next to the dump and prints a short summary.

export DEBUGINFOD_URLS="${DEBUGINFOD_URLS:-https://debuginfod.archlinux.org https://debuginfod.cachyos.org}"

# gdb over a core: every thread's native stack, and for every thread blocked on a pthread mutex, the
# thread that owns it - the answer a deadlock needs and the one a stack alone does not give.
pwnet_native_stacks() {
  local exe="$1" core="$2" out="$3"
  timeout 600 gdb -batch -q \
    -iex "set debuginfod enabled on" \
    -ex "set pagination off" \
    -ex "set print frame-arguments scalars" \
    -ex "thread apply all bt 25" \
    -ex "python
import gdb
print('=== mutex owners ===')
for inf in gdb.inferiors():
    for th in inf.threads():
        th.switch()
        f = gdb.newest_frame()
        depth = 0
        while f is not None and depth < 6:
            name = f.name() or ''
            if 'pthread_mutex_lock' in name or 'lll_lock_wait' in name or 'mutex_lock' in name:
                try:
                    f.select()
                    m = int(gdb.parse_and_eval('\$rdi'))
                    words = gdb.selected_inferior().read_memory(m, 12).tobytes()
                    lock = int.from_bytes(words[0:4], 'little')
                    count = int.from_bytes(words[4:8], 'little')
                    owner = int.from_bytes(words[8:12], 'little')
                    print('LWP %d blocked on mutex 0x%x: lock=%d count=%d owner=LWP %d' % (th.ptid[1], m, lock, count, owner))
                except Exception as e:
                    print('LWP %d blocked on a mutex; owner unreadable (%s)' % (th.ptid[1], e))
                break
            f = f.older()
            depth += 1
" \
    "$exe" "$core" > "$out" 2>&1
  grep -A40 '^=== mutex owners ===' "$out" | sed 's/^/    /'
}

# The managed half, through SOS: every thread's managed stack, and the faulting one first.
pwnet_managed_stacks() {
  local dump="$1" out="$2"
  timeout 600 dotnet-dump analyze "$dump" -c "clrthreads" -c "pe -lines" -c "clrstack -all" -c "exit" > "$out" 2>&1
  grep -aE "Exception type|Message:" "$out" | head -4 | sed 's/^/    /'
}

# The test that was running: the trace records each test's start and outcome, so the last start
# without an outcome is the one that hung or took the host down.
pwnet_running_test() {
  local trace="$1"
  [ -f "$trace" ] || { echo "(no test trace)"; return; }
  # Lines are "<time> <elapsed>ms <OUTCOME> <test name>"; the name can contain spaces (data rows).
  awk '{name=""; for (i=4;i<=NF;i++) name=name (i>4?" ":"") $i}
       $3=="START"{s[name]=1; next} {delete s[name]}
       END{for (t in s) print t}' "$trace"
}

# A hung test host: dump it, read both halves of every stack, then kill it so the run goes on.
pwnet_diagnose_hang() {
  local pid="$1" label="$2" trace="$3" dir="$4"
  local base="$dir/hang-$label-$pid"
  echo "::error::the $label test host stopped making progress; collecting evidence"
  echo "  running test(s): $(pwnet_running_test "$trace" | tr '\n' ' ')"
  local exe
  exe="$(readlink -f "/proc/$pid/exe")"
  timeout 300 dotnet-dump collect -p "$pid" -o "$base.dmp" --type Full > "$base.collect.txt" 2>&1
  if [ -s "$base.dmp" ]; then
    pwnet_managed_stacks "$base.dmp" "$base.managed.txt"
    pwnet_native_stacks "$exe" "$base.dmp" "$base.native.txt"
    echo "  evidence: $base.{managed,native}.txt"
  else
    echo "  dotnet-dump could not collect a dump: $(tail -1 "$base.collect.txt")"
  fi
  kill -9 "$pid" 2>/dev/null
}

# Crash dumps createdump wrote during a leg: the same two reads, per dump.
pwnet_diagnose_crash_dumps() {
  local dir="$1" exe="$2" trace="$3"
  local dump
  for dump in "$dir"/pwnet.*.dmp; do
    [ -f "$dump" ] || continue
    [ -f "$dump.native.txt" ] && continue
    echo "  crash dump $dump; running test(s): $(pwnet_running_test "$trace" | tr '\n' ' ')"
    pwnet_managed_stacks "$dump" "$dump.managed.txt"
    pwnet_native_stacks "$exe" "$dump" "$dump.native.txt" > /dev/null
    grep -m1 -B2 -A12 "signal\|SIGSEGV\|SIGABRT" "$dump.native.txt" | sed 's/^/    /' | head -16
  done
}

# A daemon that died: systemd-coredump keeps its core; read it with symbols.
pwnet_diagnose_daemon() {
  local pid="$1" label="$2" dir="$3"
  local core="$dir/daemon-$label-$pid.core"
  if coredumpctl dump "$pid" -o "$core" > /dev/null 2>&1; then
    pwnet_native_stacks /usr/bin/pipewire "$core" "$core.native.txt" > /dev/null
    echo "  daemon core: $core.native.txt"
    grep -m1 -A14 "^Thread 1 " "$core.native.txt" | sed 's/^/    /'
  else
    echo "  no core for daemon pid $pid (coredumpctl has none)"
  fi
}

# Whether a session still answers a client. wpctl status needs a round trip to the daemon and to
# WirePlumber's objects, and returns in well under a second on a healthy session. (pw-cli info 0 is
# no probe: it can sit waiting on its own console with the session perfectly healthy.)
pwnet_session_answers() {
  local runtime="$1"
  XDG_RUNTIME_DIR="$runtime" timeout 5 wpctl status > /dev/null 2>&1
}

# The upstream module-metadata wedge's signature: the daemon has stopped reading the sockets of the
# clients it marked busy, so their epoll mask is 0x18 (EPOLLERR|EPOLLHUP) instead of 0x19.
pwnet_wedge_signature() {
  local runtime="$1" pid="" p
  # The daemon serving that runtime directory, not just any pipewire of this user.
  for p in $(pgrep -u "$(id -u)" -x pipewire); do
    if tr '\0' '\n' < "/proc/$p/environ" 2>/dev/null | grep -qx "XDG_RUNTIME_DIR=$runtime"; then pid="$p"; break; fi
  done
  [ -n "$pid" ] || { echo "  no daemon found for $runtime"; return; }
  echo "  daemon $pid epoll masks: $(grep -h '^tfd:' /proc/"$pid"/fdinfo/* 2>/dev/null | awk '{print $4}' | sort | uniq -c | tr '\n' ' ')"
  echo "  (sockets at 18 are clients the daemon no longer reads: the module-metadata wedge)"
}

# Every headline error in a daemon log, with the test that was running when it was logged. The pod
# dumps upstream prints under a failed negotiation are dropped (their text is indented: two or more
# spaces after the prefix), so what is left is one line per actual error. Reads both the private
# daemon's own log ("[E][HH:MM:SS.us] topic | [file:line func()] msg") and the journal in
# short-precise form ("Mon DD HH:MM:SS.us host pipewire[pid]: topic: msg"). Errors matching an
# entry of $PWNET_EXPECTED_DAEMON_ERRORS (space separated; "Test" for anything logged during that
# test, "Test=regex" or "*=regex" for matching lines only, the regex run against "topic: msg" with
# digits folded to N) are marked expected. Prints the count of unexpected errors as its last line.
# Both logs carry a time of day only, so each is read in order and a time that goes backwards by
# more than twelve hours starts a new day; comparing the bare times put everything logged before
# midnight on the last test to start after it.
pwnet_attribute_errors() {
  local log="$1" trace="$2"
  # The list is read from ENVIRON, not passed with -v, which would run the regexes through awk's
  # escape processing.
  awk -v trace="$trace" '
    BEGIN {
      expected = ENVIRON["PWNET_EXPECTED_DAEMON_ERRORS"]
      while ((getline l < trace) > 0) {
        split(l, f, " ")
        if (f[3] == "START") { n++; t[n] = abs_time(f[1], "trace"); name[n] = f[4] }
      }
      ne = split(expected, entries, " ")
    }
    function abs_time(hms, which,    p, s) {
      split(hms, p, ":")
      s = p[1] * 3600 + p[2] * 60 + p[3]
      if (which in last && s < last[which] - 43200) day[which]++
      last[which] = s
      return day[which] * 86400 + s
    }
    function is_expected(who, line,    i, e, p) {
      for (i = 1; i <= ne; i++) {
        e = entries[i]; p = index(e, "=")
        if (p == 0) { if (e == who) return 1; continue }
        if ((substr(e, 1, p - 1) == who || substr(e, 1, p - 1) == "*") && line ~ substr(e, p + 1)) return 1
      }
      return 0
    }
    {
      ts = ""; msg = ""
      if (match($0, /^\[E\]\[([0-9:.]+)\]/, m)) {
        ts = m[1]
        if ($0 ~ /\)\] {2,}/) next
        msg = $0; sub(/^[^|]*\| \[[^]]*\] /, "", msg)
        topic = $0; sub(/^\[E\]\[[^]]*\] /, "", topic); sub(/ .*/, "", topic)
      } else if (match($0, / ([0-9][0-9]:[0-9][0-9]:[0-9][0-9]\.[0-9]+) /, m)) {
        ts = m[1]
        if ($0 ~ /\]: [a-z0-9.]+: {2,}/) next
        msg = $0; sub(/^.*\]: /, "", msg)
        topic = msg; sub(/:.*/, "", topic); sub(/^[^:]*: /, "", msg)
      } else next
      if (msg ~ /^Object: size/) next
      ts = abs_time(ts, "log")
      who = "(before any test)"
      for (i = n; i >= 1; i--) if (t[i] <= ts) { who = name[i]; break }
      gsub(/[0-9]+/, "N", msg)
      mark = is_expected(who, topic ": " msg) ? "expected  " : "UNEXPECTED"
      if (mark == "UNEXPECTED") bad++
      print mark "  " who "  " topic ": " msg
    }
    END { print bad + 0 }
  ' "$log" | { lines="$(cat)"; printf '%s\n' "$lines" | sed '$d' | sort | uniq -c | sort -rn | sed 's/^/  /'; printf '%s\n' "$lines" | tail -1; }
}

# A patched libpipewire is used only when it is the installed daemon's version.
pwnet_patched_lib_usable() {
  local dir="$1" installed built
  [ -f "$dir/libpipewire-0.3.so.0" ] || return 1
  installed="$(pipewire --version 2>/dev/null | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' | tail -1)"
  built="$(strings "$dir/libpipewire-0.3.so.0" | grep -m1 -xE '[0-9]+\.[0-9]+\.[0-9]+')"
  [ -n "$installed" ] && [ "$installed" = "$built" ]
}

# A patched module directory is used only when it was built from the installed daemon's version.
pwnet_patched_modules_usable() {
  local dir="$1" installed built
  [ -f "$dir/libpipewire-module-metadata.so" ] || return 1
  installed="$(pipewire --version 2>/dev/null | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' | tail -1)"
  built="$(strings "$dir/libpipewire-module-metadata.so" | grep -m1 -xE '[0-9]+\.[0-9]+\.[0-9]+')"
  [ -n "$installed" ] && [ "$installed" = "$built" ]
}
