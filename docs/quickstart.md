# Quick start

From nothing to a program that reads the graph, changes a volume and captures audio. Ten minutes,
and you pick up the concepts as you need them. The fuller model is
[pipewire-concepts.md](pipewire-concepts.md); you do not need it yet.

## 1. Check your machine

You need a running PipeWire daemon and a session manager. Almost every desktop Linux since 2022 has
both. Ask:

```sh
pw-cli info 0        # the daemon answers with its core info
wpctl status         # WirePlumber lists devices, streams and the defaults
```

If `pw-cli` is missing, install PipeWire (`pipewire` and `wireplumber` in every distro's
repositories). If `pw-cli` answers but `wpctl` does not, you have a daemon with no session manager:
things will work, but nothing routes itself and there are no defaults.

## 2. A project

```sh
dotnet new console -o pwdemo && cd pwdemo
dotnet add package PipeWire.NET
dotnet add package PipeWire.NET.Media
```

## 3. List the graph

Replace `Program.cs` with this, and run it:

```csharp
using PipeWire.NET;
using PipeWire.NET.Graph;

await using var ctx = new PipeWireContext("pwdemo");
await ctx.StartAsync();

await using var registry = new PipeWireRegistry(ctx);
await registry.WaitForInitialEnumerationAsync();

foreach (PipeWireNode node in registry.Nodes)
    Console.WriteLine($"[{node.NodeId}] {node.MediaClass,-24} {node.Description}");
```

Three things just happened, and they are the whole model in miniature:

- **`PipeWireContext` is your connection**, plus the event loop thread everything arrives on. One
  per application is normal.
- **`PipeWireRegistry` mirrors the daemon's graph.** `WaitForInitialEnumerationAsync` waits for the
  first full listing; after that it keeps itself current.
- **A node is a thing that makes or takes media** - a microphone, your speakers, Firefox. What kind
  it is, is `MediaClass`: `Audio/Sink` is something to play into, `Audio/Source` something to record
  from, `Stream/Output/Audio` an application playing.

## 4. Read and set a volume

Reading the graph gives you records. To *act* on one object you bind a proxy to it:

```csharp
using PipeWire.NET;
using PipeWire.NET.Graph;

await using var ctx = new PipeWireContext("pwdemo");
await ctx.StartAsync();

await using var registry = new PipeWireRegistry(ctx);
await registry.WaitForInitialEnumerationAsync();

PipeWireNode? sink = registry.Current.Nodes.FirstOrDefault(n => n.MediaClass == "Audio/Sink");
if (sink is null) return;

await using PipeWireNodeProxy proxy = registry.BindNode(sink.NodeId);
await proxy.ReadyAsync();

Console.WriteLine($"{sink.Description}: volume {await proxy.GetVolumeAsync()}");
await proxy.SetVolumeAsync(0.4f);
```

The distinction matters more than it looks: `PipeWireNode` is *data the registry has about somebody
else's node*, and `PipeWireNodeProxy` is *a live connection to it* that can call methods. Every kind
works this way, and [choosing-a-type.md](choosing-a-type.md) is the table.

`ReadyAsync` is not ceremony. Binding is a request to the daemon; until it answers, the proxy has no
parameters to give you.

## 5. Capture some audio

```csharp
using PipeWire.NET;
using PipeWire.NET.Media;

await using var ctx = new PipeWireContext("pwdemo-capture");
await ctx.StartAsync();

var frames = 0;
await using var capture = new PipeWireAudioCapture(ctx);

capture.FrameReady += (_, frame) =>
{
    if (frames++ == 0)
        Console.WriteLine($"{frame.SampleRate} Hz, {frame.Channels} channels, {frame.Format}");
};

capture.Connect();
await Task.Delay(TimeSpan.FromSeconds(3));
Console.WriteLine($"{frames} buffers");
```

Note what you did *not* do: you never said which device to record from, and you never linked
anything. You said "I am an audio capture" and the session manager routed you to the default source.
That is PipeWire's division of labour - the daemon owns the graph, a session manager decides the
policy, and your program mostly declares what it is.

`FrameReady` runs on the loop thread, and the frame's memory is the daemon's buffer, valid only for
the duration of your handler. Copy out (`Clone`) anything you keep. Do not block in there: the graph
is waiting, and a late return is an audible glitch for everything in that driver group.

## 6. Publish something

The reverse direction. This publishes a node other applications can record from, and fills it with
silence:

```csharp
using PipeWire.NET;
using PipeWire.NET.Media;

await using var ctx = new PipeWireContext("pwdemo-source");
await ctx.StartAsync();

await using var output = new PipeWireAudioOutput(ctx, "pwdemo_source");
output.FillSamples += (_, samples, sampleRate, channels, format) =>
{
    samples.Clear();
    return samples.Length;
};

output.Connect();
Console.WriteLine("publishing; check wpctl status");
await Task.Delay(TimeSpan.FromSeconds(10));
```

Run it and look at `wpctl status` in another terminal: `pwdemo_source` is there, and any recorder
can select it. Returning 0 from the callback publishes silence; returning the byte count publishes
what you wrote.

## 7. Publish video

Same shape, and no GPU work involved: you declare a size and a pixel format, and fill the span the
daemon hands you.

```csharp
using PipeWire.NET;
using PipeWire.NET.Media;

await using var ctx = new PipeWireContext("pwdemo-screen");
await ctx.StartAsync();

await using var screen = new PipeWireVideoOutput(ctx, "pwdemo_screen", 1280, 720, PixelFormat.Bgra, 30);

byte tick = 0;
screen.FillFrame += (_, pixels, stride, width, height, format) =>
{
    pixels.Fill(tick++);   // a solid colour that changes every frame
    return true;
};

screen.Connect();
Console.WriteLine("publishing video; add a PipeWire source in OBS to see it");
await Task.Delay(TimeSpan.FromSeconds(30));
```

Returning `false` from `FillFrame` publishes nothing that cycle, which is how a producer with no new
frame yet says so.

That is the ordinary, host-memory path, and it is what most producers want. When the pixels are
already on the GPU there is a zero-copy path (`ConnectDmaBuf`) that avoids the download entirely -
[streaming.md](streaming.md) covers it, and you do not need it to start.

## Where next

| You want | Go to |
|---|---|
| The model properly: ports, links, clocks, permissions | [pipewire-concepts.md](pipewire-concepts.md) |
| To know which type to reach for | [choosing-a-type.md](choosing-a-type.md) |
| Virtual devices, DSP, metadata stores | [serving.md](serving.md) |
| Timestamps, A/V sync, zero copy on the GPU | [streaming.md](streaming.md) |
| Reading a device's routes and profiles | [parameters-and-pods.md](parameters-and-pods.md) |
| Callback threading and object lifetime rules | [threading-and-lifetimes.md](threading-and-lifetimes.md) |
| Something is not working | [troubleshooting.md](troubleshooting.md) |

The `samples/PipeWire.NET.SampleConsole` project in this repository is a working CLI over all of it:
`list`, `monitor`, `volume`, `defaults`, `capture-audio`, `capture-video`, `filter`, `serve`.
