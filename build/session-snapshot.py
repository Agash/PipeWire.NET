#!/usr/bin/env python3
"""Snapshot a PipeWire session, or compare two snapshots and report what a test run left behind.

    session-snapshot.py snap <out.json> [daemon-pid] [wireplumber-pid]
    session-snapshot.py compare <before.json> <after.json>

A snapshot is the session's objects counted by type (from pw-dump) plus the descriptor count and
RSS of the daemon and WirePlumber. Taken before any test and again after every test process has
exited, anything the tests created should be gone: a count that grew is a leak in this library or
in what it drove, and a daemon whose descriptors grew is one holding something for a client that
no longer exists. `compare` exits 1 when it finds either.

The pids are passed in, from the session that was started, rather than guessed: on a machine with
a desktop session there is a second pipewire and a second wireplumber, and the newest process of a
name is not necessarily this session's.
"""
import json
import os
import subprocess
import sys

# Kinds whose count must come back down once every client has gone. Others - factories loaded on
# demand, the session manager's own metadata - legitimately settle at a new level.
LEAKABLE = ('Node', 'Port', 'Link', 'Client', 'Device', 'Metadata')

# Descriptors a daemon may reasonably hold more of after a busy run: epoll and timer churn, a
# lazily opened plugin. A leak per test is hundreds, not this.
FD_SLACK = 16


def snapshot(path, pids):
    dump = json.loads(subprocess.check_output(['pw-dump'], text=True, timeout=30))
    counts = {}
    for o in dump:
        kind = o.get('type', '?').rsplit(':', 1)[-1]
        counts[kind] = counts.get(kind, 0) + 1

    procs = {}
    for name, pid in pids.items():
        try:
            fds = len(os.listdir(f'/proc/{pid}/fd'))
            with open(f'/proc/{pid}/status') as status:
                rss = next(int(line.split()[1]) for line in status if line.startswith('VmRSS'))
            procs[name] = {'pid': pid, 'fds': fds, 'rss_kb': rss}
        except OSError:
            procs[name] = None

    with open(path, 'w') as out:
        json.dump({'counts': counts, 'procs': procs}, out, indent=1)


def compare(before_path, after_path):
    with open(before_path) as b, open(after_path) as a:
        before, after = json.load(b), json.load(a)

    leaks = []
    print('--- session objects (before -> after) ---')
    for kind in sorted(set(before['counts']) | set(after['counts'])):
        x, y = before['counts'].get(kind, 0), after['counts'].get(kind, 0)
        mark = ''
        if y > x and kind in LEAKABLE:
            mark = '  <-- LEAK'
            leaks.append(f'{kind} +{y - x}')
        print(f'  {kind:16} {x:5} -> {y:5}{mark}')

    print('--- daemons (before -> after) ---')
    for name in sorted(set(before['procs']) | set(after['procs'])):
        pb, pa = before['procs'].get(name), after['procs'].get(name)
        if not pb or not pa:
            print(f'  {name}: gone by the second snapshot')
            leaks.append(f'{name} exited')
            continue
        delta = pa['fds'] - pb['fds']
        mark = '  <-- FD LEAK' if delta > FD_SLACK else ''
        if mark:
            leaks.append(f'{name} fds +{delta}')
        print(f"  {name:12} fds {pb['fds']} -> {pa['fds']}{mark}  rss {pb['rss_kb']} -> {pa['rss_kb']} kB")

    print('LEAKS: ' + (', '.join(leaks) if leaks else 'none'))
    return 1 if leaks else 0


if __name__ == '__main__':
    if len(sys.argv) >= 3 and sys.argv[1] == 'snap':
        named = {}
        if len(sys.argv) > 3:
            named['pipewire'] = int(sys.argv[3])
        if len(sys.argv) > 4:
            named['wireplumber'] = int(sys.argv[4])
        snapshot(sys.argv[2], named)
    elif len(sys.argv) == 4 and sys.argv[1] == 'compare':
        sys.exit(compare(sys.argv[2], sys.argv[3]))
    else:
        print(__doc__)
        sys.exit(2)
