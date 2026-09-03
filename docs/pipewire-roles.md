# The two roles, and which one this library plays

Written after reading PipeWire 1.6.8 and WirePlumber 0.5.16 rather than inferring from the headers.
Everything here is checked against source or a running daemon; where it is not, it says so.

## Why exporters exist at all

A PipeWire client can be on either side of an object.

**Consuming** is the side most tools are on. The daemon owns an object, publishes it as a global,
and a client binds a proxy to it: reads its info and params, calls its methods, watches it change.
`pw-dump`, `pw-link` and `wpctl` do only this. Nothing the client does can make the daemon wait.

**Exporting** is the other side, and it is not "creating an object". It is volunteering to *be* the
object. The client implements an interface locally, hands it to `pw_core_export`, and the daemon
publishes a global on its behalf. From then on every other client's requests against that global
arrive at the exporting client, which must answer them.

The consequence that explains a lot of confusing behaviour: **an object created through a factory
but never served leaves the daemon waiting on a server that does not exist.** Creating a metadata
object with `pw_core_create_object` and simply holding the proxy wedges `pw-cli info 0` for every
process on the machine. That is not a bug in the daemon; it is the client having taken a role and
not performed it. Exporting is how the role is taken correctly, because it wires the object to an
implementation in the same call.

## What export actually does

`pw_core_export(core, type, props, object, user_data_size)` looks up an export type registered in
the context and calls its function. The types are registered by modules, so a context that has not
loaded the right module cannot export that interface.

For metadata, `pw_core_metadata_export` in `module-metadata/proxy-metadata.c`:

1. `pw_core_create_object(core, "metadata", ...)` asks the daemon's factory for the object and gets
   a proxy.
2. `pw_proxy_install_marshal(proxy, true)` installs the **server** side marshal on it. This is the
   line that makes the client the implementation rather than a user.
3. `pw_proxy_add_object_listener(proxy, ..., miface->cb.funcs, ...)` routes requests arriving from
   the daemon into the local implementation.
4. `pw_metadata_add_listener(meta, ..., iface->cb.funcs, ...)` routes changes made locally out to
   the daemon, and through it to everyone bound.

Both directions, one call. Note step 3 and 4 both read a callback table straight out of the object
pointer by casting it to `spa_interface`, which is why the pointer has to be the implementation
(`pw_metadata`) and not the thing that owns it (`pw_impl_metadata`). Passing the wrong one reads
whatever sits at that offset and takes the process down.

`pw_impl_metadata_get_implementation()` is the accessor between the two.

## Register is not a step before export

`pw_impl_metadata_register` publishes a global in the client's **own context**. The store is real
and works, but only this process can see it. `pw_core_export` publishes one on the **daemon**.

They are alternatives. Doing both to one implementation crashes, in either order.

## The export types that exist

Eight, from four modules. Checked with `grep pw_context_register_export_type`.

| Type | Module | What it lets a client be |
|---|---|---|
| `SPA_TYPE_INTERFACE_Node` | client-node | A raw SPA node in the graph. This is what `pw_stream` exports. |
| `PW_TYPE_INTERFACE_Node` | client-node | A `pw_impl_node`. This is what `pw_filter` exports. |
| `SPA_TYPE_INTERFACE_Device` | client-device | A device with profiles and routes, provided out of process. |
| `PW_TYPE_INTERFACE_Metadata` | metadata | A metadata store other clients read and write. |
| `Endpoint`, `EndpointStream`, `EndpointLink`, `Session` | session-manager | Nothing in practice: see below. |

The session-manager interfaces are dead. WirePlumber 0.5.16 does not reference a single one of them
(checked by scanning its shared objects for the type strings). They are not worth implementing.

## Every media client is already an exporter

This is the part that reframes the library. `pw_stream` builds an `spa_node` interface and exports
it as `SPA_TYPE_INTERFACE_Node`; `pw_filter` builds a `pw_impl_node` and exports it as
`PW_TYPE_INTERFACE_Node` at `filter.c:1692`. There is no separate "streaming API" underneath: a
stream *is* an exported node, and the daemon schedules it in the graph like any other.

