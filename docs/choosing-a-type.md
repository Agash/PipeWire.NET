# Which type do I want?

Every object in a PipeWire graph can be approached three ways, and this library has a type for each.
Picking the wrong one is the single most common way to get stuck, because the wrong one usually
compiles and then does nothing you wanted: `PipeWireMetadataProvider` *serves* a metadata store, so
reaching for it to read the session manager's defaults gets you an empty store of your own rather
than the daemon's.

The rule is the suffix, and it is the same for every kind:

| suffix | you are | upstream |
|---|---|---|
| none (`PipeWireNode`) | reading the registry's record of somebody's object | `pw_node_info`, `pw_link_info`, ... |
| `Proxy` (`PipeWireNodeProxy`) | a client of somebody else's object | `pw_proxy` and the per-interface proxies (`pw_node`, `pw_metadata`) |
| `Provider` (`PipeWireNodeProvider`) | the object; the daemon publishes it for you and forwards every request to your process | `pw_core_export`, `pw_impl_node` |
| `Builder` (`PipeWireNodeBuilder`) | describing an object for the daemon to create, not yet created | factory arguments |

`docs/pipewire-roles.md` is the long version of why the third row is different in kind from the
other two, and what it costs to take that role and not perform it.

## Consume, or serve

| I want to ... | type | how you get one |
|---|---|---|
| list what exists | `PipeWireGraphSnapshot` | `registry.Current`, or `registry.WatchAsync` per change |
| read one object's fields | `PipeWireNode`, `PipeWirePort`, `PipeWireLink`, `PipeWireDevice`, `PipeWireClient`, `PipeWireModule`, `PipeWireFactory`, `PipeWireMetadata`, `PipeWireCore`, `PipeWireProfiler`, `PipeWireSecurityContext` | off the snapshot |
| set a node's volume or mute | `PipeWireNodeProxy` | `registry.BindNode(id)` |
| switch a card profile or route | `PipeWireDeviceProxy` | `registry.BindDevice(id)` |
| read or write the session defaults | `PipeWireMetadataProxy` | `registry.BindMetadata("default")` |
| change another client's permissions | `PipeWireClientProxy` | `registry.BindClient(id)` |
| watch a link's state | `PipeWireLinkProxy` | `registry.BindLink(id)` |
| read a port's params | `PipeWirePortProxy` | `registry.BindPort(id)` |
| read the daemon's profiler output | `PipeWireProfilerProxy` | `registry.BindProfiler()` |
| create a sink other apps can play into | `PipeWireNodeBuilder` | `registry.CreateVirtualSink(description)` |
| create a source other apps can record from | `PipeWireNodeBuilder` | `registry.CreateVirtualSource(description)` |
| link two ports | `PipeWireLinkBuilder` | `registry.CreateLink(outputPortId, inputPortId)` |
| **be** a node: answer formats, buffers and cycles myself | `PipeWireNodeProvider` | `PipeWireNodeProvider.Create(ctx, name, format)` |
| publish a node that lives in a SPA plugin | `PipeWireNodeProvider` | `PipeWireNodeProvider.FromSpaFactory(ctx, factory)` |
| **be** a device: serve profiles and routes | `PipeWireDeviceProvider` | `PipeWireDeviceProvider.Create(ctx, name, description)` |
| publish a device a SPA monitor plugin enumerates | `PipeWireDeviceProvider` | `PipeWireDeviceProvider.FromSpaFactory(ctx, factory)` |
| **be** a metadata store other clients read and write | `PipeWireMetadataProvider` | `PipeWireMetadataProvider.Create(ctx, name)` |
| run DSP inside the graph | `PipeWireFilter` | `PipeWireFilter.Create(ctx, name)` |
| send or receive media with the library's own policy | `PipeWireAudioCapture` / `PipeWireAudioOutput` / `PipeWireVideoCapture` / `PipeWireVideoOutput` | `new`, then `Connect` |

## Streams, filters, and being the node

Three layers can all put media in the graph, and they differ by how much of the node is yours:

| | upstream | who answers the graph | when to use it |
|---|---|---|---|
| `PipeWireAudioCapture` and the other three | `pw_stream` | libpipewire, with its policy for formats, buffers and scheduling | almost always |
| `PipeWireFilter` | `pw_filter` | libpipewire, but you get per-port buffers on the realtime thread | in-graph DSP with several ports |
| `PipeWireNodeProvider` | `pw_core_export` of a `spa_node` you implement | your process, on the realtime thread | a virtual device, or anything `pw_stream`'s policy will not do. Upstream's `export-source` / `export-sink` examples are the same shape |

A stream is not a lesser thing than an exported node: `pw_stream` *is* an exported node, with
libpipewire implementing `spa_node` on your behalf (upstream `stream.c`). Dropping to
`PipeWireNodeProvider` buys control and costs you the buffer-negotiation state machine, the
realtime constraints, and the fifteen-method vtable.

## Capture and Output, against upstream's words

The stream names mix two of upstream's vocabularies, deliberately:

| ours | `media.category` | direction |
|---|---|---|
| `PipeWireAudioCapture`, `PipeWireVideoCapture` | `Capture` | `SPA_DIRECTION_INPUT` |
| `PipeWireAudioOutput`, `PipeWireVideoOutput` | `Playback` | `SPA_DIRECTION_OUTPUT` |

Upstream's own examples write `media.category=Capture` on consumers and `Playback` on producers,
and this library writes exactly that. The types are not called `*Playback` because the common
producer here is a screen capture or a transport feeding frames *into* the graph, which "playback"
misdescribes.

## Names that look alike and are not

- `PipeWireStreamControl` is a knob on a stream (name, value, range), which is upstream's
  `pw_stream_control`. It is not a proxy; the proxies all end in `Proxy`.
- `SpaProp` is the generated enum of `SPA_PROP_*` ids. `SpaPodProperty` is one property inside a POD
  object. They are two letters apart and unrelated.
- `PipeWireMetadata` is the registry's record of a metadata store. `PipeWireMetadataProxy` binds one
  somebody else serves. `PipeWireMetadataProvider` serves one of your own.
- `PipeWireExportedFormat` is the format an exported node offers, not an object kind.

## One thing you cannot do

You cannot bind a proxy to a store this same connection serves. The daemon will not answer, because
the answer would have to come from the process that is waiting for it, and the wait is what stops it
answering. `registry.BindMetadata` refuses this outright rather than hanging; read your own store
through the provider you already hold.
