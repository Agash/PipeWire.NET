using System.Collections.Immutable;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Interop;
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
/// until the daemon has reported its initial state) -> enumerate <see cref="Nodes"/> or
/// subscribe to <see cref="NodeAdded"/>/<see cref="NodeRemoved"/> -> dispose.
/// </para>
/// <para>Events are raised on the PipeWire main-loop thread.</para>
/// <para>
/// <b>Dispose it; do not let it fall out of scope.</b> The callbacks the daemon holds refer back
/// here through a weak handle, so an instance the application drops is collected and simply stops
/// delivering, with no error and no final state change. That is the deliberate half of the trade:
/// a strong handle would keep every one ever made alive for the life of the process. What it costs
/// is that the garbage collector cannot be the thing that closes one, because by the time it runs
/// there is nothing left to close it from.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class PipeWireRegistry : IDisposable, IAsyncDisposable
{
    internal readonly PipeWireContext _ctx;
    private readonly ILogger _logger;
    internal readonly ConcurrentDictionary<uint, PipeWireNode> _sources = new();
    internal readonly ConcurrentDictionary<uint, PipeWirePort> _ports = new();
    internal readonly ConcurrentDictionary<uint, PipeWireLink> _links = new();

    // Everything that is not a node, port or link. One dictionary rather than eight, because none
    // of these take part in routing and the snapshot is what sorts them by kind for a reader.
    internal readonly ConcurrentDictionary<uint, IPipeWireObject> _objects = new();

    // Proxies for objects this client created. PipeWire hands ownership to the caller, and
    // pw_proxy_destroy may be called exactly once, so the handle is the only thing that may.
    private readonly ConcurrentDictionary<uint, PipeWireProxyHandle> _ownedProxies = new();

    // Ids whose proxy exists on the daemon but has not reached _ownedProxies yet, because the
    // creation is still waiting for the graph to publish it. The value records whether a
    // global_remove arrived in the meantime. Without this, a removal in that window finds nothing
    // to release and the proxy is then filed anyway, where it stays until the registry is disposed:
    // the leak is invisible because the object it belonged to is long gone. Bounded by the number
    // of creations in flight.
    private readonly ConcurrentDictionary<uint, bool> _creating = new();

    // Creations that know their id and are waiting for the object to be published. Registered
    // between `bound` and the registry `global`, so the entity is handed over as it is added
    // rather than looked up afterwards, which would race anything removing it in between.
    private readonly ConcurrentDictionary<uint, PublishWaiters> _awaitingPublish = new();

    /// <summary>Everyone waiting for one id's global to arrive.</summary>
    /// <remarks>
    /// A list rather than a single source. Ids are reused as objects come and go, so two callers can
    /// be waiting on the same one; sharing a <see cref="TaskCompletionSource"/> between them means
    /// one caller's cancellation cancels the other's wait, and either caller's exit removes the
    /// registration the other is still relying on.
    /// </remarks>
    private sealed class PublishWaiters
    {
        private readonly List<TaskCompletionSource<IPipeWireObject>> _waiters = [];

        internal TaskCompletionSource<IPipeWireObject> Add()
        {
            var waiter = new TaskCompletionSource<IPipeWireObject>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_waiters) _waiters.Add(waiter);
            return waiter;
        }

        /// <summary>Removes one waiter and reports whether any remain.</summary>
        internal bool Remove(TaskCompletionSource<IPipeWireObject> waiter)
        {
            lock (_waiters)
            {
                _waiters.Remove(waiter);
                return _waiters.Count == 0;
            }
        }

        internal void CompleteAll(IPipeWireObject published)
        {
            foreach (TaskCompletionSource<IPipeWireObject> waiter in Snapshot())
                waiter.TrySetResult(published);
        }

        internal void FailAll(Exception error)
        {
            foreach (TaskCompletionSource<IPipeWireObject> waiter in Snapshot())
                waiter.TrySetException(error);
        }

        private TaskCompletionSource<IPipeWireObject>[] Snapshot()
        {
            lock (_waiters) return [.. _waiters];
        }
    }

    private PipeWireGraphSnapshot _current = PipeWireGraphSnapshot.Empty;
    private long _version;

    private PipeWireProxyHandle?       _registryOwner;
    private unsafe pw_registry_events* _events;
    // Unmanaged, not a field: pw_registry_add_listener retains this pointer and a `fixed` block only
    // pins for its own duration. Same reasoning as PipeWireStreamCore.
    private unsafe spa_hook*    _hook;
    private GCHandle            _selfHandle;
    private bool _listenerOwned;
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
    public event Action<PipeWireNode>? NodeAdded;

    /// <summary>Raised when a node is removed (carries the global id of the removed node).</summary>
    public event Action<uint>? NodeRemoved;

    /// <summary>Raised when a new port appears in the graph</summary>
    public event Action<PipeWirePort>? PortAdded;

    /// <summary>Raised when a port is removed (carries the global id of the removed port).</summary>
    public event Action<uint>? PortRemoved;

    /// <summary>Raised when a new link appears in the graph</summary>
    public event Action<PipeWireLink>? LinkAdded;

    /// <summary>Raised when a link is removed (carries the global id of the removed link).</summary>
    public event Action<uint>? LinkRemoved;

    /// <summary>Raised once when disposal begins, so watchers can finish rather than block.</summary>
    private event Action? Disposing;

    /// <param name="context">A <see cref="PipeWireContext"/> with <see cref="PipeWireContext.StartAsync"/> already called.</param>
    public PipeWireRegistry(PipeWireContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ctx = context;
        _logger = context.LoggerFactory.CreateLogger("PipeWire.NET.Registry");
        // Weak, for the reason given in BoundProxy: a strong handle here roots the registry
        // through its own listener, so an undisposed registry could never be finalized.
        _selfHandle = GCHandle.Alloc(this, GCHandleType.Weak);
        InitializeNative();
    }

    private unsafe void InitializeNative()
    {
        try
        {
            // get_registry / add_listener touch loop-owned objects -> hold the loop lock.
            using (_ctx.Lock())
            {
                pw_registry* registry =
                    Native.pw_core_get_registry(_ctx.CoreHandle, Native.PW_VERSION_REGISTRY, 0);

                // The registry is a proxy like any other, so it gets the same owner: that keeps the
                // core and context alive long enough to destroy it properly rather than having it
                // abandoned when the caller disposes them first. The handle is the only place the
                // pointer lives, so the two cannot disagree about whether it is still valid.
                if (registry is not null)
                    _registryOwner = new PipeWireProxyHandle(
                        (pw_proxy*)registry, _ctx.LoopOwner, _ctx.CoreOwner);
                if (registry is null)
                    throw new PipeWireException("pw_core_get_registry", -12);   // ENOMEM

                _events = (pw_registry_events*)NativeMemory.AllocZeroed((nuint)sizeof(pw_registry_events));
                _events->version       = Native.PW_VERSION_REGISTRY_EVENTS;
                _events->global        = &OnGlobal;
                _events->global_remove = &OnGlobalRemove;

                _hook = (spa_hook*)NativeMemory.AllocZeroed((nuint)sizeof(spa_hook));

                // Handed to the handle, which frees it after pw_proxy_destroy has actually run.
                // Freeing it when this object is disposed is too early: disposal only destroys the
                // proxy once nothing else holds a reference, and until then the daemon is still
                // dispatching through this table.
                _registryOwner!.OwnListener(_events, _hook, _selfHandle);
                _listenerOwned = true;

                int rc = Native.pw_registry_add_listener(registry, _hook, _events,
                    (void*)GCHandle.ToIntPtr(_selfHandle));
                if (rc < 0)
                    throw new PipeWireException("pw_registry_add_listener", rc);
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
            if (_awaitingPublish.TryRemove(id, out PublishWaiters? waiters))
                waiters.FailAll(new ObjectDisposedException(nameof(PipeWireRegistry),
                    $"the registry was disposed while object {id} was being created."));
        }

        // Safe in either disposal order. The handle chain - proxy holds core holds context holds
        // loop - means none of the native objects a proxy needs can have been destroyed while the
        // proxy handle is alive, even if the context was disposed first.
        foreach (uint id in _ownedProxies.Keys)
            ReleaseOwnedProxy(id);

        // Destroyed through its handle, which holds the core and context open for exactly as long
        // as it takes - so this works whichever order the caller disposed things in.
        // The listener's memory goes with the handle: destroying the registry proxy is what
        // detaches it, and that only happens once nothing else holds a reference to the handle.
        _registryOwner?.Dispose();
        _registryOwner = null;

        // Only when the handle never took them: initialisation can fail between allocating these
        // and handing them over, and nothing else would ever free them.
        if (!_listenerOwned)
        {
            if (_hook is not null) NativeMemory.Free(_hook);
            if (_events is not null) NativeMemory.Free(_events);
            if (_selfHandle.IsAllocated) _selfHandle.Free();
        }

        _hook = null;
        _events = null;
    }

    /// <remarks>
    /// The daemon has already destroyed the object by the time global_remove arrives, but the
    /// client-side proxy still needs freeing, and only the handle may do it.
    /// </remarks>
    private void ReleaseOwnedProxy(uint id)
    {
        if (_ownedProxies.TryRemove(id, out PipeWireProxyHandle? proxy))
        {
            proxy.Dispose();
            return;
        }

        // Nothing filed yet. If this id is still being created, say so, and whoever finishes the
        // creation disposes the proxy instead of filing it.
        _creating.TryUpdate(id, newValue: true, comparisonValue: false);
    }

    /// <summary>How many created objects this registry is still holding a proxy for.</summary>
    /// <remarks>
    /// Exposed to the tests because the leak it measures is otherwise invisible: a proxy left here
    /// after its object is gone costs nothing observable until the registry is disposed, so no
    /// descriptor count, daemon-side count or graph assertion moves when it happens.
    /// </remarks>
    internal int OwnedProxyCount => _ownedProxies.Count;

    /// <summary>Files a freshly created proxy, or disposes it if its object is already gone.</summary>
    private void TakeOwnership(uint id, PipeWireProxyHandle proxy)
    {
        bool removedWhileCreating = _creating.TryRemove(id, out bool removed) && removed;

        if (removedWhileCreating)
        {
            proxy.Dispose();
            return;
        }

        _ownedProxies[id] = proxy;
    }

    /// <summary>Releases a proxy whose creation did not finish, without double-disposing it.</summary>
    private void AbandonOwnership(uint id, PipeWireProxyHandle proxy)
    {
        _creating.TryRemove(id, out _);

        // TryRemove rather than a bare Dispose: a global_remove between filing and here has already
        // taken and disposed it.
        if (_ownedProxies.TryRemove(id, out PipeWireProxyHandle? filed))
        {
            filed.Dispose();
            return;
        }

        proxy.Dispose();
    }

    private PipeWireNode? GetNodeOrNull(uint id) => _sources.GetValueOrDefault(id);

    private PipeWireLink? GetLinkOrNull(uint id) => _links.GetValueOrDefault(id);

    /// <summary>Removes the ports and links that belonged to a node that has just gone.</summary>
    /// <remarks>
    /// Raises the same removal events the daemon's own events would have, so a subscriber sees each
    /// object leave exactly once whichever order the daemon reports them in. The later events find
    /// the entries already gone and do nothing.
    /// </remarks>
    private void CascadeRemove(uint nodeId)
    {
        foreach ((uint portId, PipeWirePort port) in _ports)
        {
            if (port.NodeId != nodeId) continue;

            foreach ((uint linkId, PipeWireLink link) in _links)
            {
                if (link.LinkOutputPort != portId && link.LinkInputPort != portId) continue;
                if (!_links.TryRemove(linkId, out _)) continue;

                LogRemoved("link", linkId);
                RaiseIsolated(LinkRemoved, linkId, nameof(LinkRemoved));
            }

            if (!_ports.TryRemove(portId, out _)) continue;

            LogRemoved("port", portId);
            RaiseIsolated(PortRemoved, portId, nameof(PortRemoved));
        }
    }

    /// <summary>Hands a newly published object to every creation waiting for its id.</summary>
    private void CompletePublishWaiter(uint id, IPipeWireObject published)
    {
        if (_awaitingPublish.TryRemove(id, out PublishWaiters? waiters))
            waiters.CompleteAll(published);
    }

    /// <summary>Fails everyone waiting for an id whose global arrived but could not be used.</summary>
    /// <remarks>
    /// A global the daemon sent and this library could not parse is still an answer. Dropping it
    /// silently leaves the creation that asked for the object waiting for a publish that has already
    /// been and gone, and the caller then blocks until its own token fires - a hang where the honest
    /// outcome is an error naming the property that could not be read.
    /// </remarks>
    private void FailPublishWaiter(uint id, string reason)
    {
        if (_awaitingPublish.TryRemove(id, out PublishWaiters? waiters))
        {
            waiters.FailAll(new PipeWireException(
                $"object {id} was published but could not be read: {reason}", -22));
        }
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

        PublishWaiters waiters = _awaitingPublish.GetOrAdd(id, static _ => new PublishWaiters());
        TaskCompletionSource<IPipeWireObject> waiter = waiters.Add();

        // The global may already have been delivered before we got here.
        if (lookup(id) is T early)
        {
            Unregister(id, waiters, waiter);
            return early;
        }

        // And disposal may have run between the check above and the registration, in which case the
        // drain has already been and gone; fail here rather than wait for a publish that cannot come.
        if (_disposed)
        {
            Unregister(id, waiters, waiter);
            throw new ObjectDisposedException(nameof(PipeWireRegistry));
        }

        // Cancels this caller's wait and nobody else's.
        using (cancellationToken.UnsafeRegister(
            static s => ((TaskCompletionSource<IPipeWireObject>)s!).TrySetCanceled(), waiter))
        {
            try
            {
                return (T)await waiter.Task.ConfigureAwait(false);
            }
            finally
            {
                Unregister(id, waiters, waiter);
            }
        }
    }

    /// <summary>Drops one waiter, and the id's entry once it was the last.</summary>
    private void Unregister(
        uint id, PublishWaiters waiters, TaskCompletionSource<IPipeWireObject> waiter)
    {
        if (!waiters.Remove(waiter)) return;

        // Conditional on identity: a waiter that registered after this one emptied the list holds a
        // different instance, and removing by key alone would strand it.
        _awaitingPublish.TryRemove(new KeyValuePair<uint, PublishWaiters>(id, waiters));
    }

    /// <summary>Resolves the instance a native callback belongs to, or null if it is gone.</summary>
    private static unsafe PipeWireRegistry? FromData(void* data)
    {
        if (data is null) return null;

        try
        {
            return GCHandle.FromIntPtr((nint)data).Target as PipeWireRegistry;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The registry proxy, read from the handle that owns it.</summary>
    /// <remarks>
    /// Never null when it returns. Reading the disposed flag and then dereferencing the owner is
    /// two steps, and teardown nulls the owner in between, which hands a null
    /// <c>pw_registry*</c> to whatever native call the caller was about to make.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The registry is disposed or being disposed.</exception>
    private unsafe pw_registry* RegistryHandle
    {
        get
        {
            PipeWireProxyHandle? owner = _registryOwner;
            if (_disposed || owner is null || owner.IsInvalid)
                throw new ObjectDisposedException(nameof(PipeWireRegistry));

            var registry = (pw_registry*)owner.Proxy;
            if (registry is null)
                throw new ObjectDisposedException(nameof(PipeWireRegistry));

            return registry;
        }
    }

    /// <summary>
    /// The graph as of the most recent change. Immutable and safe to hold; reading costs nothing.
    /// </summary>
    public PipeWireGraphSnapshot Current =>
        Interlocked.Read(ref _builtRevision) == Interlocked.Read(ref _revision)
            ? Volatile.Read(ref _current)
            : Rebuild();

    /// <summary>Snapshot of currently-visible nodes.</summary>
    public IReadOnlyCollection<PipeWireNode> Nodes => Current.Nodes;

    /// <summary>
    /// Yields the graph as it changes, starting with the current snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A state stream, not an event log: the newest snapshot always wins, so a slow consumer skips
    /// intermediate ones rather than falling behind. Use the granular events when every transition
    /// matters, and <see cref="PipeWireGraphSnapshot.Version"/> to detect that a skip happened.
    /// </para>
    /// <para>
    /// The first snapshot yielded is the current one at enumeration time, not the one that was
    /// current when the enumerator was created. Changes between those two points are folded into it
    /// rather than reported, so a consumer cannot assume it has observed every transition since it
    /// subscribed. Disposing the registry ends the stream.
    /// </para>
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
        void Finish() => channel.Writer.TryComplete();

        GraphChanged += Forward;

        // Disposal has to end the stream. No further change is coming once the registry is gone, so
        // a consumer that passed no cancellation token would otherwise wait on this for ever.
        Disposing += Finish;
        try
        {
            if (_disposed) yield break;

            yield return Current;
            await foreach (PipeWireGraphSnapshot snapshot in
                           channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return snapshot;
        }
        finally
        {
            Disposing -= Finish;
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
        SafeCallback.Raise(handlers, h => h(argument), ex => LogHandlerFaulted(eventName, ex));
    }

    /// <remarks>
    /// Publishes before raising anything, so no handler can observe an event describing a change
    /// that <see cref="Current"/> does not yet contain.
    /// </remarks>
    /// <summary>Marks the graph changed. The snapshot itself is built when somebody asks.</summary>
    /// <remarks>
    /// The daemon delivers its whole graph as a run of global events when a registry is created, so
    /// building a snapshot per event copies every object once per object - hundreds of arrays that
    /// nothing reads, because a caller subscribes after construction and reads once at the end.
    /// Marking dirty and building on demand collapses that burst to one build without needing to
    /// know when the burst ended.
    /// </remarks>
    private void Publish() => Interlocked.Increment(ref _revision);

    // A counter rather than a flag. A flag has to be cleared before the collections are read, or a
    // change arriving mid-build is lost - and clearing it first opens a window where a reader sees
    // "clean" while the new snapshot has not been published yet, and takes the stale one. The
    // counter closes both: the built revision is only advanced once the snapshot is in place, and a
    // change during the build leaves the counter ahead so the next reader rebuilds.
    private long _revision = 1;
    private long _builtRevision;
    private readonly Lock _publishGate = new();

    private PipeWireGraphSnapshot Rebuild()
    {
        // Under one lock, so the four collections are read at one instant rather than four. A
        // reader arriving mid-burst still sees a coherent set rather than ports from one moment
        // and links from another.
        lock (_publishGate)
        {
            long revision = Interlocked.Read(ref _revision);
            if (revision == Interlocked.Read(ref _builtRevision)) return Volatile.Read(ref _current);

            var snapshot = new PipeWireGraphSnapshot(
                ++_version, _sources.Values, _ports.Values, _links.Values, _objects.Values);

            Volatile.Write(ref _current, snapshot);
            Interlocked.Exchange(ref _builtRevision, revision);
            return snapshot;
        }
    }

    private void RaiseGraphChanged()
    {
        // Checked before Current, which is what forces the snapshot to be built. With nothing
        // subscribed - the whole of the initial enumeration, since a caller subscribes after
        // construction - there is nothing to build it for.
        GraphChangedHandler? handlers = GraphChanged;
        if (handlers is null) return;

        PipeWireGraphSnapshot snapshot = Current;
        SafeCallback.Raise(
            handlers,
            (Registry: this, Snapshot: snapshot),
            static (h, s) => h(s.Registry, s.Snapshot),
            static (s, ex) => s.Registry.LogHandlerFaulted(nameof(GraphChanged), ex));
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
    /// <exception cref="PipeWireException">
    /// The daemon refused the request. <see cref="PipeWireException.Result"/> carries its code, so
    /// a refused permission and a format that cannot be negotiated are told apart without reading
    /// the message.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The returned node exists in the graph, but <em>its ports may not yet</em>: the daemon
    /// announces those as separate globals while the node initialises. Await them through
    /// <see cref="WatchAsync"/> or <see cref="PortAdded"/> rather than assuming they are present:
    /// <code>
    /// var node = await registry.CreateVirtualNodeAsync("Mix", ct);
    /// await foreach (var graph in registry.WatchAsync(ct))
    ///     if (graph.GetPortsForNode(node.NodeId).Length == 4) break;
    /// </code>
    /// </para>
    /// <para>
    /// Needs the SPA support plugin providing <c>support.null-audio-sink</c>, which is separate from
    /// the registry factories and will not appear in a factory listing.
    /// </para>
    /// </remarks>
    public Task<PipeWireNode> CreateVirtualNodeAsync(
        string description, string? name = null, CancellationToken cancellationToken = default) =>
        CreateVirtualNode(description, name).ExecuteAsync(cancellationToken);

    /// <summary>
    /// Describes a virtual node for creation, so options can be chained before it is made.
    /// </summary>
    /// <remarks>
    /// A stereo sink unless told otherwise: <see cref="PipeWireNodeCreation.WithMediaClass"/> makes
    /// it a source and <see cref="PipeWireNodeCreation.WithChannelPositions"/> changes its channel
    /// map, so the defaults are a starting point rather than what this can build. Nothing reaches
    /// the daemon until <see cref="PipeWireNodeCreation.ExecuteAsync"/> is awaited.
    /// </remarks>
    public PipeWireNodeCreation CreateVirtualNode(string description, string? name = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(description);
        return new PipeWireNodeCreation(this, description, name, default);
    }

    /// <summary>
    /// Describes a virtual audio source: a node other clients capture from, rather than play into.
    /// </summary>
    /// <param name="description">What a mixer shows for it.</param>
    /// <param name="name">Its <c>node.name</c>. Generated when omitted.</param>
    /// <remarks>
    /// The same factory as <see cref="CreateVirtualNode"/> with <c>media.class</c> set to
    /// <c>Audio/Source</c>, named because it is the second of the two shapes that factory makes and
    /// there is nothing in "virtual node" to say which one a caller gets. A sink is something to
    /// play into; this is something to capture from, which is how a virtual microphone is built.
    /// <para>
    /// Whatever is written into it still arrives through a stream this client owns: the node carries
    /// no audio on its own, it gives other clients somewhere to read from.
    /// </para>
    /// </remarks>
    public PipeWireNodeCreation CreateVirtualSource(string description, string? name = null) =>
        CreateVirtualNode(description, name).WithMediaClass("Audio/Source");

    internal async Task<PipeWireNode> ExecuteNodeCreationAsync(
        string description, string? name, PipeWireObjectOptions options, CancellationToken cancellationToken)
    {
        name ??= Guid.NewGuid().ToString();

        Task<PipeWireNode>? published = null;

        (uint id, PipeWireProxyHandle proxy) = await CreateObjectAsync(
            isLink: false, description, name, default, default, options,
            boundId =>
            {
                // Marked before anything can be awaited, so a removal arriving while the graph
                // catches up has somewhere to record itself.
                _creating[boundId] = false;
                published = AwaitPublishedAsync(boundId, GetNodeOrNull, cancellationToken);
            },
            cancellationToken).ConfigureAwait(false);

        try
        {
            PipeWireNode node = await published!.WaitAsync(cancellationToken).ConfigureAwait(false);
            TakeOwnership(id, proxy);
            return node;
        }
        catch
        {
            AbandonOwnership(id, proxy);
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
    /// <exception cref="PipeWireException">
    /// The daemon refused the request. <see cref="PipeWireException.Result"/> carries its code, so
    /// a refused permission and a format that cannot be negotiated are told apart without reading
    /// the message.
    /// </exception>
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

    /// <summary>
    /// Describes a link between two ports named by id, for a caller that has ids rather than the
    /// port records.
    /// </summary>
    /// <param name="outputPortId">The port data leaves from.</param>
    /// <param name="inputPortId">The port data arrives at.</param>
    /// <remarks>
    /// The ids are resolved against the current graph so the direction check still happens here
    /// rather than reaching the daemon as a refusal. A caller holding ids from a configuration file
    /// or another process would otherwise have to search the graph itself to call the overload
    /// above, which is the same lookup written again at every call site.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// An id is not a port in the current graph, or a port faces the wrong way.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The registry has been disposed.</exception>
    public PipeWireLinkCreation CreateLink(uint outputPortId, uint inputPortId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        PipeWireGraphSnapshot graph = Current;

        PipeWirePort output = graph.GetPort(outputPortId)
            ?? throw new ArgumentException(
                $"{outputPortId} is not a port in the current graph.", nameof(outputPortId));

        PipeWirePort input = graph.GetPort(inputPortId)
            ?? throw new ArgumentException(
                $"{inputPortId} is not a port in the current graph.", nameof(inputPortId));

        return CreateLink(output, input);
    }

    /// <summary>Links two ports named by id and returns the link once the graph reports it.</summary>
    /// <param name="outputPortId">The port data leaves from.</param>
    /// <param name="inputPortId">The port data arrives at.</param>
    /// <param name="cancellationToken">
    /// Abandons the wait. The request is already on its way, so cancelling does not recall it: the
    /// daemon can still create the link after this throws.
    /// </param>
    /// <exception cref="ArgumentException">
    /// An id is not a port in the current graph, or a port faces the wrong way.
    /// </exception>
    /// <exception cref="PipeWireException">The daemon refused the request.</exception>
    public Task<PipeWireLink> CreateLinkAsync(
        uint outputPortId, uint inputPortId, CancellationToken cancellationToken = default) =>
        CreateLink(outputPortId, inputPortId).ExecuteAsync(cancellationToken);

    internal async Task<PipeWireLink> ExecuteLinkCreationAsync(
        PipeWirePort output, PipeWirePort input, PipeWireObjectOptions options,
        CancellationToken cancellationToken)
    {
        Task<PipeWireLink>? published = null;

        (uint id, PipeWireProxyHandle proxy) = await CreateObjectAsync(
            isLink: true, null, null, output, input, options,
            boundId =>
            {
                _creating[boundId] = false;
                published = AwaitPublishedAsync(boundId, GetLinkOrNull, cancellationToken);
            },
            cancellationToken).ConfigureAwait(false);

        try
        {
            PipeWireLink link = await published!.WaitAsync(cancellationToken).ConfigureAwait(false);
            TakeOwnership(id, proxy);
            return link;
        }
        catch
        {
            AbandonOwnership(id, proxy);
            throw;
        }
    }

    /// <summary>
    /// Removes a link. Works for links this client created and for links owned by anyone else,
    /// which is the common case for a patchbay.
    /// </summary>
    /// <exception cref="PipeWireException">
    /// The daemon refused, usually for want of execute permission on the global. Check
    /// <see cref="PipeWireException.IsPermissionDenied"/> rather than the message.
    /// </exception>
    public Task RemoveLinkAsync(uint linkId, CancellationToken cancellationToken = default) =>
        DestroyGlobalAsync(linkId, cancellationToken);

    /// <summary>
    /// Asks the daemon to destroy any global by id: a node, a link, or anything else the registry
    /// can see. This destroys the object for every client on the machine, not just for this one.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="PipeWireNodeCreation.WithLinger"/>: an object created to
    /// outlive its creator cannot be removed by disconnecting, so this is the only way to take it
    /// down. Removal is asynchronous in the graph - the object leaves
    /// <see cref="Current"/> when the daemon's <c>global_remove</c> arrives, not when this returns.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="id"/> is the core, which is the connection itself and cannot be destroyed.
    /// </exception>
    /// <exception cref="PipeWireException">
    /// The daemon refused, for want of execute permission on the global or because no global has
    /// that id. <see cref="PipeWireException.IsPermissionDenied"/> separates the two.
    /// </exception>
    public unsafe Task DestroyGlobalAsync(uint id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The daemon accepts a destroy of the core and does nothing, reporting neither an error nor
        // a removal - so without this the call looks like it worked. Every other unknown id is left
        // to the daemon, which does report those: an id that has just been removed by somebody else
        // is a race, not a caller mistake, and the daemon's answer is the honest one.
        if (id == Native.PW_ID_CORE)
        {
            throw new ArgumentOutOfRangeException(nameof(id),
                "the core is the connection itself and cannot be destroyed; dispose the context instead.");
        }

        // An object this client created is destroyed through the proxy it was created with, which
        // is the single destruction operation for it. Going through the registry instead leaves the
        // proxy alive until the daemon's global_remove arrives and disposes it, so a dispose racing
        // that window destroys it twice - which PipeWire aborts on.
        if (_ownedProxies.TryRemove(id, out PipeWireProxyHandle? owned))
        {
            owned.Dispose();

            // Still round-tripped. Destroying the proxy only queues the request, and this method's
            // completion is what a caller reads as "the daemon has dealt with it" - the removal is
            // dispatched before the barrier answers, so the graph agrees by the time this returns.
            return CoreSync.RoundTripAsync(_ctx, cancellationToken);
        }

        // destroy is asynchronous: it returns SPA_ASYNC_BIT | seq, never a status. A refusal comes
        // back later as a core error carrying that seq, so the only way to report one is to
        // round-trip the core and watch for it. Testing the return value alone detects nothing.
        //
        // Issued through the round-trip so the listener is already attached: sending it first and
        // subscribing afterwards loses any refusal the daemon answered in between.
        return CoreSync.RoundTripAsync(
            _ctx, () => Native.pw_registry_destroy_global(RegistryHandle, id), cancellationToken);
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
        ImmutableArray<KeyValuePair<string, string>> extra = options.Properties.IsDefault
            ? []
            : options.Properties;

        int needed = FixedPropertyBytes
            + (name is null ? 0 : Encoding.UTF8.GetByteCount(name) + 1)
            + (description is null ? 0 : Encoding.UTF8.GetByteCount(description) + 1);

        foreach ((string key, string value) in extra)
            needed += Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value) + 2;

        // Pinned rather than pooled. The array's address is handed to native code, and a pooled
        // array is an ordinary movable one: a collection between Build and the native call would
        // leave the daemon reading whatever replaced it.
        byte[]? rented = needed > StackScratchBytes
            ? GC.AllocateUninitializedArray<byte>(needed, pinned: true)
            : null;
        try
        {
            Span<byte> scratch = rented ?? stackalloc byte[StackScratchBytes];

            // Eight covers everything the library sets itself; the caller's properties are on top
            // of that, and the count is theirs to decide.
            Span<spa_dict_item> items = extra.Length <= 8
                ? stackalloc spa_dict_item[16]
                : GC.AllocateUninitializedArray<spa_dict_item>(8 + extra.Length, pinned: true);

            // spa_dict_lookup walks the items and returns the FIRST match, so a repeated key is not
            // an override, it is a value that never gets read. The caller's properties therefore go
            // in first and each default is written only if the caller did not name it, which means
            // no key is ever written twice.
            bool Supplied(string key)
            {
                foreach ((string k, _) in extra)
                {
                    if (string.Equals(k, key, StringComparison.Ordinal)) return true;
                }

                return false;
            }
            var dict = new SpaDictBuilder(scratch, items);

            foreach ((string key, string value) in extra) dict.Add(key, value);

            if (options.Linger && !Supplied("object.linger"))
                dict.Add(PipeWireKeys.ObjectLinger, PipeWireKeys.True);

            if (isLink)
            {
                // The endpoints are what the link is, not a property of it, so they are not
                // overridable and are written unconditionally.
                dict.Add(PipeWireKeys.LinkOutputNode, output!.NodeId);
                dict.Add(PipeWireKeys.LinkOutputPort, output.PortId);
                dict.Add(PipeWireKeys.LinkInputNode, input!.NodeId);
                dict.Add(PipeWireKeys.LinkInputPort, input.PortId);

                if (options.Passive && !Supplied("link.passive"))
                    dict.Add(PipeWireKeys.LinkPassive, PipeWireKeys.True);

                return NativeObjectCreation.CreateAsync(
                    _ctx, PipeWireKeys.LinkFactory, PipeWireKeys.InterfaceLink,
                    Native.PW_VERSION_LINK, dict.Build(), cancellationToken, onBound);
            }

            // The factory is what kind of object this is rather than a property of it, so it is
            // not overridable.
            dict.Add(PipeWireKeys.FactoryName, PipeWireKeys.NullAudioSink);

            if (!Supplied("node.name")) dict.Add(PipeWireKeys.NodeName, name!);
            if (!Supplied("node.description")) dict.Add(PipeWireKeys.NodeDescription, description!);

            // Defaults, and only defaults: a stereo sink is the useful shape for a virtual device,
            // and a caller naming either key gets theirs instead.
            if (!Supplied("media.class")) dict.Add(PipeWireKeys.MediaClass, PipeWireKeys.AudioSink);
            if (!Supplied("audio.position")) dict.Add(PipeWireKeys.AudioPosition, PipeWireKeys.StereoPosition);

            return NativeObjectCreation.CreateAsync(
                _ctx, PipeWireKeys.Adapter, PipeWireKeys.InterfaceNode,
                Native.PW_VERSION_NODE, dict.Build(), cancellationToken, onBound);
        }
        finally
        {
            // create_object copies the properties into the daemon's own pw_properties before
            // returning, so the buffer is dead the moment CreateAsync has run its synchronous part.
            // Nothing to return: the fallback is a pinned allocation rather than a pooled array,
            // because its address goes to native code. It is collected like anything else once the
            // native call has copied out of it.
        }
    }

    /// <summary>Scratch that covers every ordinary name without touching the pool.</summary>
    private const int StackScratchBytes = 512;

    /// <summary>
    /// Upper bound on the constant keys and values either creation path writes, NUL bytes included.
    /// </summary>
    /// <remarks>
    /// Computed from the literals rather than guessed, so adding a default cannot silently outgrow
    /// it. The link path is the larger of the two: four endpoint keys with their ten-digit ids, plus
    /// linger and passive. Doubled for headroom, because exhausting it fails a creation that would
    /// otherwise have worked and the scratch is transient either way.
    /// </remarks>
    private static readonly int FixedPropertyBytes = 2 * (
        PipeWireKeys.LinkOutputNode.Length + PipeWireKeys.LinkOutputPort.Length
        + PipeWireKeys.LinkInputNode.Length + PipeWireKeys.LinkInputPort.Length
        + (4 * 11)                                              // one uint32 per endpoint key
        + PipeWireKeys.ObjectLinger.Length + PipeWireKeys.LinkPassive.Length
        + PipeWireKeys.FactoryName.Length + PipeWireKeys.NullAudioSink.Length
        + PipeWireKeys.NodeName.Length + PipeWireKeys.NodeDescription.Length
        + PipeWireKeys.MediaClass.Length + PipeWireKeys.AudioSink.Length
        + PipeWireKeys.AudioPosition.Length + PipeWireKeys.StereoPosition.Length
        + PipeWireKeys.True.Length
        + 32);                                                  // one NUL per key and value

    /// <inheritdoc/>
    /// <summary>Tears down synchronously. Disposal here does no I/O.</summary>
    /// <remarks>
    /// Offered alongside the async form because nothing about this disposal is asynchronous -
    /// the async method completes synchronously - so a consumer should not be forced to write
    /// "await using" for it.
    /// </remarks>
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

        // Watchers are released before the native teardown, so a consumer blocked on the stream is
        // not still waiting while the objects it would report on are being destroyed.
        Action? finishing = Disposing;
        Disposing = null;
        SafeCallback.Raise(finishing, h => h(), LogWatchCompletionThrew);

        UnwindNative();
    }

    /// <summary>
    /// Binds a node so its parameters can be read and written.
    /// </summary>
    /// <param name="nodeId">The node to bind.</param>
    /// <remarks>
    /// The registry says a node exists; binding is what makes its volume, mute and formats
    /// reachable. Each binding is a native object and a listener, so dispose it when done - the
    /// graph snapshot is the right thing to hold for merely watching.
    /// </remarks>
    /// <exception cref="ArgumentException">The id is not a node in the current graph.</exception>
    /// <exception cref="ObjectDisposedException">The registry has been disposed.</exception>
    public unsafe PipeWireNodeControl BindNode(uint nodeId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        PipeWireNode node = Current.GetNode(nodeId)
            ?? throw new ArgumentException($"{nodeId} is not a node in the current graph.", nameof(nodeId));

        return PipeWireNodeControl.Bind(_ctx, RegistryHandle, nodeId, node.InterfaceVersion, _logger);
    }

    /// <summary>
    /// Binds a device so its profiles, routes and port configuration can be read and written.
    /// </summary>
    /// <param name="deviceId">The device to bind.</param>
    /// <exception cref="ArgumentException">The id is not a device in the current graph.</exception>
    /// <exception cref="ObjectDisposedException">The registry has been disposed.</exception>
    public unsafe PipeWireDeviceControl BindDevice(uint deviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        PipeWireDevice device = Current.GetDevice(deviceId)
            ?? throw new ArgumentException($"{deviceId} is not a device in the current graph.", nameof(deviceId));

        return PipeWireDeviceControl.Bind(_ctx, RegistryHandle, deviceId, device.InterfaceVersion, _logger);
    }

    /// <summary>
    /// Binds a client so its permissions and properties can be changed.
    /// </summary>
    /// <param name="clientId">The client to bind.</param>
    /// <remarks>
    /// Changing another client's permissions needs the manager permission, which the daemon grants
    /// to a session manager rather than to an ordinary application. Binding succeeds either way; the
    /// refusal comes when something is written.
    /// </remarks>
    /// <exception cref="ArgumentException">The id is not a client in the current graph.</exception>
    /// <exception cref="ObjectDisposedException">The registry has been disposed.</exception>
    public unsafe PipeWireClientControl BindClient(uint clientId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        PipeWireClient client = Current.GetClient(clientId)
            ?? throw new ArgumentException($"{clientId} is not a client in the current graph.", nameof(clientId));

        return PipeWireClientControl.Bind(_ctx, RegistryHandle, clientId, client.InterfaceVersion, _logger);
    }

    /// <summary>
    /// Binds a port so its parameters can be read.
    /// </summary>
    /// <param name="portId">The port to bind.</param>
    /// <remarks>
    /// The registry says a port exists, its direction and whose node it is. The format negotiated
    /// on it and the latency the graph settled on for it live on the port's own params, which need
    /// the proxy bound. Read-only: a port's format comes out of the negotiation between the nodes
    /// at either end.
    /// </remarks>
    /// <exception cref="ArgumentException">The id is not a port in the current graph.</exception>
    /// <exception cref="ObjectDisposedException">The registry has been disposed.</exception>
    public unsafe PipeWirePortControl BindPort(uint portId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        PipeWirePort port = Current.GetPort(portId)
            ?? throw new ArgumentException($"{portId} is not a port in the current graph.", nameof(portId));

        return PipeWirePortControl.Bind(_ctx, RegistryHandle, portId, port.InterfaceVersion, _logger);
    }

    /// <summary>
    /// Binds the daemon's security context, for opening a sandboxed connection point.
    /// </summary>
    /// <param name="securityContextId">The security context to bind.</param>
    /// <remarks>
    /// A session manager or sandbox supervisor uses this to hand the daemon a socket that
    /// sandboxed clients connect through, with the properties they are all given. An ordinary
    /// application has no reason to, and the daemon refuses it one.
    /// </remarks>
    /// <exception cref="ArgumentException">The id is not a security context in the current graph.</exception>
    /// <exception cref="ObjectDisposedException">The registry has been disposed.</exception>
    public unsafe PipeWireSecurityContextControl BindSecurityContext(uint securityContextId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        PipeWireSecurityContext context = Current.SecurityContext is { } found && found.Id == securityContextId
            ? found
            : throw new ArgumentException(
                $"{securityContextId} is not a security context in the current graph.",
                nameof(securityContextId));

        return PipeWireSecurityContextControl.Bind(
            _ctx, RegistryHandle, securityContextId, context.InterfaceVersion, _logger);
    }

    /// <summary>
    /// Binds the profiler so its periodic reports can be read.
    /// </summary>
    /// <param name="profilerId">The profiler to bind.</param>
    /// <remarks>
    /// The daemon publishes at most one, and only when it was built with profiling. Each report is
    /// a Profiler object carrying the graph's timings for one cycle, which is what <c>pw-top</c>
    /// renders. Binding it makes the daemon start producing them, so dispose it when done.
    /// </remarks>
    /// <exception cref="ArgumentException">The id is not the profiler in the current graph.</exception>
    /// <exception cref="ObjectDisposedException">The registry has been disposed.</exception>
    public unsafe PipeWireProfilerReader BindProfiler(uint profilerId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        PipeWireProfiler profiler = Current.Profiler is { } found && found.Id == profilerId
            ? found
            : throw new ArgumentException(
                $"{profilerId} is not the profiler in the current graph.", nameof(profilerId));

        return PipeWireProfilerReader.Bind(
            _ctx, RegistryHandle, profilerId, profiler.InterfaceVersion, _logger);
    }

    /// <summary>
    /// Binds a link so its state can be watched.
    /// </summary>
    /// <param name="linkId">The link to bind.</param>
    /// <remarks>
    /// The registry says a link exists and which ports it joins; it does not say whether anything
    /// is flowing. Negotiating, paused, active and errored are all reported on the link's own info
    /// event, which needs the proxy bound, so this is per link rather than something the graph
    /// carries for all of them: a session with hundreds of links should not pay a proxy each to
    /// answer a question about a few. Await <see cref="PipeWireLinkControl.ReadyAsync"/> before
    /// reading, or the state is whatever it was initialised to.
    /// </remarks>
    /// <exception cref="ArgumentException">The id is not a link in the current graph.</exception>
    /// <exception cref="ObjectDisposedException">The registry has been disposed.</exception>
    public unsafe PipeWireLinkControl BindLink(uint linkId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        PipeWireLink link = Current.GetLink(linkId)
            ?? throw new ArgumentException($"{linkId} is not a link in the current graph.", nameof(linkId));

        return PipeWireLinkControl.Bind(_ctx, RegistryHandle, linkId, link.InterfaceVersion, _logger);
    }

    /// <summary>
    /// Binds a metadata store so its entries can be read and written.
    /// </summary>
    /// <param name="storeId">The store to bind.</param>
    /// <remarks>
    /// The store pushes everything it holds as soon as the listener attaches, so await
    /// <see cref="PipeWireMetadataStore.ReadyAsync"/> before reading or the answer is whatever
    /// happened to have arrived.
    /// </remarks>
    /// <exception cref="ArgumentException">The id is not a metadata store in the current graph.</exception>
    /// <exception cref="ObjectDisposedException">The registry has been disposed.</exception>
    public unsafe PipeWireMetadataStore BindMetadataStore(uint storeId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        IPipeWireObject? store = Current.Objects.FirstOrDefault(o => o.Id == storeId);
        if (store is not PipeWireMetadataObject metadata)
        {
            throw new ArgumentException(
                $"{storeId} is not a metadata store in the current graph.", nameof(storeId));
        }

        return PipeWireMetadataStore.Bind(_ctx, RegistryHandle, storeId, metadata.InterfaceVersion, _logger);
    }

    /// <summary>
    /// Binds a metadata store by name, such as <c>default</c> or <c>settings</c>.
    /// </summary>
    /// <param name="name">The store name.</param>
    /// <returns>The store, or <see langword="null"/> if this session has none by that name.</returns>
    /// <remarks>
    /// By name rather than id because the id changes between sessions while the name does not. A
    /// session with no session manager running has no <c>default</c> store at all, which is why this
    /// answers with null rather than throwing.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The registry has been disposed.</exception>
    public PipeWireMetadataStore? BindMetadataStore(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(name);

        PipeWireMetadataObject? store = Current.GetMetadataStore(name);
        return store is null ? null : BindMetadataStore(store.Id);
    }

    /// <summary>
    /// Records a global that is not a node, port or link.
    /// </summary>
    /// <remarks>
    /// A kind this library does not model is dropped rather than stored as an unnamed shape: the
    /// registry would otherwise report objects a caller can neither identify nor act on. The drop is
    /// logged so a daemon growing a new interface shows up as a message rather than as silence.
    /// </remarks>
    private unsafe void AddOtherObject(
        uint id, PipeWirePermissions permissions, uint version, ReadOnlySpan<byte> kind, spa_dict* props)
    {
        IPipeWireObject? parsed = null;

        if      (kind.SequenceEqual(PipeWireKeys.InterfaceDevice))
            parsed = PipeWireGlobalParser.ParseDevice(id, permissions, version, props);
        else if (kind.SequenceEqual(PipeWireKeys.InterfaceClient))
            parsed = PipeWireGlobalParser.ParseClient(id, permissions, version, props);
        else if (kind.SequenceEqual(PipeWireKeys.InterfaceFactory))
            parsed = PipeWireGlobalParser.ParseFactory(id, permissions, version, props);
        else if (kind.SequenceEqual(PipeWireKeys.InterfaceModule))
            parsed = PipeWireGlobalParser.ParseModule(id, permissions, version, props);
        else if (kind.SequenceEqual(PipeWireKeys.InterfaceMetadata))
            parsed = PipeWireGlobalParser.ParseMetadata(id, permissions, version, props);
        else if (kind.SequenceEqual(PipeWireKeys.InterfaceCore))
            parsed = PipeWireGlobalParser.ParseCore(id, permissions, version, props);
        else if (kind.SequenceEqual(PipeWireKeys.InterfaceProfiler))
            parsed = new PipeWireProfiler(id, permissions, version);
        else if (kind.SequenceEqual(PipeWireKeys.InterfaceSecurityContext))
            parsed = new PipeWireSecurityContext(id, permissions, version);

        if (parsed is null)
        {
            LogUnmodelledGlobal(id, Encoding.UTF8.GetString(kind));
            return;
        }

        _objects[id] = parsed;
        LogObjectAdded(parsed.Kind.ToString(), id);
        Publish();
        CompletePublishWaiter(id, parsed);
        RaiseGraphChanged();
    }

    // - Native callbacks -

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnGlobal(void* data, uint id, uint permissions, sbyte* type, uint version, spa_dict* props)
    {
        // An exception escaping a reverse P/Invoke aborts the process, so nothing below may throw -
        // including the handle lookup, which throws for a handle that has already been freed.
        PipeWireRegistry? self = FromData(data);
        if (self is null || self._disposed) return;

        try
        {
            // A global with no interface name is not one this library can place. Checked rather
            // than left to the span helper's own argument check, so the outcome does not depend on
            // which exception that helper happens to throw.
            if (type is null)
            {
                self.LogGlobalWithoutType(id);
                self.FailPublishWaiter(id, "the daemon published it with no interface type");
                return;
            }

            var perms = (PipeWirePermissions)permissions;

            ReadOnlySpan<byte> kind = DaemonText.Bytes(type);

            if      (kind.SequenceEqual(PipeWireKeys.InterfaceNode)) self.AddNode(id, perms, version, props);
            else if (kind.SequenceEqual(PipeWireKeys.InterfacePort)) self.AddPort(id, perms, version, props);
            else if (kind.SequenceEqual(PipeWireKeys.InterfaceLink)) self.AddLink(id, perms, version, props);
            else self.AddOtherObject(id, perms, version, kind, props);
        }
        catch (Exception ex)
        {
            self.LogHandlerFaulted("global", ex);

            // The node parser throws rather than reporting failure, so its failures land here. The
            // waiter still has to be answered or the creation that asked for this id hangs.
            self.FailPublishWaiter(id, ex.Message);
        }
    }

    private unsafe void AddNode(uint id, PipeWirePermissions permissions, uint version, spa_dict* props)
    {
        PipeWireNode source = PipeWireGlobalParser.ParseNode(id, permissions, version, props);
        string? name = source.NodeName;
        string? mediaClass = source.MediaClass;

        // No lock. The collection is concurrent, every write to it comes from the loop thread,
        // and nothing else takes this monitor - so it excludes nobody while making the field look
        // like it has a lock discipline that readers would have to respect.
        _sources[id] = source;
        LogNodeAdded(id, name, mediaClass);
        Publish();
        CompletePublishWaiter(id, source);

        RaiseIsolated(NodeAdded, source, nameof(NodeAdded));
        RaiseGraphChanged();
    }

    private unsafe void AddPort(uint id, PipeWirePermissions permissions, uint version, spa_dict* props)
    {
        if (!PipeWireGlobalParser.TryParsePort(
                id, permissions, version, props,
                out PipeWirePort? parsed, out string reason, out string? offending))
        {
            LogPortSkipped(id, reason, offending);
            FailPublishWaiter(id, reason);
            return;
        }

        PipeWirePort port = parsed!;
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
            FailPublishWaiter(id, reason);
            return;
        }

        PipeWireLink link = parsed!;
        uint outputNode = link.LinkOutputNode, outputPort = link.LinkOutputPort;
        uint inputNode = link.LinkInputNode, inputPort = link.LinkInputPort;

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
        PipeWireRegistry? self = FromData(data);
        if (self is null || self._disposed) return;

        try
        {
            self.ReleaseOwnedProxy(id);

            // Anyone still waiting for this id to be published never will be: the object existed
            // and is gone. Without this the creation that asked for it waits until its own token
            // fires, which is a hang where the honest answer is that it was destroyed first.
            self.FailPublishWaiter(id, "it was destroyed before it reached the registry");

            if (self._sources.TryRemove(id, out _))
            {
                self.LogRemoved("node", id);

                // The daemon removes a node's ports and links in their own events, which arrive
                // after this one. Publishing in between leaves a snapshot holding ports whose node
                // is not in it and links whose endpoints are not either, so GetNodeForPort returns
                // null for a port the snapshot still lists - and the snapshot documents itself as
                // internally consistent. They go with the node, and their own removals then find
                // nothing left to do.
                self.CascadeRemove(id);

                self.Publish();
                self.RaiseIsolated(self.NodeRemoved, id, nameof(NodeRemoved));
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
            else if (self._objects.TryRemove(id, out IPipeWireObject? gone))
            {
                self.LogRemoved(gone.Kind.ToString(), id);
                self.Publish();
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

    [LoggerMessage(EventId = 10, Level = LogLevel.Error, Message = "a graph watcher's completion handler threw during disposal")]
    private partial void LogWatchCompletionThrew(Exception ex);

    [LoggerMessage(EventId = 3, Level = LogLevel.Trace, Message = "link {Id} {OutputNode}.{OutputPort} -> {InputNode}.{InputPort}")]
    private partial void LogLinkAdded(uint id, uint outputNode, uint outputPort, uint inputNode, uint inputPort);

    [LoggerMessage(EventId = 4, Level = LogLevel.Trace, Message = "removed {Kind} {Id}")]
    private partial void LogRemoved(string kind, uint id);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "skipped port {Id}: {Reason} ({Value})")]
    private partial void LogPortSkipped(uint id, string reason, string? value);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "skipped link {Id}: {Reason} ({Value})")]
    private partial void LogLinkSkipped(uint id, string reason, string? value);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning,
        Message = "the daemon published global {Id} with no interface type")]
    private partial void LogGlobalWithoutType(uint id);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "a {Event} handler threw")]
    private partial void LogHandlerFaulted(string @event, Exception exception);

    [LoggerMessage(EventId = 8, Level = LogLevel.Trace, Message = "{Kind} {Id}")]
    private partial void LogObjectAdded(string kind, uint id);

    [LoggerMessage(EventId = 9, Level = LogLevel.Debug,
                   Message = "global {Id} dropped: this version does not model {Interface}")]
    private partial void LogUnmodelledGlobal(uint id, string @interface);

}
