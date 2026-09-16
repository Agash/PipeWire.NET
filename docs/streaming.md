# Streaming: timing, sync and zero copy

What a frame carries, how two streams line up, and how to keep pixels on the GPU. The types are in
`PipeWire.NET.Media`; [choosing-a-type.md](choosing-a-type.md) says when to reach for a stream at
all rather than for a filter or a node provider.

## Timing and A/V sync

Every stream runs off one graph clock. Each frame carries four times, all in nanoseconds:

- `PresentationTimestampNs`: the producer's timestamp from the buffer header (`spa_meta_header.pts`), in the producer's clock. It crosses the graph intact for video, so a video consumer aligns on it. No audio converter or mixer copies the header, so audio arrives without one and this is null.
- `QueuedTimeNs`: the graph cycle time the buffer was queued in (`pw_buffer.time`, CLOCK_MONOTONIC). This is what an audio consumer aligns on, and what GStreamer's pipewiresrc falls back to when there is no header.
- `GraphTimeNs`: the graph time of the cycle that delivered the frame (`pw_time.now`). One value per cycle, so frames delivered together share it.
- `StreamPositionNs` and `DelayNs`: the stream's media position and its latency, for sample-accurate timestamping.

Audio and video from one producer meet on one timeline when the video is stamped in the graph's clock, which is what the outputs do unless you set `NextPresentationTimestampNs` yourself.

## Zero copy

On capture, `frame.Pixels` points straight into the daemon's mapped buffer, so reading is free. Capture also accepts DMA-BUF buffers, so a GPU source can hand frames over without touching the CPU; `frame.BufferType` and `frame.Fd` expose the descriptor for GPU import.

On publish, `FillFrame` and `FillSamples` give you a span over the daemon's buffer, so you write the frame once with no intermediate copy.

For a fully GPU-resident publish, `PipeWireVideoOutput.ConnectDmaBuf(modifiers)` advertises a set of DRM format modifiers, negotiates one with the consumer, and backs the stream with DMA-BUF buffers you own. Allocate your GPU surfaces in the `AllocateDmaBuf` callback (export each once, e.g. via `vkGetMemoryFdKHR`) and write the chosen buffer in `FillDmaBuf`; `ReleaseDmaBuf` tears them down. The producer can self-pace with `TriggerProcess`, and `NodeId` lets a consumer target the node directly.

On a machine with more than one GPU, pass `DmaBufDeviceOffer`s (a `DrmDevice` and the modifiers it can use) to `ConnectDmaBuf` or to the capture's `Connect(deviceOffers:)` instead of bare modifiers. Both ends then negotiate which device the buffers live on, as PipeWire's device-ID negotiation does; `NegotiatedDevice` reports the result and `AllocateDmaBuf` is handed it. A peer that does not negotiate still streams, with the device left undefined.

## Renegotiating against a GStreamer producer

`RequestFormat` asks the peer to re-settle the link mid-stream. Against `pipewiresink` that wedges
the producer roughly a quarter of the time, and it does not recover.

The cause is in `pipewiresink`, not here: its `on_param_changed` blocks the PipeWire thread loop
until the gst buffer pool goes active again (`gstpipewiresink.c`), and the pool is only reactivated
from the streaming thread, which needs the loop lock the blocked callback is holding. What it looks
like from this side is a stream that goes to `Paused` and either stays there or returns to
`Streaming` and never delivers again. Measured on PipeWire 1.6.8: 4 stalls in 15 runs, with the
sink's `pipewire-main-l` thread parked in `__futex_wait` rather than `epoll_wait`, after logging the
removal of every buffer and nothing since.

This side stays healthy through it - the stream does not error, teardown completes, and the
connection keeps serving - which is what
`ARenegotiationAGstProducerMayNotAnswer_LeavesThisSideUsable` pins. Until upstream fixes it, treat
mid-stream renegotiation as something to do against producers you control.
