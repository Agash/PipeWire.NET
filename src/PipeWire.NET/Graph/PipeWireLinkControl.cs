using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Graph;

/// <summary>
/// A link bound for watching what it is doing.
/// </summary>
/// <remarks>
/// <para>
/// The registry says a link exists and which ports it joins. It does not say whether anything is
/// flowing through it. A link spends its life moving between negotiating, allocating, paused and
/// active, and it can end up unlinked or in error with a reason attached, and none of that reaches
/// a client that only reads globals. A patchbay showing every link the same colour whether it is
/// carrying audio or failed to negotiate is the visible consequence.
/// </para>
/// <para>
/// The state arrives on the link's own <c>info</c> event, which needs the proxy bound, so this is
/// opt-in per link rather than something the graph carries for every link at once: a session with
/// hundreds of links would otherwise pay a proxy for each one to answer a question about a few.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class PipeWireLinkControl : IDisposable, IAsyncDisposable
{
    private readonly PipeWireContext _ctx;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private BoundProxy? _bound;
    private volatile bool _disposed;

    // Written on the loop thread inside the info callback, read from anywhere.
    private volatile LinkSnapshot _snapshot = new(PipeWireLinkState.Init, null, 0, 0, 0, 0);

    private PipeWireLinkControl(PipeWireContext ctx, uint id, ILogger logger)
    {
        _ctx = ctx;
        LinkId = id;
        _logger = logger;
    }

    /// <summary>Everything the last info event said, as one value.</summary>
    /// <remarks>
    /// One record rather than separate fields, so a reader cannot see a new state beside the
    /// previous error: the two are only meaningful together.
    /// </remarks>
    private sealed record LinkSnapshot(
        PipeWireLinkState State,
        string? Error,
        uint OutputNode,
        uint OutputPort,
        uint InputNode,
        uint InputPort);

    /// <summary>The global id of the link this is bound to.</summary>
    public uint LinkId { get; }

    /// <summary>What the link is currently doing.</summary>
    /// <remarks>
    /// <see cref="PipeWireLinkState.Init"/> until the first info event arrives, which is what
    /// <see cref="ReadyAsync"/> waits for.
    /// </remarks>
    public PipeWireLinkState State => _snapshot.State;

    /// <summary>
    /// Why the link failed, or <see langword="null"/> when <see cref="State"/> is not
    /// <see cref="PipeWireLinkState.Error"/>.
    /// </summary>
    public string? Error => _snapshot.Error;

    /// <summary>The node the data leaves.</summary>
    public uint OutputNode => _snapshot.OutputNode;

    /// <summary>The port the data leaves.</summary>
    public uint OutputPort => _snapshot.OutputPort;

    /// <summary>The node the data arrives at.</summary>
    public uint InputNode => _snapshot.InputNode;

    /// <summary>The port the data arrives at.</summary>
    public uint InputPort => _snapshot.InputPort;

    /// <summary>True once the link is carrying data.</summary>
    public bool IsActive => State == PipeWireLinkState.Active;

    /// <summary>Raised whenever the daemon reports a change, on the loop thread.</summary>
    /// <remarks>
    /// <para>
    /// The first report can arrive before a subscription is attached, because binding starts the
    /// daemon talking and there is no point at which a handler could be in place first. So this
    /// carries the changes, and <see cref="ReadyAsync"/> followed by reading <see cref="State"/>
    /// carries the starting point. A subscriber that wants both waits for ready, reads, and treats
    /// events as deltas from there.
    /// </para>
    /// <para>
    /// Do not wait for anything inside the handler: it runs on the thread that would deliver what
    /// is being waited for.
    /// </para>
    /// </remarks>
    public event Action<PipeWireLinkControl>? StateChanged;

    internal static unsafe PipeWireLinkControl Bind(
        PipeWireContext ctx, pw_registry* registry, uint id, uint version, ILogger logger)
    {
        var control = new PipeWireLinkControl(ctx, id, logger);
        control._bound = BoundProxy.Bind(
            ctx, registry, id, Native.PW_TYPE_INTERFACE_LINK, version, Native.PW_VERSION_LINK,
            sizeof(pw_link_events),
            events =>
            {
                var table = (pw_link_events*)events;
                table->version = Native.PW_VERSION_LINK_EVENTS;
                table->info = &OnInfo;
            },
            static (proxy, hook, events, data) => Native.pw_link_add_listener(
                (pw_link*)proxy, (spa_hook*)hook, (pw_link_events*)events, (void*)data),
            control);

        return control;
    }

    /// <summary>Waits for the daemon's first report about this link.</summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// Reading <see cref="State"/> before this completes reports <see cref="PipeWireLinkState.Init"/>
    /// whatever the link is really doing, because nothing has been said about it yet.
    /// </remarks>
    public Task ReadyAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _ready.Task.WaitAsync(cancellationToken);
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(
        CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnInfo(void* data, pw_link_info* info)
    {
        // An exception escaping a reverse P/Invoke aborts the process, so nothing below may throw.
        try
        {
            if (data is null || info is null) return;
            if (GCHandle.FromIntPtr((nint)data).Target is not PipeWireLinkControl self) return;
            if (self._disposed) return;

            string? error = info->error is null
                ? null
                : DaemonText.String(info->error);

            // The error only means anything in the error state. Carrying a stale one alongside a
            // recovered link would have a caller reporting a failure that is over.
            var snapshot = new LinkSnapshot(
                info->state,
                info->state == PipeWireLinkState.Error ? error : null,
                info->output_node_id,
                info->output_port_id,
                info->input_node_id,
                info->input_port_id);

            self._snapshot = snapshot;
            self._ready.TrySetResult();

            self.LogState(self.LinkId, snapshot.State, snapshot.Error);
            SafeCallback.Raise(self.StateChanged, h => h(self), ex => self.LogHandlerFaulted(ex));
        }
        catch (Exception ex)
        {
            // Reached only if the marshalling above fails, which would mean the daemon sent
            // something the struct does not describe.
            try { Console.Error.WriteLine($"PipeWire.NET link info callback faulted: {ex}"); }
            catch (IOException) { /* Deliberately not logged: nothing left that could report it. */ }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _ready.TrySetCanceled();
        _bound?.Dispose();
        _bound = null;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(EventId = 34300, Level = LogLevel.Debug,
        Message = "link {LinkId} is {State}{Error}")]
    private partial void LogState(uint linkId, PipeWireLinkState state, string? error);

    [LoggerMessage(EventId = 34301, Level = LogLevel.Warning,
        Message = "a link state handler threw")]
    private partial void LogHandlerFaulted(Exception exception);
}
