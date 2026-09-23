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

Each leakable object is also recorded with the process behind it: the client's own
application.process.binary and .id (what a pulse client reports too, where pipewire.sec.pid would
only name pipewire-pulse). On a desktop session other programs come and go while the suite runs -
kded6 opening a pulse connection when a test switches a profile - so a new object counts as a leak
only when it is ours: owned by a process that has exited, or by a test host or a tool the tests
drive. The desktop's own are listed separately and do not fail the comparison.
"""
import json
import os
import subprocess
import sys

# Kinds whose count must come back down once every client has gone. Others - factories loaded on
# demand, the session manager's own metadata - legitimately settle at a new level.
LEAKABLE = ('Node', 'Port', 'Link', 'Client', 'Device', 'Metadata')

# Processes the suite runs, directly or through the tools it drives. An object owned by one of
# these that outlives the run is the suite's.
OURS = {'dotnet', 'PipeWire.NET.Tests', 'gst-launch-1.0', 'pw-cat', 'pw-play', 'pw-record',
        'pw-loopback', 'pw-metadata', 'pw-cli', 'pw-link', 'pw-dump', 'pw-top', 'pactl', 'wpctl'}

# Descriptors a daemon may reasonably hold more of after a busy run: epoll and timer churn, a
# lazily opened plugin. A leak per test is hundreds, not this. On a session with real cards it is
# larger: resuming a device and switching a profile open the ALSA descriptors and keep them.
FD_SLACK = int(os.environ.get('PWNET_FD_SLACK', '16'))


def snapshot(path, pids):
    proc = subprocess.Popen(['pw-dump'], stdout=subprocess.PIPE, text=True)
    dumper = proc.pid
    out_text, _ = proc.communicate(timeout=30)
    dump = json.loads(out_text)
    counts = {}
    clients = {}
    for o in dump:
        kind = o.get('type', '?').rsplit(':', 1)[-1]
        counts[kind] = counts.get(kind, 0) + 1
        if kind == 'Client':
            props = (o.get('info') or {}).get('props') or {}
            clients[o['id']] = (props.get('application.process.binary') or '?',
                                props.get('application.process.id') or props.get('pipewire.sec.pid'))

    objects = []
    for o in dump:
        kind = o.get('type', '?').rsplit(':', 1)[-1]
        if kind not in LEAKABLE:
            continue
        props = (o.get('info') or {}).get('props') or {}
        owner = o['id'] if kind == 'Client' else props.get('client.id')
        binary, pid = clients.get(int(owner), ('?', None)) if owner is not None else ('?', None)
        if binary == 'pw-dump' and pid is not None and int(pid) == dumper:
            continue  # this snapshot's own connection, present in every dump it takes
        name = props.get('node.name') or props.get('metadata.name') or props.get('application.name') or ''
        objects.append({'id': o['id'], 'kind': kind, 'binary': binary,
                        'pid': int(pid) if pid else None, 'name': name})

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
        json.dump({'counts': counts, 'procs': procs, 'objects': objects}, out, indent=1)


def compare(before_path, after_path):
    with open(before_path) as b, open(after_path) as a:
        before, after = json.load(b), json.load(a)

    leaks = []
    print('--- session objects (before -> after) ---')
    for kind in sorted(set(before['counts']) | set(after['counts'])):
        x, y = before['counts'].get(kind, 0), after['counts'].get(kind, 0)
        print(f'  {kind:16} {x:5} -> {y:5}')

    # What is new, by owner. Keyed on (kind, id): an id is reused only after its object is gone.
    seen = {(o['kind'], o['id']) for o in before.get('objects', [])}
    for o in after.get('objects', []):
        if (o['kind'], o['id']) in seen:
            continue
        # An object with no client of its own belongs to the daemon or the session manager - the
        # default metadata store, the ports a device grows when it resumes - and is never ours.
        # Ours is one of the suite's binaries, or an owner that has since exited.
        known = o['pid'] is not None and o['binary'] != '?'
        alive = known and os.path.exists(f"/proc/{o['pid']}")
        ours = known and (o['binary'] in OURS or not alive)
        what = f"{o['kind']} {o['id']} '{o['name']}' of {o['binary']} (pid {o['pid']}, {'running' if alive else 'gone'})"
        if ours:
            print(f'  new, ours: {what}  <-- LEAK')
            leaks.append(f"{o['kind']} {o['id']}")
        elif known:
            print(f"  new, another program's: {what}")
        else:
            print(f"  new, the session's own: {o['kind']} {o['id']} '{o['name']}'")

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
