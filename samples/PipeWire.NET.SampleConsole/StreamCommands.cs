using System.Runtime.Versioning;
using PipeWire.NET.Media;
using PipeWire.NET.Media.Streams;
using PipeWire.NET;

namespace PipeWire.NET.SampleConsole;

// Capture with counters, not files: connect to the default route, count frames and bytes per
// second, then say what was negotiated. A missing source is a hint, not a crash.
[SupportedOSPlatform("linux")]
internal static class StreamCommands
{
    public static async Task<int> CaptureAudioAsync(string[] args, CancellationToken cancellationToken)
    {
        int seconds = Program.Seconds(args, 5);

        await using var session = await Session.ConnectAsync(
            "sample-capture-audio", cancellationToken).ConfigureAwait(false);

        await using var capture = new PipeWireAudioCapture(session.Context, "sample_capture");

        long frames = 0;
        long bytes = 0;
        int rate = 0;
        int channels = 0;
        int format = (int)AudioSampleFormat.Unknown;

        capture.FrameReady += (_, frame) =>
        {
            Interlocked.Increment(ref frames);
            Interlocked.Add(ref bytes, frame.Samples.Length);
            Volatile.Write(ref rate, frame.SampleRate);
            Volatile.Write(ref channels, frame.Channels);
            Volatile.Write(ref format, (int)frame.Format);
        };

        var states = new StateLogger();
        capture.StateChanged += (_, oldState, newState) => states.Log(oldState, newState);

        capture.Connect();

        long lastFrames = 0;
        long lastBytes = 0;
        for (int left = seconds; left > 0; left--)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return 1;
            }

            long nowFrames = Interlocked.Read(ref frames);
            long nowBytes = Interlocked.Read(ref bytes);
            Console.WriteLine($"  audio: {nowFrames - lastFrames}/s, " +
                $"{(nowBytes - lastBytes) / 1024.0:F1} KiB/s");
            lastFrames = nowFrames;
            lastBytes = nowBytes;
        }

        long totalFrames = Interlocked.Read(ref frames);
        long totalBytes = Interlocked.Read(ref bytes);
        if (totalFrames == 0)
        {
            Console.WriteLine("No audio arrived. Is a source routed to the default?");
            return 1;
        }

        Console.WriteLine($"Negotiated: {Volatile.Read(ref rate)} Hz, " +
            $"{Volatile.Read(ref channels)}ch, {(AudioSampleFormat)Volatile.Read(ref format)}.");
        Console.WriteLine($"Total: {totalFrames} frames, {totalBytes:N0} bytes.");
        return 0;
    }

    public static async Task<int> CaptureVideoAsync(string[] args, CancellationToken cancellationToken)
    {
        int seconds = Program.Seconds(args, 5);

        await using var session = await Session.ConnectAsync(
            "sample-capture-video", cancellationToken).ConfigureAwait(false);

        await using var capture = new PipeWireVideoCapture(session.Context, "sample_capture");

        long frames = 0;
        long bytes = 0;
        int width = 0;
        int height = 0;
        int format = (int)PixelFormat.Unknown;

        capture.FrameReady += (_, frame) =>
        {
            Interlocked.Increment(ref frames);
            Interlocked.Add(ref bytes, frame.Data.Length);
            Volatile.Write(ref width, frame.Width);
            Volatile.Write(ref height, frame.Height);
            Volatile.Write(ref format, (int)frame.Format);
        };

        var states = new StateLogger();
        capture.StateChanged += (_, oldState, newState) => states.Log(oldState, newState);

        capture.Connect();

        long lastFrames = 0;
        long lastBytes = 0;
        for (int left = seconds; left > 0; left--)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return 1;
            }

            long nowFrames = Interlocked.Read(ref frames);
            long nowBytes = Interlocked.Read(ref bytes);
            Console.WriteLine($"  video: {nowFrames - lastFrames} fps, " +
                $"{(nowBytes - lastBytes) / 1024.0 / 1024.0:F2} MiB/s");
            lastFrames = nowFrames;
            lastBytes = nowBytes;
        }

        long totalFrames = Interlocked.Read(ref frames);
        long totalBytes = Interlocked.Read(ref bytes);
        if (totalFrames == 0)
        {
            Console.WriteLine("No video arrived. Plug in a camera or run `pw-loopback` first.");
            return 1;
        }

        Console.WriteLine($"Negotiated: {Volatile.Read(ref width)}x{Volatile.Read(ref height)} " +
            $"{(PixelFormat)Volatile.Read(ref format)}.");
        Console.WriteLine($"Total: {totalFrames} frames, {totalBytes:N0} bytes.");
        return 0;
    }

    // State transitions are the interesting log until a stream starts failing: a source that
    // never appears retries audibly, so the repeats get one line and then silence.
    private sealed class StateLogger
    {
        private int _errors;

        public void Log(PipeWireStreamState oldState, PipeWireStreamState newState)
        {
            if (newState == PipeWireStreamState.Error)
            {
                int count = Interlocked.Increment(ref _errors);
                if (count == 3)
                    Console.WriteLine("  (further errors suppressed)");
                if (count >= 3)
                    return;
            }

            Console.WriteLine($"  state: {oldState} -> {newState}");
        }
    }
}
