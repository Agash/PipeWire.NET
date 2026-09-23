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
public sealed partial class PipeWireLinkProxy : IDisposable, IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    private BoundProxy? _bound;
    private volatile bool _disposed;

    // Written on the loop thread inside the info callback, read from anywhere.
    private volatile LinkSnapshot _snapshot = new(PipeWireLinkState.Init, null, 0, 0, 0, 0);

    private PipeWireLinkProxy(uint id, ILogger logger)
    {
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
        uint OutputNodeId,
        uint OutputPortId,
        uint InputNodeId,
        uint InputPortId
    );

    /// <summary>The global id of the link this is bound to.</summary>
    public uint LinkId { get; }

    /// <summary>
    /// Where an <c>info</c> event's properties go, set by the registry that made this object.
    /// </summary>
    /// <remarks>
    /// The registry global carries the four endpoint ids and little else; the rest of a link's
    /// properties only arrive here.
    /// </remarks>
    internal Action<uint, PipeWireProperties>? PropertiesObserved { get; set; }

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
    public uint OutputNodeId => _snapshot.OutputNodeId;

    /// <summary>The port the data leaves.</summary>
    public uint OutputPortId => _snapshot.OutputPortId;

    /// <summary>The node the data arrives at.</summary>
    public uint InputNodeId => _snapshot.InputNodeId;

    /// <summary>The port the data arrives at.</summary>
    public uint InputPortId => _snapshot.InputPortId;

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
    public event Action<PipeWireLinkProxy>? StateChanged;

    internal static unsafe PipeWireLinkProxy Bind(
        PipeWireContext ctx,
        pw_registry* registry,
        uint id,
        uint version,
        ILogger logger,
        Action<uint, PipeWireProperties>? propertiesObserved = null
    )
    {
        // The observer is in place before the proxy is bound: the first info after a bind is the
        // only one that carries the object's properties (later ones set no PROPS in their change
        // mask, so their dictionary arrives empty), and it can arrive the moment the bind is sent.
        // Assigned after Bind returned, a fast daemon's first info found no observer and the
        // properties never reached the registry.
        var control = new PipeWireLinkProxy(id, logger) { PropertiesObserved = propertiesObserved };
        control._bound = BoundProxy.Bind(
            ctx,
            registry,
            id,
            PipeWireKeys.PW_TYPE_INTERFACE_Link,
            version,
            NativeConstants.PW_VERSION_LINK,
            sizeof(pw_link_events),
            events =>
            {
                var table = (pw_link_events*)events;
                table->version = NativeConstants.PW_VERSION_LINK_EVENTS;
                table->info = &OnInfo;
            },
            static (proxy, hook, events, data) =>
                Native.pw_link_add_listener(
                    (pw_link*)proxy,
                    (spa_hook*)hook,
                    (pw_link_events*)events,
                    (void*)data
                ),
            control
        );

        control._bound.Removed = control.RaiseRemoved;

        return control;
    }

    /// <summary>Raised on the loop thread when the daemon destroys the object behind this proxy.</summary>
    /// <remarks>
    /// A bound object can go at any time. The proxy survives as a zombie and every call through it
    /// fails from here on, so this is the signal to stop using it. A caller watching the whole graph
    /// sees the same thing through the registry; one holding only this proxy has nothing else.
    /// </remarks>
    public event Action? Removed;

    /// <summary>Whether the daemon has destroyed the object behind this proxy.</summary>
    public bool IsRemoved => _bound?.IsRemoved ?? false;

    private void RaiseRemoved()
    {
        Action? handler = Removed;
        if (handler is null)
            return;

        // A native callback frame, so nothing may escape it.
        try
        {
            handler();
        }
        catch (Exception)
        { /* a subscriber that throws must not reach the daemon */
        }
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
        CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)]
    )]
    private static unsafe void OnInfo(void* data, pw_link_info* info)
    {
        // An exception escaping a reverse P/Invoke aborts the process, so nothing below may throw.
        try
        {
            if (data is null || info is null)
                return;
            if (GCHandle.FromIntPtr((nint)data).Target is not PipeWireLinkProxy self)
                return;
            if (self._disposed)
                return;

            string? error = info->error is null ? null : DaemonText.String(info->error);

            // The error only means anything in the error state. Carrying a stale one alongside a
            // recovered link would have a caller reporting a failure that is over.
            var snapshot = new LinkSnapshot(
                info->state,
                info->state == PipeWireLinkState.Error ? error : null,
                info->output_node_id,
                info->output_port_id,
                info->input_node_id,
                info->input_port_id
            );

            self._snapshot = snapshot;
            self._ready.TrySetResult();

            if (self.PropertiesObserved is { } observer && info->props is not null)
                observer(self.LinkId, PipeWireProperties.From(info->props));

            self.LogState(self.LinkId, snapshot.State, snapshot.Error);
            SafeCallback.Raise(self.StateChanged, h => h(self), ex => self.LogHandlerFaulted(ex));
        }
        catch (Exception ex)
        {
            // Reached only if the marshalling above fails, which would mean the daemon sent
            // something the struct does not describe.
            try
            {
                Console.Error.WriteLine($"PipeWire.NET link info callback faulted: {ex}");
            }
            catch (IOException)
            { /* Deliberately not logged: nothing left that could report it. */
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
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

    [LoggerMessage(
        EventId = 34500,
        Level = LogLevel.Debug,
        Message = "link {LinkId} is {State}{Error}"
    )]
    private partial void LogState(uint linkId, PipeWireLinkState state, string? error);

    [LoggerMessage(
        EventId = 34501,
        Level = LogLevel.Warning,
        Message = "a link state handler threw"
    )]
    private partial void LogHandlerFaulted(Exception exception);
}
