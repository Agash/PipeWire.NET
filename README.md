# PipeWire.NET

[![NuGet](https://img.shields.io/nuget/v/PipeWire.NET.svg)](https://www.nuget.org/packages/PipeWire.NET)
[![build](https://github.com/Agash/PipeWire.NET/actions/workflows/build.yml/badge.svg)](https://github.com/Agash/PipeWire.NET/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

.NET bindings for [PipeWire](https://pipewire.org) on Linux. Capture and publish video and audio
through the local media graph, with an API that stays close to PipeWire while feeling natural in C#.

```csharp
await using var ctx = new PipeWireContext();
await ctx.StartAsync();

await using var camera = new PipeWireVideoCapture(ctx);
camera.FrameReady += (_, frame) =>
    Console.WriteLine($"{frame.Width}x{frame.Height} {frame.Format}");
camera.Connect();                       // auto-selects the default video source

await Task.Delay(TimeSpan.FromSeconds(5));
```

## What you get

- Capture and publish, for both video and audio.
- Source discovery through the registry (cameras, microphones, virtual nodes).
- Frame timestamps on a shared clock, so audio and video can be kept in sync.
- Efficient buffers: frames are read straight from shared memory with no copy, and DMA-BUF file
  descriptors are exposed for GPU import on the capture side.
- NativeAOT friendly: source-generated P/Invoke, no reflection.

## Requirements

| | |
|---|---|
| OS | Linux (x64 / arm64) |
| Runtime | `libpipewire-0.3.so.0` (ships with any PipeWire install) |
| Daemon | A running PipeWire daemon plus a session manager such as WirePlumber |
| .NET | .NET 10, or .NET 11 (preview) |

```sh
sudo apt-get install pipewire wireplumber     # Debian / Ubuntu
sudo dnf install pipewire wireplumber          # Fedora
sudo pacman -S pipewire wireplumber            # Arch
```

## Install

```sh
dotnet add package PipeWire.NET
```

## Usage

### Discover sources

```csharp
await using var registry = new PipeWireRegistry(ctx);
await registry.WaitForInitialEnumerationAsync();

foreach (var source in registry.Sources.Where(s => s.IsVideoSource))
    Console.WriteLine($"[{source.NodeId}] {source.Description} ({source.Class})");
```

### Capture video

```csharp
await using var capture = new PipeWireVideoCapture(ctx);

capture.FrameReady += (_, frame) =>
{
    // frame.Data is valid only inside this handler. Do not store it.
    Process(frame.Data, frame.Stride);
};

capture.Connect(source, preferredFormats: [PixelFormat.Bgra, PixelFormat.Rgba]);
```

### Publish a virtual camera

```csharp
await using var output = new PipeWireVideoOutput(ctx, "My Virtual Camera",
    width: 1280, height: 720, format: PixelFormat.Bgra, frameRate: 30);

output.FillFrame += (_, pixels, stride, w, h, format) =>
{
    RenderInto(pixels, stride, w, h);   // write straight into the buffer
    return true;
};

output.Connect();
```

On Linux this is how you feed a tool like OBS: publish a node here, then add a PipeWire video
source in OBS and it reads the feed. It is the Linux counterpart to Spout on Windows or Syphon
on macOS.

### Capture and publish audio

```csharp
await using var mic = new PipeWireAudioCapture(ctx);
mic.FrameReady += (_, frame) =>
    Mix(frame.Samples, frame.SampleRate, frame.Channels, frame.Format);
mic.Connect();

await using var synth = new PipeWireAudioOutput(ctx, "Synth",
    sampleRate: 48000, channels: 2, format: AudioSampleFormat.F32Le);
synth.FillSamples += (_, buffer, rate, channels, format) => Synthesize(buffer);
synth.Connect();
```

Both capture types accept a `targetObjectName` to bind to a specific node by name instead of
relying on the session manager's default routing.

## Frames

`VideoFrame` and `AudioFrame` are `ref struct`s delivered on the loop thread; their data is valid
only for the duration of the handler.

`VideoFrame` carries the pixels (`Data`, `Stride`, `Width`, `Height`, `Format`), the negotiated
`Color` info, the backing memory (`BufferType`, `Fd`, `MapOffset`), and timing (see below). For a
DMA-BUF frame it also exposes the DRM format `Modifier` and the per-plane layout (`Planes`: fd,
offset, stride, size per plane — e.g. two planes for `Nv12`), so a multi-plane tiled surface can be
imported correctly. `AudioFrame` carries `Samples`, `SampleRate`, `Channels`, `Format`,
`FrameCount`, and timing.

### Timing and A/V sync

Every stream runs off one graph clock. Each frame carries:

- `CaptureClockNs`: the monotonic graph time of the cycle that delivered it. It is the same clock
  for every stream, so align audio against video on this value to keep them in sync.
- `MediaClockNs` and `DelayNs`: the stream's media position and its latency, for
  sample-accurate timestamping.

`PresentationTimeNs` is the content timestamp from the buffer header. Video sources provide it;
PipeWire audio does not, so for audio it is `-1`. Use `CaptureClockNs` for sync.

### Zero copy

On capture, `frame.Data` points straight into the daemon's mapped buffer, so reading is free.
Capture also accepts DMA-BUF buffers, so a GPU source can hand frames over without touching the
CPU; `frame.BufferType` and `frame.Fd` expose the descriptor for GPU import.

On publish, `FillFrame` and `FillSamples` give you a span over the daemon's buffer, so you write
the frame once with no intermediate copy.

For a fully GPU-resident publish, `PipeWireVideoOutput.ConnectDmaBuf(modifiers)` advertises a set of
DRM format modifiers, negotiates one with the consumer, and backs the stream with DMA-BUF buffers you
own. Allocate your GPU surfaces in the `AllocateDmaBuf` callback (export each once, e.g. via
`vkGetMemoryFdKHR`) and write the chosen buffer in `FillDmaBuf`; `ReleaseDmaBuf` tears them down. The
producer can self-pace with `TriggerFrame`, and `NodeId` lets a consumer target the node directly.
This is the path a VAAPI/Vulkan pipeline uses to feed OBS with no CPU round-trip.

## Screen capture on Wayland

This library does not deal with Wayland directly. Screen capture goes through the
`org.freedesktop.portal.ScreenCast` portal, which after the user grants permission returns a
PipeWire node id. Pass that id to `PipeWireVideoCapture.Connect(nodeId)` and it behaves like any
other source. Drive the portal with any D-Bus library; this library takes it from the node id on.

## How it is built

The low-level bindings in `src/PipeWire.NET/generated/` are produced by
[ClangSharpPInvokeGenerator](https://github.com/dotnet/ClangSharp) from the installed PipeWire
headers and committed to the repo, so consumers never run the generator. The hand-written
high-level types (`PipeWireContext`, the four stream classes, `PipeWireRegistry`) and the SPA pod
helpers sit on top.

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

Integration tests run against a live daemon. Some start real producers through GStreamer
(`videotestsrc`, `audiotestsrc`) and check capture across formats, registry discovery, real frame
content, alpha preservation, timestamps, and audio/video sharing one clock. Tests tagged
`RequiresGpu` cover DMA-BUF capture and run on a host with a GPU; everything else runs on CI
against a headless PipeWire.

## Scope

This library is the PipeWire layer: it delivers correctly formatted, correctly timed frames and
samples in and out. Encoding and network transport live above it.

DMA-BUF is supported on **both** directions: capture imports DMA-BUF frames, and publish can hand out
GPU-resident DMA-BUF buffers (`PipeWireVideoOutput.ConnectDmaBuf`, with DRM format-modifier
negotiation — see "Zero copy"). A zero-copy GPU producer (e.g. a VAAPI/Vulkan pipeline) thus feeds a
GPU consumer like OBS with no CPU round-trip; host-memory output (`Connect` + `FillFrame`) remains for
sources already in CPU memory.

## License

MIT, see [LICENSE](LICENSE). PipeWire is MIT licensed and is not redistributed here.
