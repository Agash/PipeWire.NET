# PipeWire.NET

[![NuGet](https://img.shields.io/nuget/v/PipeWire.NET.svg)](https://www.nuget.org/packages/PipeWire.NET)
[![build](https://github.com/Agash/PipeWire.NET/actions/workflows/build.yml/badge.svg)](https://github.com/Agash/PipeWire.NET/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

.NET bindings for [PipeWire](https://pipewire.org) on Linux. `PipeWire.NET`
for the graph (enumeration, routing, node and device control, DSP nodes, virtual devices),
and `PipeWire.NET.Media` streams audio and video through it.

> **Alpha.** Early and working, but largely untested in the wild and rough in places. Try it and file
> issues; expect breaking changes before 1.0.

```csharp
await using var ctx = new PipeWireContext();
await ctx.StartAsync();

await using var registry = new PipeWireRegistry(ctx);
await registry.WaitForInitialEnumerationAsync();

foreach (PipeWireNode node in registry.Nodes)
    Console.WriteLine($"[{node.NodeId}] {node.Description} ({node.MediaClass})");
```

## Packages

| Package              | What it holds                                                                                                                                                                                   |
| -------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `PipeWire.NET`       | Context and loop, registry and snapshots, virtual nodes and links, node/device/client controls, metadata stores, virtual device and metadata providers, in-graph DSP filters, the SPA pod codec |
| `PipeWire.NET.Media` | Audio/video capture and output, DMA-BUF and explicit sync, frame timestamps and clock alignment                                                                                                 |

## Requirements

|          |                                                                      |
| -------- | -------------------------------------------------------------------- |
| OS       | Linux (x64 / arm64)                                                  |
| Runtime  | `libpipewire-0.3.so.0` (ships with any PipeWire install)             |
| PipeWire | Bindings generated against 1.6.8; see the version policy below       |
| Daemon   | A running PipeWire daemon plus a session manager such as WirePlumber |
| .NET     | .NET 10, or .NET 11 (preview)                                        |

```sh
sudo apt-get install pipewire wireplumber     # Debian / Ubuntu
sudo dnf install pipewire wireplumber          # Fedora
sudo pacman -S pipewire wireplumber            # Arch
```

The library and the sample publish as NativeAOT.

### PipeWire version policy

The native bindings are generated from the PipeWire headers of one specific release, recorded in`generate/HEADER-VERSION` and enforced by the generator. That release is what the committed bindings describe, and it is the version the gating test job runs against.

Older daemons arent rejected I believe, and should mostly work: the library binds a small, long-stable part of the protocol. What an older daemon can lack is usually a whole interface, and binding one that is not there fails at the bind rather than silently misbehaving.

If you need a specific older release supported, open an issue with the version.

## Install

```sh
dotnet add package PipeWire.NET          # graph, metadata, control
dotnet add package PipeWire.NET.Media    # audio and video streams
```

## The graph

Everything starts with a `PipeWireContext`: one connection, one realtime loop. Native calls that must serialize with the loop go through `Lock()` (or `TryLock`), which hands out a scope; disposal waits out every live scope, so a callback can never outlive the loop it runs on.

`PipeWireRegistry` keeps a live, immutable `Current` snapshot of the graph: nodes, ports, links, devices, clients, metadata stores. Read it directly for point queries, or consume `WatchAsync` for a snapshot per change.

```csharp
await foreach (PipeWireGraphSnapshot graph in registry.WatchAsync(cancellationToken))
{
    foreach (PipeWireNode node in graph.Nodes)
        Console.WriteLine($"[{graph.Version}] [{node.NodeId}] {node.Description}");
}
```

### Connecting over a portal fd

A sandboxed client may not see the daemon socket itself. xdg-desktop-portal's ScreenCast
`OpenPipeWireRemote` returns the next best thing: a socket fd already connected to the daemon,
restricted to the access the user granted. `StartAsync` connects over that fd directly. The
handle is only borrowed: the library duplicates the descriptor (close-on-exec, as PipeWire does
itself), the connection owns the duplicate, and the caller's handle stays open and usable.

```csharp
// fd: the SafeFileHandle the ScreenCast OpenPipeWireRemote reply carries
await using var ctx = new PipeWireContext();
await ctx.StartAsync(fd, cancellationToken);
```

### Creating nodes and links

```csharp
PipeWireNode source = await registry.CreateVirtualNode("Audio/Source")
    .WithName("sample_tone")
    .ExecuteAsync(cancellationToken);

// Link two ports by id: output first, input second.
PipeWireLink link = await registry.CreateLink(outputPortId, inputPortId)
    .ExecuteAsync(cancellationToken);

await registry.RemoveLinkAsync(link.LinkId, cancellationToken);
await registry.DestroyGlobalAsync(source.NodeId, cancellationToken);
```

A creation can opt out of cleanup-on-dispose with `WithLinger`, in which case the daemon object survives the process and `LingeringIds` lists what was left behind for later destruction. A link can be `Passive`, so it follows the graph without forcing the nodes active.

## Control

Bind a control to act on one object: volumes and mutes on nodes, profiles and routes on devices, permissions on clients, defaults and clock on metadata stores. Controls are used with `await using` and report readiness through `ReadyAsync`.

```csharp
await using PipeWireNodeControl control = registry.BindNode(nodeId);
await control.ReadyAsync(cancellationToken);

float? volume = await control.GetVolumeAsync(cancellationToken);
bool? muted = await control.GetMutedAsync(cancellationToken);
Console.WriteLine($"volume {volume}, muted {muted}");

await control.SetVolumeAsync(0.5f, cancellationToken);
await control.SetMutedAsync(false, cancellationToken);
```

Device routes and profiles work the same way through `registry.BindDevice`:

```csharp
await using PipeWireDeviceControl device = registry.BindDevice(deviceId);
await device.ReadyAsync(cancellationToken);

foreach (SpaObject route in await device.EnumerateRoutesAsync(cancellationToken))
    Console.WriteLine(route);
```

The session defaults (default sink/source, graph clock rate and quantum) live in the `default` metadata store, which may be absent on a session without a session manager:

```csharp
PipeWireMetadataStore? store = registry.BindMetadataStore("default");
if (store is not null)
{
    await using (store)
    {
        await store.ReadyAsync(cancellationToken);
        Console.WriteLine($"sink: {store.DefaultAudioSink?.NameValue}");
        Console.WriteLine($"source: {store.DefaultAudioSource?.NameValue}");
        Console.WriteLine($"clock: {store.ClockRate} Hz / quantum {store.ClockQuantum}");
    }
}
```

## Serving the graph

`PipeWireFilter` is a DSP node of your own inside the graph: declare audio, MIDI, or control ports, handle `ProcessCallback` on the realtime thread, and link it like any other node. Buffers are only present once the ports are linked; until then the spans come back empty.

```csharp
await using PipeWireFilter filter = PipeWireFilter.Create(ctx, "sample_gain");
PipeWireFilterPort input = filter.AddAudioPort(PipeWirePortDirection.In, "in");
PipeWireFilterPort output = filter.AddAudioPort(PipeWirePortDirection.Out, "out");

filter.ProcessCallback = (_, sampleCount) =>
{
    Span<float> dry = input.GetSamples(sampleCount);
    Span<float> wet = output.GetSamples(sampleCount);
    for (uint i = 0; i < sampleCount; i++)
        wet[(int)i] = dry[(int)i] * 0.5f;
};

await filter.ConnectAsync(cancellationToken);
```

For virtual hardware there is `PipeWireDeviceProvider` (profiles, routes, parameters served to the daemon) and for session state `PipeWireMetadataProvider`. The sample project and `DeviceProviderTests` show both ends: export the object, then read it back through the ordinary client path.

## Streaming audio

```csharp
await using var capture = new PipeWireAudioCapture(ctx);
capture.FrameReady += (_, frame) =>
    Console.WriteLine($"{frame.SampleRate} Hz {frame.Channels}ch {frame.Format}");

capture.Connect();
```

```csharp
await using var output = new PipeWireAudioOutput(ctx, "sample_synth");
output.FillSamples += (_, samples, sampleRate, channels, format) =>
    WriteTone(samples, sampleRate, channels);
output.Connect();
```

`FillSamples` returns the byte count written; returning 0 publishes silence. Both capture types accept a target node id or a `targetObjectName` to bind to a specific node instead of relying on the session manager's default routing.

## Streaming video

```csharp
await using var camera = new PipeWireVideoCapture(ctx);
camera.FrameReady += (_, frame) =>
    Console.WriteLine($"{frame.Width}x{frame.Height} {frame.Format}");

camera.Connect();   // auto-selects the default video source
```

```csharp
await using var screen = new PipeWireVideoOutput(ctx, "sample_screen", 1280, 720);
screen.FillFrame += (_, pixels, stride, width, height, format) =>
    Render(pixels, stride, width, height);
screen.Connect();
```

On Linux this is how you feed a tool like OBS: publish a node here, then add a PipeWire video source in OBS and it reads the feed. It is the Linux counterpart to Spout on Windows or Syphon on macOS.

## Frames

`VideoFrame` and `AudioFrame` are `ref struct`s delivered on the loop thread; their data is valid only for the duration of the handler. Copy out (`Clone`) anything that must outlive it.

`VideoFrame` carries the pixels (`Data`, `Stride`, `Width`, `Height`, `Format`), the negotiated `Color` info, the backing memory (`BufferType`, `Fd`, `MapOffset`), and timing (see below). For a DMA-BUF frame it also exposes the DRM format `Modifier` and the per-plane layout (`Planes`: fd, offset, stride, size per plane, e.g. two planes for `Nv12`), so a multi-plane tiled surface can be imported correctly. `AudioFrame` carries `Samples`, `SampleRate`, `Channels`, `Format`, `FrameCount`, and timing.

### Timing and A/V sync

Every stream runs off one graph clock. Each frame carries:

- `CaptureClockNs`: the monotonic graph time of the cycle that delivered it. It is the same clock for every stream, so align audio against video on this value to keep them in sync.
- `MediaClockNs` and `DelayNs`: the stream's media position and its latency, for
  sample-accurate timestamping.

`PresentationTimeNs` is the content timestamp from the buffer header. Video sources provide it; PipeWire audio does not, so for audio it is `-1`. Use `CaptureClockNs` for sync.

### Zero copy

On capture, `frame.Data` points straight into the daemon's mapped buffer, so reading is free. Capture also accepts DMA-BUF buffers, so a GPU source can hand frames over without touching the CPU; `frame.BufferType` and `frame.Fd` expose the descriptor for GPU import.

On publish, `FillFrame` and `FillSamples` give you a span over the daemon's buffer, so you write the frame once with no intermediate copy.

For a fully GPU-resident publish, `PipeWireVideoOutput.ConnectDmaBuf(modifiers)` advertises a set of DRM format modifiers, negotiates one with the consumer, and backs the stream with DMA-BUF buffers you own. Allocate your GPU surfaces in the `AllocateDmaBuf` callback (export each once, e.g. via `vkGetMemoryFdKHR`) and write the chosen buffer in `FillDmaBuf`; `ReleaseDmaBuf` tears them down. The producer can self-pace with `TriggerFrame`, and `NodeId` lets a consumer target the node directly.

## Screen capture on Wayland

This library does not deal with Wayland directly. Screen capture goes through the `org.freedesktop.portal.ScreenCast` (or similar) portal, which after the user grants permission returns a PipeWire node id. Pass that id to `PipeWireVideoCapture.Connect(nodeId)` and it behaves like any other source.

## SPA pods

Parameters on the wire are SPA pods. `SpaPod` parses and writes the value model (`SpaInt`, `SpaString`, `SpaObject`, `SpaChoice`, and the rest); the builder and reader types used while assembling them are currently internal.

```csharp
byte[] bytes = SpaPod.ToBytes(new SpaInt(48000));
if (SpaPod.TryParse(bytes, out SpaValue? value) && value is SpaInt rate)
    Console.WriteLine(rate.Value);
```

## Sample app

`samples/PipeWire.NET.SampleConsole` is a small CLI over both packages. With no arguments it connects and lists the graph; every mutating command needs a flag or target.

```sh
dotnet run --project samples/PipeWire.NET.SampleConsole -- list
dotnet run --project samples/PipeWire.NET.SampleConsole -- monitor
dotnet run --project samples/PipeWire.NET.SampleConsole -- volume
dotnet run --project samples/PipeWire.NET.SampleConsole -- volume alsa_output.pci --set 0.5
dotnet run --project samples/PipeWire.NET.SampleConsole -- defaults
dotnet run --project samples/PipeWire.NET.SampleConsole -- capture-audio --seconds 5
dotnet run --project samples/PipeWire.NET.SampleConsole -- capture-video --seconds 5
dotnet run --project samples/PipeWire.NET.SampleConsole -- filter --seconds 8
dotnet run --project samples/PipeWire.NET.SampleConsole -- serve
```

`serve` publishes a virtual source until Ctrl+C. `filter` plays a quiet generated tone through a gain node into the default sink, if there is one; otherwise it runs the node unlinked.

## How it is built

The low-level bindings in `src/PipeWire.NET/generated/` are produced by[ClangSharpPInvokeGenerator](https://github.com/dotnet/ClangSharp) from the installed PipeWire headers and committed to the repo, so consumers never run the generator. The hand-written high-level types (`PipeWireContext`, `PipeWireRegistry`, the node/device/client controls, the stream classes, `PipeWireFilter`, the providers) and the SPA pod helpers sit on top.

To regenerate after a PipeWire version bump (on Linux, with `libpipewire-0.3-dev` and
`libclang-dev`):

```sh
dotnet tool install --global ClangSharpPInvokeGenerator --version 21.1.8.3
bash generate/generate.sh
```

CI runs the generator on every build and fails if the committed output drifts.

## Testing

```sh
dotnet test --filter "TestCategory!=Integration"     # pure logic, runs anywhere
dotnet test --filter "TestCategory=Integration"      # needs a running daemon
```

Integration tests run against a live daemon. Some start real producers through GStreamer (`videotestsrc`, `audiotestsrc`) and check capture across formats, registry discovery, real frame content, alpha preservation, timestamps, and audio/video sharing one clock. Tests tagged `RequiresGpu` cover DMA-BUF capture and run on a host with a GPU; everything else runs on CI against a headless PipeWire.

## License

MIT, see [LICENSE](LICENSE). PipeWire is MIT licensed and is not redistributed here.
