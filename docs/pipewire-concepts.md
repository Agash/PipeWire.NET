# PipeWire in ten minutes

Enough of PipeWire's model to use this library without reading upstream first. If you already know
the graph, skip to [choosing-a-type.md](choosing-a-type.md); if you would rather learn it with a
project open, start with [quickstart.md](quickstart.md).

Upstream's own introduction is [Overview](https://docs.pipewire.org/page_overview.html), and its
[API tutorial](https://docs.pipewire.org/page_tutorial.html) is the C version of what this page
describes.

## A daemon, and a graph

PipeWire is a daemon that owns a graph of media objects, and a client library that talks to it over
a UNIX socket. Your process is a client. Nothing you create is local: you ask the daemon for it, and
every other client on the machine can see the result.

The graph is made of a few object kinds. You will meet these five constantly:

| Object | What it is | In this library |
|---|---|---|
| **Node** | A thing that produces or consumes media. A microphone, a speaker, Firefox's audio, your app. | `PipeWireNode`, `PipeWireNodeProxy` |
| **Port** | A node's input or output, one per channel for audio. Nodes are linked port to port. | `PipeWirePort`, `PipeWirePortProxy` |
| **Link** | A connection between one output port and one input port. | `PipeWireLink`, `registry.CreateLink` |
| **Device** | The hardware behind nodes: a sound card with profiles and routes. | `PipeWireDevice`, `PipeWireDeviceProxy` |
| **Metadata** | A key/value store the whole session shares. The default sink lives here. | `PipeWireMetadata`, `registry.BindMetadata` |

Three more appear in a graph listing and matter less day to day: **Client** (another process
connected to the daemon), **Module** and **Factory** (what the daemon loaded, and what it can
create), and **Core** (the connection itself).

Every object has a numeric **id**, unique for as long as it exists and never reused while it does.
Ids are how you refer to anything: `registry.BindNode(id)`, `CreateLink(outputPortId, inputPortId)`.

## Properties, and media.class

Objects carry string properties - `node.name`, `node.description`, `media.class`, `audio.position`
and a few hundred more. They are how the graph explains itself, and how you explain your own objects
to it.

`media.class` is the one worth knowing by name, because routing is decided from it:

| `media.class` | Means |
|---|---|
| `Audio/Source` | Something to record *from* (a microphone) |
| `Audio/Sink` | Something to play *into* (speakers) |
| `Stream/Output/Audio` | An application playing audio |
| `Stream/Input/Audio` | An application recording audio |
| `Video/Source` | A camera, a screen capture |

`PipeWireKeys` has the key names as constants, so you do not hand-write the strings.

## The session manager decides routing

The daemon does not decide what connects to what. A **session manager** - almost always
[WirePlumber](https://pipewire.pages.freedesktop.org/wireplumber/) - watches the graph and makes the
policy decisions: which sink a new stream goes to, what the default source is, which profile a card
uses.

That has one consequence worth internalising: **when you create a stream, you usually do not link
it**. You say what it is (`media.class`) and the session manager links it. If you link by hand you
are overriding policy, which is legitimate but is your problem to maintain.

Session-wide state lives in a metadata store called `default`:

```csharp
PipeWireMetadataProxy? store = registry.BindMetadata("default");
if (store is not null)
{
    await using (store)
    {
        await store.ReadyAsync(cancellationToken);
        Console.WriteLine($"default sink: {store.DefaultAudioSink?.NameValue}");
    }
}
```

A session with no session manager running has no `default` store at all, which is why that call
returns null rather than throwing.

## One clock, and the quantum

Every node in a connected group runs off one clock, driven by one node in that group (usually the
hardware). The graph wakes up, hands every node the same number of frames - the **quantum** - and
each node processes them. A cycle that is late is an **xrun**, and it is audible.

Two things follow:

- Work in a process callback must not allocate, lock, log or block. It runs on the realtime thread
  and the whole driver group is waiting for it.
- Two streams on one connection share that clock, which is what makes audio and video from your
  process line up. [streaming.md](streaming.md) covers the timestamps.

## Your process: one context, one loop

`PipeWireContext` is one connection and one event loop thread. Everything the daemon says arrives
on that thread, and every callback this library raises runs there unless it says otherwise.

```csharp
await using var ctx = new PipeWireContext();
await ctx.StartAsync(cancellationToken);
```

Native calls that have to be serialised with the loop take a lock for the duration
(`ctx.Lock()`), and disposal waits for every live scope, so a callback cannot outlive the loop it
runs on. You rarely need the lock yourself - the library takes it - but it is there when you call
into native code.

## Permissions

A client sees objects the daemon has granted it permission to see. On an ordinary desktop session
that is everything. In a sandbox, or behind a portal, it is a subset, and objects you have no read
permission for are simply not in the registry rather than erroring when touched.

## Where to go next

| | |
|---|---|
| [quickstart.md](quickstart.md) | the same ground with a project in front of you |
| [choosing-a-type.md](choosing-a-type.md) | which type in this library to reach for, per task |
| [threading-and-lifetimes.md](threading-and-lifetimes.md) | which thread a callback is on, and what disposal does |
| [parameters-and-pods.md](parameters-and-pods.md) | parameters are SPA pods; how to read and write them |
| [troubleshooting.md](troubleshooting.md) | when something silently does not happen |
| [pipewire-roles.md](pipewire-roles.md) | consuming an object versus *being* one, and what serving costs |
| [streaming.md](streaming.md) | frame timing, A/V sync, DMA-BUF, explicit sync |
| [running-tests.md](running-tests.md) | the test categories and what each needs |
| [upstream Overview](https://docs.pipewire.org/page_overview.html) | PipeWire's own architecture page |
| [upstream tutorial](https://docs.pipewire.org/page_tutorial.html) | the same ground in C |
