# Serving: being an object in the graph

Reading the graph is the easy half. This page is the other one: publishing something other clients
see and use. It assumes [pipewire-concepts.md](pipewire-concepts.md);
[pipewire-roles.md](pipewire-roles.md) is the deep version of *why* this side is different in kind.

The short version: serving is not "creating an object". It is volunteering to **be** one. The daemon
publishes a global on your behalf, and from then on every other client's request against that global
arrives in your process and has to be answered. An object that is created but never served leaves
the daemon waiting on a server that does not exist.

## The four ways to put something in the graph

| You want | Reach for | You answer |
|---|---|---|
| To send or receive media | `PipeWireAudioCapture`, `PipeWireAudioOutput`, `PipeWireVideoCapture`, `PipeWireVideoOutput` | a fill or frame callback |
| Multi-port DSP inside the graph | `PipeWireFilter` | one process callback per cycle |
| A node whose behaviour `pw_stream` cannot express | `PipeWireNodeProvider` | the whole `spa_node` contract |
| A device with profiles and routes, or a metadata store | `PipeWireDeviceProvider`, `PipeWireMetadataProvider` | parameter and property requests |

The first row covers almost everything. A stream is not a lesser thing than an exported node -
`pw_stream` *is* an exported node, with libpipewire implementing the node contract for you.

## A sink other applications can play into

The simplest useful thing to publish, and it needs no callbacks at all: the daemon's
`support.null-audio-sink` factory provides the node, and you just ask for it.

Its ports arrive as separate globals shortly after the node, so wait for them before linking:

```csharp
PipeWireNode sink = await registry.CreateVirtualSinkAsync("My mix", cancellationToken: cancellationToken);

await foreach (PipeWireGraphSnapshot graph in registry.WatchAsync(cancellationToken))
{
    if (graph.GetPortsForNode(sink.NodeId).Length == 4) break;
}
```

`CreateVirtualSource` is the same factory with `media.class` set to `Audio/Source`, which is how a
virtual microphone is built. Both return a builder, so options chain before anything is created:

```csharp
PipeWireNode node = await registry.CreateVirtualSink("Monitor mix")
                                  .WithName("monitor_mix")
                                  .WithLinger()
                                  .ExecuteAsync(cancellationToken);
```

`WithLinger` keeps the node alive after your process disconnects. That is the right choice for a
routing setup meant to outlive the app and the wrong one for anything else: a lingering object
cannot be removed by disconnecting, only by destroying it explicitly. `registry.LingeringIds` lists
what you left behind.

## Producing media

`PipeWireVideoOutput` and `PipeWireAudioOutput` publish a node and ask you to fill each buffer. The
callback runs on the realtime thread, so it may not allocate, lock or block.

```csharp
await using var screen = new PipeWireVideoOutput(ctx, "sample_screen", 1280, 720);
screen.FillFrame += (_, pixels, stride, width, height, format) =>
{
    Render(pixels, stride, width, height);
    return true;          // false publishes nothing this cycle
};

screen.Connect();
```

The span is the daemon's buffer, so writing it is the only copy. For GPU-resident sources there is
`ConnectDmaBuf`, covered in [streaming.md](streaming.md).

On Linux this is how you feed OBS: publish the node, then add a PipeWire video source in OBS. It is
the counterpart to Spout on Windows and Syphon on macOS.

## DSP inside the graph

`PipeWireFilter` is a node with the ports you declare, scheduled in the graph like any other. A port
carries one DSP stream: `AddAudioPort` is mono 32-bit float, and there are `AddVideoPort` (RGBA
32-bit float, read with `GetPixels`), `AddControlPort`, `AddMidiPort` and `AddUmpPort`. Upstream's
[filter API](https://docs.pipewire.org/group__pw__filter.html) is the same object.

```csharp
await using PipeWireFilter filter = PipeWireFilter.Create(ctx, "sample_gain");
PipeWireFilterPort input = filter.AddAudioPort(PipeWirePortDirection.In, "in");
PipeWireFilterPort output = filter.AddAudioPort(PipeWirePortDirection.Out, "out");

filter.ProcessCallback = (_, sampleCount, in _) =>
{
    Span<float> dry = input.GetSamples(sampleCount);
    Span<float> wet = output.GetSamples(sampleCount);
    for (uint i = 0; i < sampleCount; i++)
        wet[(int)i] = dry[(int)i] * 0.5f;
};

await filter.ConnectAsync(PipeWireFilterFlags.RtProcess, cancellationToken);
```

Buffers exist only once the ports are linked; until then the spans come back empty. `samples/`'s
`filter` command is this, wired to the default sink.

## A metadata store of your own

Metadata is how session-wide state is shared. Serving one means other clients read and write it
through you.

```csharp
await using var provider = PipeWireMetadataProvider.Create(ctx, "my-app-state");
await provider.ReadyAsync(cancellationToken);
provider.Set("some.key", "\"value\"", "Spa:String:JSON");
```

One rule with teeth: **you cannot bind a proxy to a store this same connection serves.** The answer
would have to come from the process that is blocked waiting for it. `registry.BindMetadata` refuses
that outright rather than hanging; read your own store through the provider you already hold.

## A device with profiles and routes

`PipeWireDeviceProvider` publishes a device that appears in a mixer alongside real hardware, with
the profiles and routes you answer with. It needs `libpipewire-module-client-device`, which
`client.conf` loads unless it has been turned off.

What it does *not* do is publish the device's child nodes. A real card announces its PCM nodes
through `object_info`, and each of those is a node implementation of its own. A device announcing no
objects is legal and selectable and carries no audio - for audio, use a virtual sink.

There is also `PipeWireDeviceProvider.FromSpaFactory`, which publishes a device that lives in a SPA
plugin (`api.v4l2.enum.udev`, `api.bluez5.enum.dbus`) rather than one you implement. Nothing about
it is yours: the plugin enumerates the hardware and creates the nodes.
`PipeWireNodeProvider.FromSpaFactory` is the same move for a plugin that provides a node.

## Being the node yourself

`PipeWireNodeProvider.Create` is the bottom of the stack: you implement the node, and the graph calls
*in* rather than out. Formats, buffer negotiation and the realtime contract are yours. Upstream's
`export-source` and `export-sink` examples are the same shape.

Reach for it when `pw_stream`'s policy is the thing in your way, and not before -
[pipewire-roles.md](pipewire-roles.md) is honest about what it costs.

## Serving and consuming in one process

A served object owes the daemon prompt answers. If it shares a context with heavy consuming work,
your own slow work becomes its latency - this is the lesson WirePlumber learned and acted on, and it
retired its shared export core because of it. A process that serves something busy should serve it
on its own `PipeWireContext`. Serving one small metadata store beside your other work is fine.
