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
| `TestCategory=PenTest` | a session; `PWNET_PEN_SECONDS` to soak |

Stateful modules run with `--max-parallel-test-modules 1`. They share one graph, so running them
concurrently makes them fail on each other's changes instead of on defects.

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
