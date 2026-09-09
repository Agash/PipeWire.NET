using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
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
        // The profiler's protocol marshal lives in a module the client loads for itself; without
        // it pw_proxy_new has nothing to build the proxy with and the bind returns null. Neither
        // client.conf nor the daemon supplies it, which is why pw-top loads it by name too.
        EnsureProfilerModule(ctx);

        var reader = new PipeWireProfilerReader(ctx, id, logger);
        reader._bound = BoundProxy.Bind(
            ctx, registry, id, Native.PW_TYPE_INTERFACE_PROFILER, version, Native.PW_VERSION_PROFILER,
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

    /// <summary>Loads the profiler extension module into this context, once.</summary>
    /// <remarks>Loading it twice is harmless; the module refcounts.</remarks>
    private static unsafe void EnsureProfilerModule(PipeWireContext ctx)
    {
        ReadOnlySpan<byte> name = "libpipewire-module-profiler\0"u8;
        using (ctx.Lock())
        {
            fixed (byte* n = name)
                _ = Native.pw_context_load_module(ctx.ContextHandle, (sbyte*)n, null, null);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnProfileCallback(void* data, spa_pod* pod)
    {
        if (data is null || pod is null) return;
        PipeWireProfilerReader? self;
        try
        {
            if (GCHandle.FromIntPtr((nint)data).Target is not PipeWireProfilerReader found) return;
            self = found;
        }
        catch (Exception)
        {
            // A freed handle throws out of the lookup, and this is a native frame.
            return;
        }

        if (self._disposed) return;

        try
        {
            if (TryParseReport(pod, out ImmutableArray<SpaObject> reports, out int size))
            {
                foreach (SpaObject report in reports)
                    self.Raise(report);
            }
            else
            {
                self.LogUnparsedReport(size);
            }
        }
        catch (Exception ex)
        {
            // A native callback frame: an escaping exception aborts the process rather than
            // unwinding into a catch.
            self.LogProfileDispatchFailed(ex);
        }
    }

    /// <summary>Reads the Profiler objects out of one report pod.</summary>
    /// <param name="pod">The pod the daemon handed the callback.</param>
    /// <param name="reports">The objects it carried, when this returns true.</param>
    /// <param name="size">The pod's total size, for diagnostics when this returns false.</param>
    /// <returns>Whether <paramref name="pod"/> parsed and carried at least one object.</returns>
    /// <remarks>
    /// A report is a struct of Profiler objects, one per driver in the cycle, and a bare object is
    /// accepted too. Split from the callback so hostile pods are testable without a daemon: the
    /// size in the header is the daemon's word, and a wrong one must refuse rather than span.
    /// </remarks>
    internal static unsafe bool TryParseReport(
        spa_pod* pod,
        out ImmutableArray<SpaObject> reports,
        out int size)
    {
        reports = [];
        size = 0;

        if (pod is null) return false;

        // Checked before the cast. A size near uint.MaxValue casts to a negative length, and
        // the span constructor is the one place that would not tell us so.
        if (pod->size > int.MaxValue - 8)
        {
            size = int.MaxValue;
            return false;
        }

        size = 8 + (int)pod->size;
        var bytes = new ReadOnlySpan<byte>(pod, size);

        if (!SpaPod.TryParse(bytes, out SpaValue? value)) return false;

        switch (value)
        {
            case SpaObject single:
                reports = [single];
                return true;

            case SpaStruct outer:
                ImmutableArray<SpaObject> found =
                    [.. outer.Fields.OfType<SpaObject>()];
                reports = found;
                return found.Length > 0;

            default:
                return false;
        }
    }

    private void Raise(SpaObject report)
    {
        SafeCallback.Raise(ProfileReceived, h => h(this, report), ex => LogHandlerFaulted(Id, ex));
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
