using System.Diagnostics;

namespace PipeWire.NET;

/// <summary>
/// The <see cref="ActivitySource"/> this library's multi-step operations report on.
/// </summary>
/// <remarks>
/// <para>
/// Logging says what happened; this says what it was part of. Connecting a stream, negotiating a
/// format and creating a graph object are each several round trips to another process, and when one
/// of them stalls the useful question is which step and how long the ones before it took. A log line
/// per step cannot answer that once two streams are connecting at once.
/// </para>
/// <para>
/// Nothing is emitted unless a listener is attached, so an application that does not care pays a
/// null check. Subscribe with <c>ActivitySource.AddActivityListener</c> or by adding
/// <see cref="Name"/> to an OpenTelemetry tracer provider.
/// </para>
/// </remarks>
public static class PipeWireDiagnostics
{
    /// <summary>The activity source name: <c>PipeWire.NET</c>.</summary>
    public const string Name = "PipeWire.NET";

    /// <summary>The source itself, for hosts that want to attach a listener directly.</summary>
    public static ActivitySource Source { get; } = new(Name, ThisAssembly());

    private static string ThisAssembly() =>
        typeof(PipeWireDiagnostics).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
