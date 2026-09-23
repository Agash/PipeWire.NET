# Troubleshooting

Symptoms, in the order people hit them. Most PipeWire problems are silent rather than loud: nothing
throws, something just never arrives.

Before anything else, ask the daemon what it thinks:

```sh
pw-cli info 0        # is there a daemon at all
wpctl status         # devices, streams, defaults - the session manager's view
pw-link -l -I        # every port and every link, by id
pw-dump | head -100  # everything, as JSON
```

## Connecting

**`PipeWireConnectFailedException` from `StartAsync`.** No daemon reachable. Either none is running
(`pw-cli info 0` says so too), or your process cannot see its socket. The socket lives under
`XDG_RUNTIME_DIR`, so a service, a container or a cron job with a different environment will not find
it even though the desktop has one.

**A sandboxed app (Flatpak, Snap) that sees no socket.** That is the design. Ask the portal:
xdg-desktop-portal's ScreenCast `OpenPipeWireRemote` hands back a connected descriptor. Wrap it in a
`SafeFileHandle` and pass that to `StartAsync` - the library duplicates it close-on-exec, so yours
stays yours. The README's "Connecting over a portal fd" shows the wrap.

**`DllNotFoundException` for `libpipewire-0.3`.** The runtime library is missing. It ships with
PipeWire itself, not with the `-dev` package, so this usually means PipeWire is not installed at all.
This library asks for `libpipewire-0.3.so.0` first and falls back to the unversioned `libpipewire-0.3.so`
that the `-dev` package provides.

**The connection drops later: `PipeWireConnectionClosedException`.** The daemon went away, or it
stopped reading from your client. A daemon restart is the ordinary cause.

You do not have to wait for a call to throw to find out. `PipeWireContext.ConnectionLost` fires once
when it happens, and `ConnectionFault` holds the reason afterwards - worth wiring up in any process
that only consumes callbacks, since such a process may otherwise never make a call that fails.
Everything built on that context is dead once it fires; reconnecting means a new context.

## Nothing routes itself

**`registry.BindMetadata("default")` returns null.** There is no `default` metadata store, which
means no session manager is running. The daemon alone does not create one. Start WirePlumber, or do
the routing yourself with explicit links.

**A stream reaches `Streaming` and no frames arrive.** Almost always a routing question, not a bug.
In order:

1. `pw-link -l -I` - is your node linked to anything? If it has no links, the session manager
   decided not to route it.
2. Is your `media.class` what you meant? A capture with the class of a producer gets routed like a
   producer. [pipewire-concepts.md](pipewire-concepts.md) has the table.
3. Is the peer actually producing? A camera node exists whether or not anything is pushing frames
   through it.
4. If you targeted a node explicitly, does that id still exist? Ids are not reused while alive but
   are gone for good once the object is.

**A capture connects to the wrong device.** With no target, you get the session manager's default.
Pass a node id, or a `targetObjectName`, to pin it.

## Audio problems

**Crackling, dropouts, or `xrun` in the daemon's log.** Something is late in a graph cycle, and if
your process has a fill or process callback, assume it is you. Allocation, a lock, a log write, or
anything awaiting inside a realtime callback will do it.
[threading-and-lifetimes.md](threading-and-lifetimes.md) has the contract.

**A producer that publishes nothing, or a filter that processes nothing.** Check
`LastProcessError` on it (and `ProcessErrorCount`, which separates "threw once" from "throws every
cycle"). A callback that throws is caught rather than allowed to take the process down, so a handler
failing every cycle looks like one doing nothing until you read that. Going the other way, a
callback that cannot do what the graph asked should call `SetError` rather than return quietly,
which is what stops the peer waiting on a cycle that will never come.

**Frames arrive but are silent or black.** Check the negotiated format rather than the one you asked
for - the peer chooses from what you offered. Frames carry what was actually agreed, and a producer
that has not started yet legitimately sends zeroed buffers.

## Objects behaving oddly

**The node I created vanished when my program exited.** That is the default, and usually what you
want. `WithLinger` opts out; then it is yours to destroy with `registry.DestroyGlobalAsync`, and
`registry.LingeringIds` lists what you left behind.

**The node I created is still there after my program exited.** You used `WithLinger` (or a previous
run did). Destroy it by id, or restart the daemon.

**`InvalidOperationException` when binding my own metadata store.** Deliberate. A proxy to a store
this same connection serves would have to be answered by the process that is blocked waiting for the
answer, so it would hang for ever. Read your own store through the provider you hold.

**An object is missing from the registry entirely.** Permissions. A client only sees what the daemon
granted it, and objects you have no read permission for are absent rather than erroring when
touched. Ordinary desktop sessions grant everything; portals and security contexts do not.

**A renegotiation mid-stream wedges a GStreamer producer.** Known upstream bug, not yours:
`pipewiresink` deadlocks on a consumer-initiated renegotiation about a quarter of the time.
[streaming.md](streaming.md) has the detail and what stays safe on this side.

## Serving problems

**`pw_core_export` refused, or the device provider says it has no export type.** A module that
registers the type is not loaded. Devices need `libpipewire-module-client-device`, which
`client.conf` loads unless it has been turned off. Nodes and metadata are loaded by default.

**A device I served is selectable but carries no audio.** Expected: a device announces its child
nodes separately, and this library does not publish them for you. For audio, publish a virtual sink
instead. [serving.md](serving.md) explains the split.

**A served object answers slowly, or the graph complains about it.** It is sharing a context with
work that blocks the loop. Give a busy served object its own `PipeWireContext`.

**My exported node is in the graph but nothing comes out of it.** Two different faults look the same
from outside, and `PipeWireNodeProvider` tells them apart. `HasBeenScheduled` false means nothing is
driving the node: it is unlinked, or its peer is not running. `HasBeenScheduled` true with
`HasProcessed` false means cycles are arriving and your handler is returning zero. Check
`LastProcessError` as well - a handler that threw is recorded there rather than thrown, because an
exception must not reach the realtime caller.

**The proxy I am holding stopped working and raised nothing.** It probably did raise something.
`Removed` fires when the daemon destroys the object behind a bound proxy, and `IsRemoved` answers
after the fact. If you never subscribed, an object that went away is indistinguishable from one that
is merely quiet. See [threading-and-lifetimes.md](threading-and-lifetimes.md).

## Tests and CI

**Integration tests hang or interfere with your desktop.** Run them against a private session:
[running-tests.md](running-tests.md) has the recipe and the categories, including the three that must
be asked for by name because they need a session of their own.

## Reading an exception

Every failure the daemon reports carries the native call, the result code and the daemon's own
message:

```csharp
try
{
    await registry.CreateLink(outputPortId, inputPortId).ExecuteAsync(cancellationToken);
}
catch (PipeWireException ex)
{
    Console.WriteLine($"{ex.Operation} failed: {ex.Result} ({ex.DaemonMessage})");
}
```

`Result` is a negative errno. `-EPERM` is permissions, `-ENOENT` is an object that no longer exists,
`-EINVAL` is usually a parameter the daemon would not accept.
