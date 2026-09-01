using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Generated;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Graph;

/// <summary>
/// Live view of PipeWire nodes (cameras, virtual cameras, screen-capture portals, microphones)
/// discoverable in the local graph.
/// </summary>
/// <remarks>
/// <para>
/// Drives the registry via SPA interface VTBL dispatch (<c>pw_core_get_registry</c> +
/// <c>pw_registry_add_listener</c> are C macros - see <see cref="Native.GetInterface{TMethods}"/>).
/// </para>
/// <para>
/// Lifecycle: construct -> call <see cref="WaitForInitialEnumerationAsync"/> (optional, blocks
/// until the daemon has reported its initial state) -> enumerate <see cref="Sources"/> or
/// subscribe to <see cref="SourceAdded"/>/<see cref="SourceRemoved"/> -> dispose.
/// </para>
/// <para>Events are raised on the PipeWire main-loop thread.</para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class PipeWireRegistry : IAsyncDisposable
{
    internal readonly PipeWireContext _ctx;
    private readonly ILogger _logger;
    internal readonly ConcurrentDictionary<uint, PipeWireNode> _sources = new();
    internal readonly ConcurrentDictionary<uint, PipeWirePort> _ports = new();
    internal readonly ConcurrentDictionary<uint, PipeWireLink> _links = new();

    // Proxies for objects this client created. PipeWire hands ownership to the caller, and
    // pw_proxy_destroy may be called exactly once, so the handle is the only thing that may.
    private readonly ConcurrentDictionary<uint, PipeWireProxyHandle> _ownedProxies = new();

    // Creations that know their id and are waiting for the object to be published. Registered
    // between `bound` and the registry `global`, so the entity is handed over as it is added
    // rather than looked up afterwards, which used to race anything removing it in between.
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<IPipeWireObject>> _awaitingPublish = new();

    private PipeWireGraphSnapshot _current = PipeWireGraphSnapshot.Empty;
    private long _version;

    private unsafe pw_registry*        _registry;
    private unsafe pw_registry_events* _events;
    // Unmanaged, not a field: pw_registry_add_listener retains this pointer and a `fixed` block only
    // pins for its own duration. Same reasoning as PipeWireStreamCore.
    private unsafe spa_hook*    _hook;
    private GCHandle            _selfHandle;
    private volatile bool       _disposed;

    /// <summary>Signature for <see cref="GraphChanged"/>.</summary>
    /// <param name="sender">The registry that published the snapshot.</param>
    /// <param name="snapshot">The graph as it stands after the change.</param>
    public delegate void GraphChangedHandler(PipeWireRegistry sender, PipeWireGraphSnapshot snapshot);

    /// <summary>
    /// Raised after every graph change, carrying the snapshot that reflects it.
    /// </summary>
    /// <remarks>
    /// Raised after <see cref="Current"/> is updated, so a handler reading it always sees a graph at
    /// least as new as the change it was told about. Runs on the PipeWire loop thread; keep handlers
    /// short.
    /// </remarks>
    public event GraphChangedHandler? GraphChanged;

    /// <summary>Raised when a new node appears in the graph.</summary>
    public event Action<PipeWireNode>? SourceAdded;

    /// <summary>Raised when a node is removed (carries the global id of the removed node).</summary>
    public event Action<uint>? SourceRemoved;

    /// <summary>Raised when a new port appears in the graph</summary>
    public event Action<PipeWirePort>? PortAdded;

    /// <summary>Raised when a new port appears in the graph</summary>
    public event Action<uint>? PortRemoved;

    /// <summary>Raised when a new link appears in the graph</summary>
    public event Action<PipeWireLink>? LinkAdded;

    /// <summary>Raised when a new link appears in the graph</summary>
    public event Action<uint>? LinkRemoved;

    /// <param name="context">A <see cref="PipeWireContext"/> with <see cref="PipeWireContext.StartAsync"/> already called.</param>
    public PipeWireRegistry(PipeWireContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ctx = context;
        _logger = context.LoggerFactory.CreateLogger("PipeWire.NET.Registry");
        _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);
        InitializeNative();
    }

    private unsafe void InitializeNative()
    {
        try
        {
            // get_registry / add_listener touch loop-owned objects -> hold the loop lock.
            using (_ctx.Lock())
            {
                _registry = Native.pw_core_get_registry(_ctx.CoreHandle, Native.PW_VERSION_REGISTRY, 0);
                if (_registry is null)
                    throw new InvalidOperationException("pw_core_get_registry returned null.");

                _events = (pw_registry_events*)NativeMemory.AllocZeroed((nuint)sizeof(pw_registry_events));
                _events->version       = Native.PW_VERSION_REGISTRY_EVENTS;
                _events->global        = &OnGlobal;
                _events->global_remove = &OnGlobalRemove;

                _hook = (spa_hook*)NativeMemory.AllocZeroed((nuint)sizeof(spa_hook));

                int rc = Native.pw_registry_add_listener(_registry, _hook, _events,
                    (void*)GCHandle.ToIntPtr(_selfHandle));
                if (rc < 0)
                    throw new InvalidOperationException(
                        $"pw_registry_add_listener failed with code {rc}.");
            }
        }
        catch
        {
            UnwindNative();
            throw;
        }
    }

    /// <summary>
    /// Releases every native resource acquired so far, in reverse order. Safe to call partway through
    /// initialization and safe to call twice.
    /// </summary>
    private unsafe void UnwindNative()
    {
        // Nothing more will ever be published, so anything waiting for a global has to be failed
        // here. Without this a creation in flight at disposal waits forever, and a caller that
        // passed CancellationToken.None never gets control back.
        foreach (uint id in _awaitingPublish.Keys)
        {
            if (_awaitingPublish.TryRemove(id, out TaskCompletionSource<IPipeWireObject>? waiter))
                waiter.TrySetException(new ObjectDisposedException(nameof(PipeWireRegistry),
                    $"the registry was disposed while object {id} was being created."));
        }

        // Disposing the context first destroys the loop, and with it everything below. Touching
        // those objects now would be a use-after-free, and taking the loop lock would throw out of
        // our own DisposeAsync. Release the managed side and leave the rest to the loop that took
        // it with it.
        bool loopIsGone = _ctx.IsDisposed;

        foreach (uint id in _ownedProxies.Keys)
        {
            if (loopIsGone) _ownedProxies.TryRemove(id, out _);
            else ReleaseOwnedProxy(id);
        }

        if (_registry is not null)
        {
            if (!loopIsGone)
            {
                using (_ctx.Lock())
                    Native.pw_registry_destroy(_registry);
            }
            _registry = null;
        }
        // Free the hook only after the registry is destroyed: destroying it removes the listener that
        // points at this memory.
        if (_hook is not null)
        {
            NativeMemory.Free(_hook);
            _hook = null;
        }
        if (_events is not null)
        {
            NativeMemory.Free(_events);
            _events = null;
        }
        if (_selfHandle.IsAllocated) _selfHandle.Free();
    }

    /// <remarks>
    /// The daemon has already destroyed the object by the time global_remove arrives, but the
    /// client-side proxy still needs freeing, and only the handle may do it.
    /// </remarks>
    private void ReleaseOwnedProxy(uint id)
    {
        if (_ownedProxies.TryRemove(id, out PipeWireProxyHandle? proxy))
            proxy.Dispose();
    }

    private PipeWireNode? GetNodeOrNull(uint id) => _sources.GetValueOrDefault(id);

    private PipeWireLink? GetLinkOrNull(uint id) => _links.GetValueOrDefault(id);

    /// <summary>Hands a newly published object to a creation that is waiting for its id.</summary>
    private void CompletePublishWaiter(uint id, IPipeWireObject published)
    {
        if (_awaitingPublish.TryRemove(id, out TaskCompletionSource<IPipeWireObject>? waiter))
            waiter.TrySetResult(published);
    }

    /// <summary>
    /// Registers interest in an id before its <c>global</c> arrives, and returns the object once
    /// published. Falls back to the live collections when the object arrived first.
    /// </summary>
    private async Task<T> AwaitPublishedAsync<T>(
        uint id, Func<uint, T?> lookup, CancellationToken cancellationToken) where T : class, IPipeWireObject
    {
        // A registry that is already gone will never publish anything, and `bound` can arrive after
        // disposal, so this has to be checked here rather than only when disposal runs.
        ObjectDisposedException.ThrowIf(_disposed, this);

        var waiter = new TaskCompletionSource<IPipeWireObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<IPipeWireObject> registered = _awaitingPublish.GetOrAdd(id, waiter);

        // The global may already have been delivered before we got here.
        if (lookup(id) is T early)
        {
            _awaitingPublish.TryRemove(id, out _);
            return early;
        }

        // And disposal may have run between the check above and the registration, in which case the
        // drain has already been and gone; fail here rather than wait for a publish that cannot come.
        if (_disposed)
        {
            _awaitingPublish.TryRemove(id, out _);
            throw new ObjectDisposedException(nameof(PipeWireRegistry));
        }

        using (cancellationToken.UnsafeRegister(static s => ((TaskCompletionSource<IPipeWireObject>)s!).TrySetCanceled(), registered))
        {
            try
            {
                return (T)await registered.Task.ConfigureAwait(false);
            }
            finally
            {
                _awaitingPublish.TryRemove(id, out _);
            }
        }
    }

    private unsafe pw_registry* RegistryHandle
    {
        get { ObjectDisposedException.ThrowIf(_disposed, this); return _registry; }
    }

    /// <summary>
    /// The graph as of the most recent change. Immutable and safe to hold; reading costs nothing.
    /// </summary>
    public PipeWireGraphSnapshot Current => Volatile.Read(ref _current);

    /// <summary>Snapshot of currently-visible nodes.</summary>
    public IReadOnlyCollection<PipeWireNode> Sources => Current.Nodes;

    /// <summary>
    /// Yields the graph as it changes, starting with the current snapshot.
    /// </summary>
    /// <remarks>
    /// A state stream, not an event log: the newest snapshot always wins, so a slow consumer skips
    /// intermediate ones rather than falling behind. Use the granular events when every transition
    /// matters, and <see cref="PipeWireGraphSnapshot.Version"/> to detect that a skip happened.
    /// </remarks>
    public async IAsyncEnumerable<PipeWireGraphSnapshot> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = System.Threading.Channels.Channel.CreateBounded<PipeWireGraphSnapshot>(
            new System.Threading.Channels.BoundedChannelOptions(1)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        void Forward(PipeWireRegistry _, PipeWireGraphSnapshot snapshot) => channel.Writer.TryWrite(snapshot);

        GraphChanged += Forward;
        try
        {
            yield return Current;
            await foreach (PipeWireGraphSnapshot snapshot in
                           channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return snapshot;
        }
        finally
        {
            GraphChanged -= Forward;
            channel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Invokes every subscriber, isolating each one.
    /// </summary>
    /// <remarks>
    /// An event is a multicast delegate, and invoking it as a single call stops at the first
    /// subscriber that throws - the rest are never reached. One badly behaved consumer would
    /// therefore silently starve every other subscriber, including <see cref="WatchAsync"/>, which
    /// forwards snapshots through <see cref="GraphChanged"/> like any other handler. Walking the
    /// invocation list keeps one hostile handler from becoming everyone else's outage.
    /// </remarks>
    private void RaiseIsolated<T>(Action<T>? handlers, T argument, string eventName)
    {
        if (handlers is null) return;

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<T>)handler)(argument); }
            catch (Exception ex) { LogHandlerFaulted(eventName, ex); }
        }
    }

    /// <remarks>
    /// Publishes before raising anything, so no handler can observe an event describing a change
    /// that <see cref="Current"/> does not yet contain.
    /// </remarks>
    private void Publish()
    {
        var snapshot = new PipeWireGraphSnapshot(
            ++_version, _sources.Values, _ports.Values, _links.Values);
        Volatile.Write(ref _current, snapshot);
    }

    private void RaiseGraphChanged()
    {
        GraphChangedHandler? handlers = GraphChanged;
        if (handlers is null) return;

        PipeWireGraphSnapshot snapshot = Current;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((GraphChangedHandler)handler)(this, snapshot); }
            catch (Exception ex) { LogHandlerFaulted(nameof(GraphChanged), ex); }
        }
    }

    /// <summary>
    /// Completes once the daemon has reported the objects that existed when the registry was
    /// created. Optional: events fire as the graph populates either way.
    /// </summary>
    /// <remarks>
    /// Uses a <c>pw_core_sync</c> round-trip rather than a timer. Because methods and events are
    /// delivered in order, the matching <c>done</c> proves the initial burst of <c>global</c> events
    /// has already been dispatched.
    /// </remarks>
    public Task WaitForInitialEnumerationAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return CoreSync.RoundTripAsync(_ctx, cancellationToken);
    }

    /// <summary>
    /// Creates a virtual stereo audio sink and returns it once the graph reports it.
    /// </summary>
    /// <param name="description">Human-readable name, shown to users as <c>node.description</c>.</param>
    /// <param name="name">Stable <c>node.name</c>; a GUID is used when omitted.</param>
    /// <param name="cancellationToken">Abandons the wait and destroys the half-created node.</param>
    /// <exception cref="InvalidOperationException">The daemon refused the request.</exception>
    /// <remarks>
    /// <para>
    /// The returned node exists in the graph, but <em>its ports may not yet</em>: the daemon
    /// announces those as separate globals while the node initialises. Await them through
    /// <see cref="WatchAsync"/> or <see cref="PortAdded"/> rather than assuming they are present:
    /// <code>
    /// var node = await registry.CreateVirtualStereoNodeAsync("Mix", ct);
    /// await foreach (var graph in registry.WatchAsync(ct))
    ///     if (graph.GetPortsForNode(node.NodeId).Length == 4) break;
    /// </code>
    /// </para>
    /// <para>
    /// Needs the SPA support plugin providing <c>support.null-audio-sink</c>, which is separate from
    /// the registry factories and will not appear in a factory listing.
    /// </para>
    /// </remarks>
    public Task<PipeWireNode> CreateVirtualStereoNodeAsync(
        string description, string? name = null, CancellationToken cancellationToken = default) =>
        CreateVirtualStereoNode(description, name).ExecuteAsync(cancellationToken);

    /// <summary>
    /// Describes a virtual stereo sink for creation, so options can be chained before it is made.
    /// </summary>
    /// <remarks>
    /// Nothing reaches the daemon until <see cref="PipeWireNodeCreation.ExecuteAsync"/> is awaited.
    /// </remarks>
    public PipeWireNodeCreation CreateVirtualStereoNode(string description, string? name = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(description);
        return new PipeWireNodeCreation(this, description, name, default);
    }

    internal async Task<PipeWireNode> ExecuteNodeCreationAsync(
        string description, string? name, PipeWireObjectOptions options, CancellationToken cancellationToken)
    {
        name ??= Guid.NewGuid().ToString();

        Task<PipeWireNode>? published = null;

        (uint id, PipeWireProxyHandle proxy) = await CreateObjectAsync(
            isLink: false, description, name, default, default, options,
            boundId => published = AwaitPublishedAsync(boundId, GetNodeOrNull, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        try
        {
            PipeWireNode node = await published!.WaitAsync(cancellationToken).ConfigureAwait(false);
            _ownedProxies[id] = proxy;
            return node;
        }
        catch
        {
            proxy.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Links an output port to an input port and returns the link once the graph reports it.
    /// </summary>
    /// <param name="output">The port data leaves from; must be <see cref="PipeWirePortDirection.Out"/>.</param>
    /// <param name="input">The port data arrives at; must be <see cref="PipeWirePortDirection.In"/>.</param>
    /// <param name="cancellationToken">Abandons the wait and destroys the half-created link.</param>
    /// <exception cref="ArgumentException">A port faces the wrong way.</exception>
    /// <exception cref="InvalidOperationException">The daemon refused the request.</exception>
    public Task<PipeWireLink> CreateLinkAsync(
        PipeWirePort output, PipeWirePort input, CancellationToken cancellationToken = default) =>
        CreateLink(output, input).ExecuteAsync(cancellationToken);

    /// <summary>
    /// Describes a link for creation, so options can be chained before it is made.
    /// </summary>
    /// <remarks>
    /// Nothing reaches the daemon until <see cref="PipeWireLinkCreation.ExecuteAsync"/> is awaited.
    /// Port directions are validated here rather than at execution, so a mistake surfaces where it
    /// was made.
    /// </remarks>
    /// <exception cref="ArgumentException">A port faces the wrong way.</exception>
    public PipeWireLinkCreation CreateLink(PipeWirePort output, PipeWirePort input)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(input);

        if (output.PortDirection != PipeWirePortDirection.Out)
            throw new ArgumentException($"Port {output.PortId} is not an output port.", nameof(output));
        if (input.PortDirection != PipeWirePortDirection.In)
            throw new ArgumentException($"Port {input.PortId} is not an input port.", nameof(input));

        return new PipeWireLinkCreation(this, output, input, default);
    }

    internal async Task<PipeWireLink> ExecuteLinkCreationAsync(
        PipeWirePort output, PipeWirePort input, PipeWireObjectOptions options,
        CancellationToken cancellationToken)
    {
        Task<PipeWireLink>? published = null;

        (uint id, PipeWireProxyHandle proxy) = await CreateObjectAsync(
            isLink: true, null, null, output, input, options,
            boundId => published = AwaitPublishedAsync(boundId, GetLinkOrNull, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        try
        {
            PipeWireLink link = await published!.WaitAsync(cancellationToken).ConfigureAwait(false);
            _ownedProxies[id] = proxy;
            return link;
        }
        catch
        {
            proxy.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Removes a link. Works for links this client created and for links owned by anyone else,
    /// which is the common case for a patchbay.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The daemon refused, usually for want of execute permission on the global.
    /// </exception>
    public Task RemoveLinkAsync(uint linkId, CancellationToken cancellationToken = default) =>
        RemoveObjectAsync(linkId, cancellationToken);

    /// <summary>
    /// Asks the daemon to destroy any global by id - a node, a link, or anything else the registry
    /// can see.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="PipeWireNodeCreation.WithLinger"/>: an object created to
    /// outlive its creator cannot be removed by disconnecting, so this is the only way to take it
    /// down. Removal is asynchronous in the graph - the object leaves
    /// <see cref="Current"/> when the daemon's <c>global_remove</c> arrives, not when this returns.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The daemon refused, usually for want of execute permission on the global.
    /// </exception>
    public unsafe Task RemoveObjectAsync(uint id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        int rc;
        using (_ctx.Lock())
            rc = Native.pw_registry_destroy_global(RegistryHandle, id);

        // destroy is asynchronous: it returns SPA_ASYNC_BIT | seq, never a status. A refusal comes
        // back later as a core error carrying that same seq, so the only way to report one is to
        // round-trip the core and watch for it. Testing this value for 0 or for < 0 detects nothing.
        if (Native.SPA_RESULT_IS_ASYNC(rc))
            return CoreSync.RoundTripAsync(_ctx, Native.SPA_RESULT_ASYNC_SEQ(rc), cancellationToken);

        return rc < 0
            ? Task.FromException(new InvalidOperationException($"Removing object {id} failed with code {rc}."))
            : Task.CompletedTask;
    }

    /// <inheritdoc cref="RemoveLinkAsync(uint, CancellationToken)"/>
    public Task RemoveLinkAsync(PipeWireLink link, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        return RemoveLinkAsync(link.LinkId, cancellationToken);
    }

    private unsafe Task<(uint Id, PipeWireProxyHandle Proxy)> CreateObjectAsync(
        bool isLink, string? description, string? name,
        PipeWirePort? output, PipeWirePort? input, PipeWireObjectOptions options,
        Action<uint> onBound, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // PipeWire puts no length limit on a property: spa_dict_item is a pair of const char*.
        // So size the scratch to the caller's strings rather than imposing a limit of our own -
        // a stackalloc covers every ordinary name, and anything longer rents.
        int needed = FixedPropertyBytes
            + (name is null ? 0 : Encoding.UTF8.GetByteCount(name) + 1)
            + (description is null ? 0 : Encoding.UTF8.GetByteCount(description) + 1);

        byte[]? rented = needed > StackScratchBytes ? ArrayPool<byte>.Shared.Rent(needed) : null;
        try
        {
            Span<byte> scratch = rented ?? stackalloc byte[StackScratchBytes];
            Span<spa_dict_item> items = stackalloc spa_dict_item[8];
            var dict = new SpaDictBuilder(scratch, items);

            if (options.Linger) dict.Add(PipeWireKeys.ObjectLinger, PipeWireKeys.True);

            if (isLink)
            {
                dict.Add(PipeWireKeys.LinkOutputNode, output!.NodeId);
                dict.Add(PipeWireKeys.LinkOutputPort, output.PortId);
                dict.Add(PipeWireKeys.LinkInputNode, input!.NodeId);
                dict.Add(PipeWireKeys.LinkInputPort, input.PortId);
                if (options.Passive) dict.Add(PipeWireKeys.LinkPassive, PipeWireKeys.True);

                return NativeObjectCreation.CreateAsync(
                    _ctx, PipeWireKeys.LinkFactory, PipeWireKeys.InterfaceLink,
                    Native.PW_VERSION_LINK, dict.Build(), cancellationToken, onBound);
            }

            dict.Add(PipeWireKeys.FactoryName, PipeWireKeys.NullAudioSink);
            dict.Add(PipeWireKeys.NodeName, name!);
            dict.Add(PipeWireKeys.NodeDescription, description!);
            dict.Add(PipeWireKeys.MediaClass, PipeWireKeys.AudioSink);
            dict.Add(PipeWireKeys.AudioPosition, PipeWireKeys.StereoPosition);

            return NativeObjectCreation.CreateAsync(
                _ctx, PipeWireKeys.Adapter, PipeWireKeys.InterfaceNode,
                Native.PW_VERSION_NODE, dict.Build(), cancellationToken, onBound);
        }
        finally
        {
            // create_object copies the properties into the daemon's own pw_properties before
            // returning, so the buffer is dead the moment CreateAsync has run its synchronous part.
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Scratch that covers every ordinary name without touching the pool.</summary>
    private const int StackScratchBytes = 512;

    /// <summary>
    /// Upper bound on the constant keys and values either creation path writes, NUL bytes included.
    /// </summary>
    private const int FixedPropertyBytes = 256;

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        UnwindNative();
        return ValueTask.CompletedTask;
    }

    // - Native callbacks -

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnGlobal(void* data, uint id, uint permissions, sbyte* type, uint version, spa_dict* props)
    {
        var self = (PipeWireRegistry?)GCHandle.FromIntPtr((nint)data).Target;
        if (self is null || self._disposed) return;

        // An exception escaping a reverse P/Invoke aborts the process, so nothing below may throw.
        try
        {
            var perms = (PipeWirePermissions)permissions;

            ReadOnlySpan<byte> kind = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)type);

            if      (kind.SequenceEqual(PipeWireKeys.InterfaceNode)) self.AddNode(id, perms, version, props);
            else if (kind.SequenceEqual(PipeWireKeys.InterfacePort)) self.AddPort(id, perms, version, props);
            else if (kind.SequenceEqual(PipeWireKeys.InterfaceLink)) self.AddLink(id, perms, version, props);
        }
        catch (Exception ex)
        {
            self.LogHandlerFaulted("global", ex);
        }
    }

    private unsafe void AddNode(uint id, PipeWirePermissions permissions, uint version, spa_dict* props)
    {
        PipeWireNode source = PipeWireGlobalParser.ParseNode(id, permissions, version, props);
        string? name = source.NodeName;
        string? mediaClass = source.MediaClass;

        lock (_sources)
            _sources[id] = source;
        LogNodeAdded(id, name, mediaClass);
        Publish();
        CompletePublishWaiter(id, source);

        RaiseIsolated(SourceAdded, source, nameof(SourceAdded));
        RaiseGraphChanged();
    }

    private unsafe void AddPort(uint id, PipeWirePermissions permissions, uint version, spa_dict* props)
    {
        if (!PipeWireGlobalParser.TryParsePort(
                id, permissions, version, props,
                out PipeWirePort? parsed, out string reason, out string? offending))
        {
            LogPortSkipped(id, reason, offending);
            return;
        }

        PipeWirePort port = parsed!;
        lock (_ports)
            _ports[id] = port;
        LogPortAdded(id, port.PortName, port.PortDirection, port.NodeId);
        Publish();
        CompletePublishWaiter(id, port);

        RaiseIsolated(PortAdded, port, nameof(PortAdded));
        RaiseGraphChanged();
    }

    private unsafe void AddLink(uint id, PipeWirePermissions permissions, uint version, spa_dict* props)
    {
        if (!PipeWireGlobalParser.TryParseLink(
                id, permissions, version, props,
                out PipeWireLink? parsed, out string reason, out string? offending))
        {
            LogLinkSkipped(id, reason, offending);
            return;
        }

        PipeWireLink link = parsed!;
        uint outputNode = link.LinkOutputNode, outputPort = link.LinkOutputPort;
        uint inputNode = link.LinkInputNode, inputPort = link.LinkInputPort;

        lock (_links)
            _links[id] = link;
        LogLinkAdded(id, outputNode, outputPort, inputNode, inputPort);
        Publish();
        CompletePublishWaiter(id, link);

        RaiseIsolated(LinkAdded, link, nameof(LinkAdded));
        RaiseGraphChanged();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnGlobalRemove(void* data, uint id)
    {
        var self = (PipeWireRegistry?)GCHandle.FromIntPtr((nint)data).Target;
        if (self is null || self._disposed) return;

        try
        {
            self.ReleaseOwnedProxy(id);

            if (self._sources.TryRemove(id, out _))
            {
                self.LogRemoved("node", id);
                self.Publish();
                self.RaiseIsolated(self.SourceRemoved, id, nameof(SourceRemoved));
                self.RaiseGraphChanged();
            }
            else if (self._ports.TryRemove(id, out _))
            {
                self.LogRemoved("port", id);
                self.Publish();
                self.RaiseIsolated(self.PortRemoved, id, nameof(PortRemoved));
                self.RaiseGraphChanged();
            }
            else if (self._links.TryRemove(id, out _))
            {
                self.LogRemoved("link", id);
                self.Publish();
                self.RaiseIsolated(self.LinkRemoved, id, nameof(LinkRemoved));
                self.RaiseGraphChanged();
            }
        }
        catch (Exception ex)
        {
            self.LogHandlerFaulted("global_remove", ex);
        }
    }

    // - spa_dict helpers -



    // - Diagnostics (source-generated, level-gated). Enabled via the logger factory passed to
    //   PipeWireContext; silent by default. -

    [LoggerMessage(EventId = 1, Level = LogLevel.Trace, Message = "node {Id} '{Name}' ({MediaClass})")]
    private partial void LogNodeAdded(uint id, string? name, string? mediaClass);

    [LoggerMessage(EventId = 2, Level = LogLevel.Trace, Message = "port {Id} '{Name}' ({Direction}) of node {NodeId}")]
    private partial void LogPortAdded(uint id, string? name, PipeWirePortDirection direction, uint nodeId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Trace, Message = "link {Id} {OutputNode}.{OutputPort} -> {InputNode}.{InputPort}")]
    private partial void LogLinkAdded(uint id, uint outputNode, uint outputPort, uint inputNode, uint inputPort);

    [LoggerMessage(EventId = 4, Level = LogLevel.Trace, Message = "removed {Kind} {Id}")]
    private partial void LogRemoved(string kind, uint id);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "skipped port {Id}: {Reason} ({Value})")]
    private partial void LogPortSkipped(uint id, string reason, string? value);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "skipped link {Id}: {Reason} ({Value})")]
    private partial void LogLinkSkipped(uint id, string reason, string? value);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "a {Event} handler threw")]
    private partial void LogHandlerFaulted(string @event, Exception exception);

}
