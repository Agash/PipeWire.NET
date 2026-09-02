using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Graph;

/// <summary>
/// Receives the daemon's profiling reports.
/// </summary>
/// <remarks>
/// Each report carries the driver's clock, the quantum, and per-node timings for the cycle. The
/// daemon serves only the first client to subscribe, so hold this no longer than needed.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class PipeWireProfilerReader : IDisposable, IAsyncDisposable
{
    private readonly PipeWireContext _ctx;
    private readonly ILogger _logger;
    private BoundProxy? _bound;
    private volatile bool _disposed;

    private PipeWireProfilerReader(PipeWireContext ctx, uint id, ILogger logger)
    {
        _ctx = ctx;
        Id = id;
        _logger = logger;
    }

    /// <summary>The profiler's global id.</summary>
    public uint Id { get; }

    /// <summary>
    /// Raised for each report, on the loop thread.
    /// </summary>
    /// <remarks>
    /// The report as the daemon sent it. Its shape changes between versions, so it is not modelled
    /// further; read the fields you need out of the object.
    /// </remarks>
    public event Action<PipeWireProfilerReader, SpaObject>? ProfileReceived;

    internal static unsafe PipeWireProfilerReader Bind(
        PipeWireContext ctx, pw_registry* registry, uint id, uint version, ILogger logger)
    {
        var reader = new PipeWireProfilerReader(ctx, id, logger);
        reader._bound = BoundProxy.Bind(
            ctx, registry, id, Native.PW_TYPE_INTERFACE_PROFILER, version,
            sizeof(pw_profiler_events),
            events =>
            {
                var table = (pw_profiler_events*)events;
                table->version = Native.PW_VERSION_PROFILER_EVENTS;
                table->profile = &OnProfileCallback;
            },
            (proxy, hook, events, data) => Native.pw_profiler_add_listener(
                (void*)proxy, (spa_hook*)hook, (pw_profiler_events*)events, (void*)data),
            reader);

        return reader;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnProfileCallback(void* data, spa_pod* pod)
    {
        if (data is null || pod is null) return;
        if (GCHandle.FromIntPtr((nint)data).Target is not PipeWireProfilerReader self) return;
        if (self._disposed) return;

        try
        {
            // Checked before the cast. A size near uint.MaxValue casts to a negative length, and
            // the span constructor is the one place that would not tell us so.
            if (pod->size > int.MaxValue - 8)
            {
                self.LogUnparsedReport(int.MaxValue);
                return;
            }

            int size = 8 + (int)pod->size;
            var bytes = new ReadOnlySpan<byte>(pod, size);

            if (SpaPod.TryParse(bytes, out SpaValue? value) && value is SpaObject report)
                self.Raise(report);
            else
                self.LogUnparsedReport(size);
        }
        catch (Exception ex)
        {
            // A native callback frame: an escaping exception aborts the process rather than
            // unwinding into a catch.
            self.LogProfileDispatchFailed(ex);
        }
    }

    private void Raise(SpaObject report)
    {
        Action<PipeWireProfilerReader, SpaObject>? handlers = ProfileReceived;
        if (handlers is null) return;

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PipeWireProfilerReader, SpaObject>)handler)(this, report); }
            catch (Exception ex) { LogHandlerFaulted(Id, ex); }
        }
    }

    /// <inheritdoc/>
    public void Dispose() => DisposeCore();

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        DisposeCore();
        return ValueTask.CompletedTask;
    }

    private void DisposeCore()
    {
        if (_disposed) return;
        _disposed = true;

        _bound?.Dispose();
        _bound = null;
    }

    [LoggerMessage(EventId = 34000, Level = LogLevel.Warning,
                   Message = "a profiler report of {Size} bytes did not parse as an object")]
    private partial void LogUnparsedReport(int size);

    [LoggerMessage(EventId = 34001, Level = LogLevel.Error, Message = "dispatching a profiler report failed")]
    private partial void LogProfileDispatchFailed(Exception ex);

    [LoggerMessage(EventId = 34002, Level = LogLevel.Error,
                   Message = "a ProfileReceived handler for profiler {ProfilerId} threw")]
    private partial void LogHandlerFaulted(uint profilerId, Exception ex);
}
