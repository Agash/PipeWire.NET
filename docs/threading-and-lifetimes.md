# Threading and lifetimes

The rules that keep a PipeWire client correct. Most bugs in this space do not throw: they produce a
stream that goes quiet, a callback that stops arriving, or a crash minutes later on an unrelated
thread. Worth ten minutes up front.

## There is one loop thread

A `PipeWireContext` owns one event loop on its own thread. Everything the daemon says arrives there,
and **every callback this library raises runs on it**: `FrameReady`, `FillSamples`, `FillFrame`,
`ProcessCallback`, `StateChanged`, the registry's change notifications.

Consequences, in the order people hit them:

- **Do not block in a callback.** Awaiting, locking on something a slow thread holds, or doing IO
  stalls the loop, and while it is stalled the daemon is not being answered.
- **Marshal to your own thread to touch UI or shared state.** Your handler is on a thread you did not
  create.
- **Do not call a blocking library method from inside a callback** where its documentation says it
  waits for the loop. You are the loop.

The realtime callbacks are stricter still.

## The realtime contract

`FillSamples`, `FillFrame` and a filter's `ProcessCallback` run on the data loop, in the middle of a
graph cycle. The whole driver group is waiting for you.

In there, do not: allocate, lock, log, take a `lock`, touch a `Task`, or call anything that might.
A late return is an xrun, and an xrun is audible for every node in the group, not just yours.

The pattern that works is a pre-allocated buffer written by your own thread and read in the callback,
with nothing between them that can block.

```csharp
await using var output = new PipeWireAudioOutput(ctx, "pwdemo_source");

output.FillSamples += (_, samples, sampleRate, channels, format) =>
{
    // Copy from something already in memory; no allocation, no locks, no logging.
    samples.Clear();
    return samples.Length;
};

output.Connect();
```

An exception thrown in a callback cannot cross back into native code, so this library catches it
rather than letting it take the process down. Left at that, **a handler that throws would look
exactly like a handler that did nothing** - which is why every type that runs your code records what
it caught:

```csharp
await using var output = new PipeWireAudioOutput(ctx, "pwdemo_source");

// ... later, from your own loop rather than from the callback:
if (output.LastProcessError is { } error)
    Console.WriteLine($"the fill handler threw {output.ProcessErrorCount} times, last: {error}");
```

`LastProcessError` and `ProcessErrorCount` are on all four stream types and on `PipeWireFilter`.
Nothing is logged, because logging on the realtime thread is itself an xrun - read them from your
own loop. `PipeWireNodeProvider` adds `LastCallbackError` beside them, because for a node you
implement there are two different failures: your handler throwing, and this library failing to
answer the graph.

`PipeWireFilter.SetError` is the other direction - telling the graph you cannot go on.

## Spans are borrowed, always

A frame handed to `FrameReady` is a `ref struct` over the daemon's mapped buffer. It is valid for
the duration of your handler and not one instruction longer - after you return, the buffer goes back
to the pool and may be overwritten by the next cycle.

```csharp
await using var capture = new PipeWireVideoCapture(ctx);

capture.FrameReady += (_, frame) =>
{
    // Fine: read it here.
    int first = frame.Pixels.Length > 0 ? frame.Pixels[0] : 0;

    // Keep it: copy out. Clone gives you an owned frame you can queue.
    OwnedVideoFrame owned = frame.Clone();
    Console.WriteLine($"{owned.Width}x{owned.Height} {first}");
};
```

The same is true in the other direction: the span `FillFrame` gives you is the daemon's buffer, which
is why writing it is the only copy in the path.

## Disposal, and what it actually does

Everything that holds a daemon object implements both `IDisposable` and `IAsyncDisposable`, and the
two do the same work - none of this disposal awaits anything, so pick whichever idiom suits the
code you are in. Dispose in reverse order of creation: streams and proxies before the registry, the
registry before the context.

```csharp
await using var ctx = new PipeWireContext("pwdemo");
await ctx.StartAsync();

await using var registry = new PipeWireRegistry(ctx);
await using PipeWireNodeProxy node = registry.BindNode(nodeId);
```

Two things to know about what disposal is for:

- **Dropping a reference is not disposal.** The daemon holds callbacks that refer back into your
  process. An instance you simply stop using is collected and quietly stops answering, with no
  error anywhere. Disposal is what withdraws the object.
- **Disposing the context waits.** Native calls that must serialise with the loop take a scope for
  their duration, and disposal waits for every live scope, so a callback cannot outlive the loop it
  runs on. That wait is the reason disposal is not instant.

## Objects that outlive you, on purpose

By default the daemon destroys what you created when your connection goes away. `WithLinger` opts
out, which is right for a routing setup meant to survive a restart of your app and wrong for
anything else:

```csharp
PipeWireNode node = await registry.CreateVirtualSink("Monitor mix")
                                  .WithLinger()
                                  .ExecuteAsync(cancellationToken);
```

A lingering object cannot be removed by disconnecting - not by disposing the registry or the context
either. Destroy it explicitly with `registry.DestroyGlobalAsync`. `registry.LingeringIds` lists what
you left behind, so nothing has to be remembered by hand.

## When the object you are holding goes away

A graph moves underneath you: the node you bound a moment ago can be removed by its owner, by the
session manager, or because the device it belonged to was unplugged. Every bound proxy tells you:

```csharp
await using PipeWireNodeProxy node = registry.BindNode(nodeId);

node.Removed += () => Console.WriteLine("the daemon destroyed the object behind this proxy");

if (node.IsRemoved)
{
    // The proxy is still safe to hold and to dispose. What it points at is gone, so calls on it
    // will not reach anything.
}
```

`Removed` is raised once, on the loop thread, and `IsRemoved` answers the same question for code
that was not listening at the time. Both come from `pw_proxy_events.removed`, so they fire for any
reason the object disappeared and not only the ones you caused.

This is not the same as watching the registry. A registry watcher hears about every global going
away; `Removed` is about the one object you hold, and it is the only signal a caller who never built
a registry snapshot gets.

## Why this library removes listeners before destroying

Not something you have to do, but worth knowing, because it explains a class of crash if you ever go
around the library into native calls of your own.

| call | unlinks its listeners? | so the caller must |
|---|---|---|
| `pw_stream_destroy` | yes | nothing |
| `pw_filter_destroy` | yes | nothing |
| `pw_proxy_destroy` | **no** - marks it a zombie, then unrefs | remove the hook first, under the loop lock |
| `pw_core_disconnect` | **no** - frees the core outright | remove the core listener *before* calling |

A proxy routinely outlives its own destroy, because the path takes another reference. Getting it
wrong writes into freed memory and surfaces much later, on whichever thread next allocates. Upstream
agrees: `pw-dump` and WirePlumber both remove before destroying. This library does it for you.

## Serving needs its own context when it is busy

A served object owes the daemon prompt answers. Sharing a context between heavy consuming work and a
served object makes your own slow work into that object's latency. WirePlumber hit this and retired
its shared export core because of it. One small metadata store beside other work is fine; a busy
served node deserves its own `PipeWireContext`. See [serving.md](serving.md).
