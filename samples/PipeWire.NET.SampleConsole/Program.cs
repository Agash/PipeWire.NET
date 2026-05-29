using System.Runtime.Versioning;
using PipeWire.NET;

// Console demo:
//   1. Connect to the local PipeWire daemon
//   2. Enumerate visible nodes via the registry (cameras, virtual cameras, mics)
//   3. Pick the first video source and stream its frames for 10 seconds
//   4. Log FPS, byte rate, and the negotiated format/resolution
//
// Requires:
//   - Linux with libpipewire-0.3-0 installed and the daemon running
//     (pipewire.service + wireplumber.service)

if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("This sample is Linux-only (PipeWire is a Linux daemon).");
    return 1;
}

return await RunAsync().ConfigureAwait(false);

[SupportedOSPlatform("linux")]
static async Task<int> RunAsync()
{
    Console.WriteLine("Initialising PipeWire context...");
    await using var ctx = new PipeWireContext();
    await ctx.StartAsync().ConfigureAwait(false);
    Console.WriteLine("  connected to daemon.");

    // - Discover sources -
    Console.WriteLine("Enumerating sources via the registry...");
    await using var registry = new PipeWireRegistry(ctx);

    using var initCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    try
    {
        await registry.WaitForInitialEnumerationAsync(cancellationToken: initCts.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("  (no globals reported in 2s - graph may be empty)");
    }

    var sources = registry.Sources;
    Console.WriteLine($"  found {sources.Count} node(s):");
    foreach (var s in sources)
        Console.WriteLine($"    id={s.NodeId,4}  class={s.MediaClass ?? "?",-24}  {s.Description ?? s.NodeName ?? "<no name>"}");

    var pick = sources.FirstOrDefault(s => s.IsVideoSource && (s.NodeName?.Contains("gst") ?? true));
    if (pick is null)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("No video source found. Plug in a camera or run `pw-loopback` first.");
        return 1;
    }

    // - Stream from the picked source -
    Console.WriteLine();
    Console.WriteLine($"Streaming from id={pick.NodeId} ({pick.Description ?? pick.NodeName})...");

    await using var stream = new PipeWireVideoCapture(ctx, name: "PipeWire.NET.SampleConsole");

    int frameCount = 0;
    long byteCount = 0;
    PipeWireStreamState lastState = PipeWireStreamState.Unconnected;

    stream.StateChanged += (_, oldState, newState) =>
    {
        lastState = newState;
        Console.WriteLine($"  state: {oldState} -> {newState}");
    };

    stream.FrameReady += (sender, frame) =>
    {
        Interlocked.Increment(ref frameCount);
        Interlocked.Add(ref byteCount, frame.Data.Length);
    };

    stream.Connect(pick);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    var deadline = DateTime.UtcNow.AddSeconds(10);
    var lastTick = DateTime.UtcNow;
    int lastCount = 0;
    long lastBytes = 0;

    while (DateTime.UtcNow < deadline && !cts.IsCancellationRequested)
    {
        try { await Task.Delay(1000, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { break; }

        int currentCount = Volatile.Read(ref frameCount);
        long currentBytes = Volatile.Read(ref byteCount);
        var now = DateTime.UtcNow;
        double seconds = (now - lastTick).TotalSeconds;
        double fps     = (currentCount - lastCount) / seconds;
        double mbps    = (currentBytes - lastBytes) / 1024.0 / 1024.0 / seconds;

        Console.WriteLine(
            $"  {now:HH:mm:ss}  state={lastState,-12}  frames={currentCount,5}  " +
            $"fps={fps,5:F1}  rate={mbps,6:F2} MB/s");

        lastTick  = now;
        lastCount = currentCount;
        lastBytes = currentBytes;
    }

    Console.WriteLine();
    Console.WriteLine($"Done. Total frames received: {frameCount}, total bytes: {byteCount:N0}");
    return 0;
}
