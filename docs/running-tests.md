# Running the tests

## Against your own session

`dotnet test` works against a desktop session. Know what that costs: the daemon-facing tests create
nodes, write metadata, and in two cases change a sound card's profile and a route's mixer volume.
Each restores what it changed, but an interrupted run may not get that far.

## Against a private session

Preferred. Removes the risk and makes runs reproducible, because WirePlumber's saved defaults,
routes and profiles no longer carry over between runs.

```bash
export XDG_RUNTIME_DIR="$(mktemp -d)"
export XDG_CONFIG_HOME="$(mktemp -d)"
export XDG_STATE_HOME="$(mktemp -d)"
export XDG_DATA_HOME="$(mktemp -d)"

pipewire &      PW=$!
wireplumber &   WP=$!
until pw-cli info 0 >/dev/null 2>&1; do sleep 0.1; done

dotnet test PipeWire.NET.slnx

kill $WP $PW
```

`XDG_RUNTIME_DIR` alone is not enough. It isolates the socket, but WirePlumber keeps state under
`XDG_STATE_HOME`, so a test that sets a default sink still writes into the desktop's saved state and
the next run starts from whatever the last one left.

## Categories

| Filter | Needs |
|---|---|
| `TestCategory!=Integration` | nothing |
| `TestCategory=Integration&TestCategory!=RequiresDaemon` | the runtime, no daemon |
| `TestCategory=RequiresDaemon` | a running PipeWire session |
| `TestCategory=RequiresGStreamer` | `gst-launch-1.0` with `pipewiresink` |
| `TestCategory=RequiresGpu` | a GPU that can import DMA-BUF |
| `TestCategory=PenTest` | a session of its own; `PWNET_PEN_SECONDS` to soak |
| `TestCategory=KillsTheDaemon` | a session you are willing to lose |
| `TestCategory=RequiresPatchedDaemon` | a daemon carrying `repro/module-metadata.patch` |

Stateful modules run with `--max-parallel-test-modules 1`. They share one graph, so running them
concurrently makes them fail on each other's changes instead of on defects.

Three categories are excluded from every ordinary leg and have to be asked for by name.

**`RequiresPatchedDaemon`** pins an upstream bug that is still open: the test fails on a stock 1.6.8
daemon because that is what it is about. `build/verify-linux.sh` runs it against daemons loading the
patches in `repro/`; CI, whose sessions are stock, excludes it.

**`PenTest`** churns one session hard for seconds at a time - twelve contexts opening at once,
metadata written in a loop - so anything sharing that session fails on this traffic rather than on
anything of its own. Give it a session to itself.

**`KillsTheDaemon`** is the permission tests, and on a stock PipeWire 1.6.8 they can do what the
name says: withdrawing a client's read access makes `pw_global_update_permissions` destroy its
resources while walking the global's resource list, and `pw_impl_client_update_permissions` calls it
while walking the context's global list. A destroy runs hooks that can take other resources with it,
and the global itself when it is an object the client exported, so the walks continue into destroyed
or freed memory: `assert(!resource->destroyed)`, or a segfault. Everything after it in the same
session fails to connect. `repro/permissions-crash.sh` reproduces it (confining a session manager's
client). `repro/libpipewire-permissions.patch` fixes it; `build/verify-linux.sh` runs this
category against a private daemon of its own that loads the patched library, and checks that it did.

## External tools

Tests that cross-check against PipeWire's own tools resolve them through `PATH`. Override with
`PWNET_TEST_<TOOL>`, dashes as underscores, uppercased:

```bash
export PWNET_TEST_PW_DUMP=/opt/pipewire/bin/pw-dump
export PWNET_TEST_WPCTL=/opt/wireplumber/bin/wpctl
```

A missing tool skips its tests rather than failing them.

## Diagnosing a failure

The daemon's state at the time of failure is usually what you need:

```bash
pw-dump | head -400
wpctl status
pw-link -l -I
pw-metadata -n default
```

CI runs these on any integration failure.