So a library that binds `pw_stream` and `pw_filter` is already doing export, through a wrapper that
hides it. The wrapper is worth having: it owns the realtime `process` callback, the buffer pool and
the format negotiation, none of which a caller should hand-roll.

## Where the tools sit

Measured by scanning the binaries for `pw_core_export`, `pw_stream_new` and `pw_registry_bind`.

| Tool | Role |
|---|---|
| `pw-dump`, `pw-link`, `wpctl` | Consume only. |
| `pw-cat` | Exports, through `pw_stream`. |
| `pw-cli` | Exports, and can create objects directly. |
| `pw-loopback` | Loads a module into its own context; the module makes the nodes. |
| WirePlumber | Consumes everything, exports metadata, and hosts nodes and devices. |

WirePlumber is the reference for the serving side, and it uses exactly the sequence above:
`pw_impl_metadata_get_implementation`, `pw_impl_metadata_add_listener`, `pw_core_export`.

## The threading lesson WirePlumber learned

`wp_core_get_export_core` is **deprecated** in 0.5. Its NEWS says the export core was retired in
favour of a separate client context on its own thread hosting the in-process media objects,
"so that slow Lua event hooks on the main thread no longer stall their control path".

The point is not re-entrancy. It is that **a served object owes the daemon prompt answers**, and if
it shares a loop with whatever else the application is doing, the application's slow work becomes
the served object's latency. A process that both consumes heavily and serves should serve on its own
context.

This library does not enforce that, and should not: a caller serving one small metadata store from
the same context is fine. It is a documented consequence, not a rule.

## Where PipeWire.NET stands against this

**Consuming: complete.** Ten of the thirteen interfaces are bound with a control type. The three
that are not are `Core` and `Registry`, which are the connection itself, and `ClientNode`, which is
the transport underneath node export rather than something a caller binds.

**Exporting: nodes and metadata, not devices.**

| Role | Status |
|---|---|
| Export a node as a media source or sink | Through `PipeWireAudioCapture`, `PipeWireVideoCapture`, `PipeWireAudioOutput`, `PipeWireVideoOutput`. |
| Export a multi-port processing node | Through `PipeWireFilter`. |
| Export a metadata store | `PipeWireMetadataProvider`, exported by default. |
| Export a device with profiles and routes | Not implemented. |
| Export Endpoint or Session | Deliberately not: nothing uses them. |

The one real gap is device export. It matters for a specific shape of application: one that provides
hardware to the graph from outside the daemon, the way a Bluetooth or ALSA monitor does. A .NET
application wanting to publish a virtual sound card with selectable profiles and routes cannot do it
today; one wanting to publish audio or video can, through the stream types.

## What "full-fledged" would mean from here

In order of what a real application would miss first:

1. **Device export** (`SPA_TYPE_INTERFACE_Device`), so a .NET process can provide a device with
   profiles and routes rather than only nodes. The wiring is identical to metadata, read from
   `module-client-device/protocol-native.c`: create through the `client-device` factory, route the
   daemon's requests into the implementation with `pw_proxy_add_object_listener`, and route the
   implementation's events out with `spa_device_add_listener`.

   What makes it bigger than metadata is the interface behind it. The object is a raw `spa_device`,
   so a caller has to answer four methods (`add_listener`, `sync`, `enum_params`, `set_param`) and
   emit four events (`info`, `result`, `event`, `object_info`). `enum_params` and `set_param` carry
   SPA pods, which the existing builder and parser already cover.

   `object_info` is the part with no equivalent in the metadata work: it is how a device announces
   the child objects it provides, which is how a sound card publishes its PCM nodes. A device that
   announces none is legal and selectable in a mixer but carries no audio, so a useful
   implementation needs the child nodes too, and each of those is a node export of its own.

   That is the honest size of it: not a wrapper over one call like the metadata provider, but a
   second serving surface with its own vtable and a dependency on node serving underneath.
2. **A served node that is not a stream**, if a caller ever needs port shapes `pw_filter` cannot
   express. Worth deferring until something actually needs it, because `pw_filter` covers the cases
   that exist.
3. Nothing else. The consuming side is complete, and the interfaces not covered are either the
   connection itself or dead.
