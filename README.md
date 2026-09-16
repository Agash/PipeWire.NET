# PipeWire.NET

[![NuGet](https://img.shields.io/nuget/v/PipeWire.NET.svg)](https://www.nuget.org/packages/PipeWire.NET)
[![build](https://github.com/Agash/PipeWire.NET/actions/workflows/build.yml/badge.svg)](https://github.com/Agash/PipeWire.NET/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

.NET bindings for [PipeWire](https://pipewire.org), the audio and video graph on modern Linux
desktops. Read and route the graph, control volumes and devices, capture and publish audio and
video, run DSP inside the graph, or publish virtual devices of your own.

> **Alpha.** Early and working, but largely untested in the wild and rough in places. Try it and
> file issues; expect breaking changes before 1.0.

## Install

```sh
dotnet add package PipeWire.NET          # graph, control, serving, SPA pods
dotnet add package PipeWire.NET.Media    # audio and video streams
```

## Hello, graph

```csharp
await using var ctx = new PipeWireContext();
await ctx.StartAsync();

await using var registry = new PipeWireRegistry(ctx);
await registry.WaitForInitialEnumerationAsync();

foreach (PipeWireNode node in registry.Nodes)
    Console.WriteLine($"[{node.NodeId}] {node.Description} ({node.MediaClass})");
```

A `PipeWireContext` is one connection and one event loop thread; a `PipeWireRegistry` keeps a live
snapshot of what the daemon has.

**New here?** [docs/quickstart.md](docs/quickstart.md) takes you from an empty project to reading the
graph, changing a volume, capturing audio and publishing a node, picking up the concepts on the way.
[docs/pipewire-concepts.md](docs/pipewire-concepts.md) is the model on its own.

## What you can do

| | Start here |
|---|---|
| List what exists, watch it change | `PipeWireRegistry`, below |
| Set a volume, switch a card profile, read session defaults | [one object at a time](#acting-on-one-object) |
| Capture or publish audio and video | [streams](#streaming), [docs/streaming.md](docs/streaming.md) |
| Run DSP in the graph, publish a virtual device | [docs/serving.md](docs/serving.md) |
| Work out which type you want | [docs/choosing-a-type.md](docs/choosing-a-type.md) |

The rule the API turns on: are you *reading somebody else's* object, or *being* one? Those are
different types with different suffixes, and picking wrong compiles and then does nothing you
wanted. [docs/choosing-a-type.md](docs/choosing-a-type.md) is one table per case.

## Requirements

|          |                                                                      |
| -------- | -------------------------------------------------------------------- |
| OS       | Linux (x64 / arm64)                                                  |
| Runtime  | `libpipewire-0.3.so.0` (ships with any PipeWire install)             |
| PipeWire | Bindings generated against 1.6.8; see [version policy](#pipewire-version-policy) |
| Daemon   | A running PipeWire daemon plus a session manager such as WirePlumber |
| .NET     | .NET 10, or .NET 11 (preview)                                        |

```sh
sudo apt-get install pipewire wireplumber     # Debian / Ubuntu
sudo dnf install pipewire wireplumber          # Fedora
sudo pacman -S pipewire wireplumber            # Arch
```

Both packages are Native AOT compatible, and CI publishes and runs the sample as AOT on every build.

## Reading the graph

`registry.Current` is an immutable snapshot: nodes, ports, links, devices, clients, metadata. Read
it for point queries, or consume `WatchAsync` for one snapshot per change.

```csharp
await foreach (PipeWireGraphSnapshot graph in registry.WatchAsync(cancellationToken))
{
    foreach (PipeWireNode node in graph.Nodes)
        Console.WriteLine($"[{graph.Version}] [{node.NodeId}] {node.Description}");
}
```

### Connecting over a portal fd

A sandboxed client may not see the daemon socket. xdg-desktop-portal's ScreenCast
`OpenPipeWireRemote` returns a socket fd already connected to the daemon, restricted to what the
user granted. The handle is borrowed: the library duplicates it close-on-exec, as PipeWire does
itself, and yours stays open.

`StartAsync` takes a `SafeHandle`, so wrap the descriptor the reply carries:

```csharp
using Microsoft.Win32.SafeHandles;

// rawFd: the descriptor from the OpenPipeWireRemote reply's UnixFdList
using var handle = new SafeFileHandle((IntPtr)rawFd, ownsHandle: true);

await using var ctx = new PipeWireContext();
await ctx.StartAsync(handle, cancellationToken);
```

## Acting on one object

Bind a proxy to act on one object: volumes and mutes on nodes, profiles and routes on devices,
permissions on clients, defaults and clock on metadata. A proxy is used with `await using` and
reports readiness through `ReadyAsync`.

```csharp
await using PipeWireNodeProxy node = registry.BindNode(nodeId);
await node.ReadyAsync(cancellationToken);

float? volume = await node.GetVolumeAsync(cancellationToken);
bool? muted = await node.GetMutedAsync(cancellationToken);
Console.WriteLine($"volume {volume}, muted {muted}");

await node.SetVolumeAsync(0.5f, cancellationToken);
await node.SetMutedAsync(false, cancellationToken);
```

Device routes and profiles work the same way through `registry.BindDevice`:

```csharp
await using PipeWireDeviceProxy device = registry.BindDevice(deviceId);
await device.ReadyAsync(cancellationToken);

foreach (SpaObject route in await device.EnumerateRoutesAsync(cancellationToken))
    Console.WriteLine(route);
```

The session defaults - default sink and source, graph clock rate and quantum - live in the `default`
metadata store, which is absent on a session with no session manager running:

```csharp
PipeWireMetadataProxy? store = registry.BindMetadata("default");
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

## Linking

```csharp
// Output port first, input port second.
PipeWireLink link = await registry.CreateLink(outputPortId, inputPortId)
    .ExecuteAsync(cancellationToken);

await registry.RemoveLinkAsync(link.LinkId, cancellationToken);
```

A link can be `Passive`, so it follows the graph without forcing the nodes active. Most applications
never call this: routing is the session manager's job, as
[docs/pipewire-concepts.md](docs/pipewire-concepts.md) explains.

## Streaming

Capture reads from a node; output publishes one. Both take a target node id or name, or let the
session manager choose.

```csharp
await using var capture = new PipeWireAudioCapture(ctx);
capture.FrameReady += (_, frame) =>
    Console.WriteLine($"{frame.SampleRate} Hz {frame.Channels}ch {frame.Format}");

capture.Connect();
```

```csharp
await using var output = new PipeWireAudioOutput(ctx, "sample_synth");
output.FillSamples += (_, samples, sampleRate, channels, format) =>
{
    // The byte count written; 0 publishes silence.
    return WriteTone(samples, sampleRate, channels);
};

output.Connect();
```

Video is the same shape:

```csharp
await using var camera = new PipeWireVideoCapture(ctx);
camera.FrameReady += (_, frame) =>
    Console.WriteLine($"{frame.Width}x{frame.Height} {frame.Format}");

camera.Connect();   // auto-selects the default video source
```

```csharp
await using var screen = new PipeWireVideoOutput(ctx, "sample_screen", 1280, 720);
screen.FillFrame += (_, pixels, stride, width, height, format) =>
{
    Render(pixels, stride, width, height);
    return true;          // false publishes nothing this cycle
};

screen.Connect();
```

Frames are `ref struct`s delivered on the loop thread and valid only for the handler; `Clone` what
must outlive it. They carry the pixels or samples, the negotiated format, the backing memory and
four timestamps. Fill callbacks write straight into the daemon's buffer, so there is no intermediate
copy, and capture can take DMA-BUF buffers for GPU sources.

[docs/streaming.md](docs/streaming.md) covers timestamps and A/V sync, DMA-BUF, explicit sync and
multi-GPU device negotiation.

### Screen capture on Wayland

This library does not talk to Wayland. Screen capture goes through the
`org.freedesktop.portal.ScreenCast` portal, which returns a node id after the user grants
permission; pass it to `PipeWireVideoCapture.Connect(nodeId)` and it behaves like any other source.

## Serving

Publishing something other clients use - a virtual sink, a DSP node, a device, a metadata store - is
its own guide: [docs/serving.md](docs/serving.md). The shortest example is a sink other applications
can play into, which needs no callbacks at all:

```csharp
PipeWireNode sink = await registry.CreateVirtualSinkAsync("My mix", cancellationToken: cancellationToken);
```

## SPA pods

Everything configurable on a node, port or device is a parameter, and every parameter is a SPA pod.
`SpaPod` parses and writes the value model (`SpaInt`, `SpaString`, `SpaObject`, `SpaChoice` and the
rest); [docs/parameters-and-pods.md](docs/parameters-and-pods.md) covers reading a device's routes,
what a `SpaChoice` means during negotiation, and writing one back.

```csharp
byte[] bytes = SpaPod.ToBytes(new SpaInt(48000));
if (SpaPod.TryParse(bytes, out SpaValue? value) && value is SpaInt rate)
    Console.WriteLine(rate.Value);
```

## Sample app

`samples/PipeWire.NET.SampleConsole` is a small CLI over both packages, and the quickest way to see
whether your machine is set up.

```sh
dotnet run --project samples/PipeWire.NET.SampleConsole -- list
dotnet run --project samples/PipeWire.NET.SampleConsole -- monitor
dotnet run --project samples/PipeWire.NET.SampleConsole -- volume alsa_output.pci --set 0.5
dotnet run --project samples/PipeWire.NET.SampleConsole -- defaults
dotnet run --project samples/PipeWire.NET.SampleConsole -- capture-audio --seconds 5
dotnet run --project samples/PipeWire.NET.SampleConsole -- capture-video --seconds 5
dotnet run --project samples/PipeWire.NET.SampleConsole -- filter --seconds 8
dotnet run --project samples/PipeWire.NET.SampleConsole -- serve
```

`filter` plays a quiet generated tone through a gain node into the default sink; `serve` publishes a
virtual source until Ctrl+C.

## Documentation

**Start here**

| | |
|---|---|
| [quickstart.md](docs/quickstart.md) | empty project to working program, with the concepts as you need them |
| [pipewire-concepts.md](docs/pipewire-concepts.md) | the graph model in ten minutes, if PipeWire is new to you |
| [choosing-a-type.md](docs/choosing-a-type.md) | which type to reach for, per task, with upstream's name for each |
| [troubleshooting.md](docs/troubleshooting.md) | symptoms and what they usually mean |

**Going further**

| | |
|---|---|
| [threading-and-lifetimes.md](docs/threading-and-lifetimes.md) | callback threading, the realtime contract, disposal, lingering objects |
| [parameters-and-pods.md](docs/parameters-and-pods.md) | reading and writing parameters: routes, profiles, formats |
| [streaming.md](docs/streaming.md) | frame timing, A/V sync, DMA-BUF, explicit sync |
| [serving.md](docs/serving.md) | publishing nodes, devices, filters and metadata |
| [pipewire-roles.md](docs/pipewire-roles.md) | consuming versus being an object, and what serving costs |
| [running-tests.md](docs/running-tests.md) | the test categories and what each one needs |

Upstream's own [Overview](https://docs.pipewire.org/page_overview.html) and
[API tutorial](https://docs.pipewire.org/page_tutorial.html) are the reference for the C API these
bindings cover.

## How it is built

The low-level bindings in `src/PipeWire.NET/generated/` are produced by
[ClangSharpPInvokeGenerator](https://github.com/dotnet/ClangSharp) from the installed PipeWire
headers and committed, so consumers never run the generator. Four passes produce them: the ABI
(`pipewire.rsp`) and one per library whose macros are needed (`pipewire-constants.rsp`, `libc.rsp`,
`libdrm.rsp`). The hand-written types on top - context, registry, proxies, providers, streams,
filters, the SPA pod codec - are ordinary C#.

To regenerate after a PipeWire version bump, on Linux with `libpipewire-0.3-dev` and `libclang-dev`:

```sh
dotnet tool install --global ClangSharpPInvokeGenerator --version 21.1.8.3
bash generate/generate.sh
```

CI regenerates on every build and fails if the committed output drifts.

### PipeWire version policy

The bindings are generated from the headers of one specific release, recorded in
`generate/HEADER-VERSION` and enforced by the generator. That release is what the committed bindings
describe and what the gating test job runs against.

Older daemons are not rejected and should mostly work: the library binds a small, long-stable part
of the protocol. What an older daemon can lack is usually a whole interface, and binding one that is
not there fails at the bind rather than silently misbehaving. If you need a specific older release
supported, open an issue with the version.

## Testing

```sh
dotnet test --filter "TestCategory!=Integration"     # pure logic, runs anywhere
dotnet test --filter "TestCategory=Integration"      # needs a running daemon
```

Integration tests run against a live daemon, some of them driving real producers through GStreamer.
[docs/running-tests.md](docs/running-tests.md) lists every category and what it needs.

## License

MIT, see [LICENSE](LICENSE). PipeWire is MIT licensed and is not redistributed here.
