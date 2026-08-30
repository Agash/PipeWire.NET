using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using PipeWire.NET.Generated;

namespace PipeWire.NET;

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
public sealed class PipeWireRegistry : IAsyncDisposable
{
    internal readonly PipeWireContext _ctx;
    internal readonly ConcurrentDictionary<uint, PipeWireSource> _sources = new();
    internal readonly ConcurrentDictionary<uint, PipeWirePort> _ports = new();
    internal readonly ConcurrentDictionary<uint, PipeWireLink> _links = new();

    private unsafe pw_registry*        _registry;
    private unsafe pw_registry_events* _events;
    private spa_hook            _hook;
    private GCHandle            _selfHandle;
    private volatile bool       _disposed;
    private TaskCompletionSource _initialEnumeration = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Raised when a new node appears in the graph.</summary>
    public event Action<PipeWireSource>? SourceAdded;

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
        _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);
        InitializeNative();
    }

    private unsafe void InitializeNative()
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

            fixed (spa_hook* hookPtr = &_hook)
                Native.pw_registry_add_listener(_registry, hookPtr, _events,
                    (void*)GCHandle.ToIntPtr(_selfHandle));
        }
    }

    /// <summary>Snapshot of currently-visible sources.</summary>
    public IReadOnlyCollection<PipeWireSource> Sources => _sources.Values.ToArray();

    /// <summary>
    /// Completes once the initial registry enumeration has finished. Optional - events still
    /// fire as the registry populates, this just gives consumers a convenient "ready" gate.
    /// </summary>
    /// <remarks>
    /// Implementation: we treat the first <c>global</c> event as proof that the registry is
    /// alive, and use a short tail delay to let the burst of initial enumerations drain.
    /// </remarks>
    public Task WaitForInitialEnumerationAsync(TimeSpan? settleDelay = null, CancellationToken cancellationToken = default)
    {
        var settle = settleDelay ?? TimeSpan.FromMilliseconds(250);
        return WaitImplAsync(settle, cancellationToken);

        async Task WaitImplAsync(TimeSpan delay, CancellationToken ct)
        {
            await _initialEnumeration.Task.WaitAsync(ct).ConfigureAwait(false);
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        DisposeNative();
        if (_selfHandle.IsAllocated) _selfHandle.Free();
        return ValueTask.CompletedTask;
    }

    private unsafe void DisposeNative()
    {
        if (_registry is not null)
        {
            using (_ctx.Lock())
                Native.pw_registry_destroy(_registry);
            _registry = null;
        }
        if (_events is not null)
        {
            NativeMemory.Free(_events);
            _events = null;
        }
    }

    // - Native callbacks -

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnGlobal(void* data, uint id, uint permissions, sbyte* type, uint version, spa_dict* props)
    {
        var self = (PipeWireRegistry?)GCHandle.FromIntPtr((nint)data).Target;
        if (self is null || self._disposed) return;

        string? typeStr = PtrToUtf8(type);
        switch (typeStr)
        {
            case "PipeWire:Interface:Node":
                string? nodeName    = TryReadKey(props, Native.PW_KEY_NODE_NAME);
                string? nodeDescription = TryReadKey(props, Native.PW_KEY_NODE_DESCRIPTION);
                string? nodeNick    = TryReadKey(props, Native.PW_KEY_NODE_NICK);
                string? mediaClass  = TryReadKey(props, Native.PW_KEY_MEDIA_CLASS);

                var source = new PipeWireSource(self, id, nodeName, nodeDescription, mediaClass, nodeNick);
                self._sources[id] = source;
                Debug.WriteLine($"Loaded Node {id} '{source.NodeName}' ({mediaClass}): {nodeDescription}");

                try { self.SourceAdded?.Invoke(source); }
                catch { /* event handler should not break the main loop */ }

                break;
            case "PipeWire:Interface:Port":
                string? portNodeId = TryReadKey(props, Native.PW_KEY_NODE_ID);
                string? portName = TryReadKey(props, Native.PW_KEY_PORT_NAME);
                string? portDirection = TryReadKey(props, Native.PW_KEY_PORT_DIRECTION);
                string? portMonitor = TryReadKey(props, Native.PW_KEY_PORT_MONITOR);
                string? portExclusive = TryReadKey(props, Native.PW_KEY_PORT_EXCLUSIVE);

                if (portNodeId == null)
                {
                    Debug.WriteLine($"Port {id} was loaded without a node id!");
                    return;
                }

                if (!uint.TryParse(portNodeId, out uint parsedPortNodeId))
                {
                    Debug.WriteLine($"Port {id} was loaded with an invalid node id '{portNodeId}'");
                    return;
                }

                if (!Enum.TryParse(typeof(PipeWirePortDirection), portDirection, true, out object? parsedPortDirection))
                {
                    Debug.WriteLine($"Port {id} was loaded with an invalid direction attribute '{portDirection}'");
                    return;
                }

                var port = new PipeWirePort(self, id, parsedPortNodeId, portName,
                    (PipeWirePortDirection) parsedPortDirection,
                    portMonitor != null, portExclusive != null);
                self._ports[id] = port;
                Debug.WriteLine($"Loaded Port {id} '{port.PortName}' ({port.PortDirection}) of Node {port.NodeId}");

                try { self.PortAdded?.Invoke(port); }
                catch { /* event handler should not break the main loop */ }

                break;
            case "PipeWire:Interface:Link":
                string? linkInputNodeId = TryReadKey(props, Native.PW_KEY_LINK_INPUT_NODE);
                string? linkInputPortId = TryReadKey(props, Native.PW_KEY_LINK_INPUT_PORT);
                string? linkOutputNodeId = TryReadKey(props, Native.PW_KEY_LINK_OUTPUT_NODE);
                string? linkOutputPortId = TryReadKey(props, Native.PW_KEY_LINK_OUTPUT_PORT);

                if (!uint.TryParse(linkInputNodeId, out uint parsedLinkInputNodeId))
                {
                    Debug.WriteLine($"Link {id} was loaded with an invalid input node id '{linkInputNodeId}'");
                    return;
                }

                if (!uint.TryParse(linkInputPortId, out uint parsedLinkInputPortId))
                {
                    Debug.WriteLine($"Link {id} was loaded with an invalid input port id '{linkInputPortId}'");
                    return;
                }

                if (!uint.TryParse(linkOutputNodeId, out uint parsedLinkOutputNodeId))
                {
                    Debug.WriteLine($"Link {id} was loaded with an invalid output node id '{linkOutputNodeId}'");
                    return;
                }

                if (!uint.TryParse(linkOutputPortId, out uint parsedLinkOutputPortId))
                {
                    Debug.WriteLine($"Link {id} was loaded with an invalid output port id '{linkOutputPortId}'");
                    return;
                }

                var link = new PipeWireLink(self, id,
                    parsedLinkInputNodeId, parsedLinkInputPortId,
                    parsedLinkOutputNodeId, parsedLinkOutputPortId);
                self._links[id] = link;
                Debug.WriteLine($"Loaded Link {id} ({link.LinkOutputNode}.{link.LinkOutputPort} -> {link.LinkInputNode}.{link.LinkInputPort})");

                try { self.LinkAdded?.Invoke(link); }
                catch { /* event handler should not break the main loop */ }

                break;
            default:
                Debug.WriteLine($"Not enumerating result of type {typeStr}");
                break;
        }

        self._initialEnumeration.TrySetResult();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnGlobalRemove(void* data, uint id)
    {
        var self = (PipeWireRegistry?)GCHandle.FromIntPtr((nint)data).Target;
        if (self is null || self._disposed) return;

        if (self._sources.TryRemove(id, out _))
        {
            try { self.SourceRemoved?.Invoke(id); }
            catch { /* swallow */ }
        } else if (self._ports.TryRemove(id, out _))
        {
            try { self.PortRemoved?.Invoke(id); }
            catch { /* swallow */ }
        } else if (self._links.TryRemove(id, out _))
        {
            try { self.LinkRemoved?.Invoke(id); }
            catch { /* swallow */ }
        }
    }

    // - spa_dict helpers -

    private static unsafe string? TryReadKey(spa_dict* dict, string key)
    {
        if (dict is null) return null;

        int nItems = (int)dict->n_items;
        if (nItems == 0 || dict->items is null) return null;

        ReadOnlySpan<byte> keyUtf8 = Encoding.UTF8.GetBytes(key + '\0');

        for (int i = 0; i < nItems; i++)
        {
            spa_dict_item* item = dict->items + i;
            if (item->key is null) continue;
            if (Utf8Equals(item->key, keyUtf8))
                return PtrToUtf8(item->value);
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe string? PtrToUtf8(sbyte* p) =>
        p is null ? null : Marshal.PtrToStringUTF8((nint)p);

    private static unsafe bool Utf8Equals(sbyte* nullTerminated, ReadOnlySpan<byte> needle)
    {
        int i = 0;
        for (; i < needle.Length - 1; i++)              // skip the trailing \0 in needle
            if ((byte)nullTerminated[i] != needle[i]) return false;
        return nullTerminated[i] == 0;
    }

    /// <summary>
    /// todo: write docs
    /// </summary>
    /// <param name="feedPortId"></param>
    /// <param name="sinkPortId"></param>
    /// <returns></returns>
    public Task<PipeWireLink> WaitForLink(uint feedPortId, uint sinkPortId)
    {
        var waiter = new WaitForLinkRegistration(this, feedPortId, sinkPortId);
        waiter.CheckAlreadyExists();
        return waiter._completion.Task;
    }

    private sealed class WaitForLinkRegistration
    {
        internal readonly TaskCompletionSource<PipeWireLink> _completion;
        private readonly PipeWireRegistry _registry;
        private readonly uint _feedPortId;
        private readonly uint _sinkPortId;

        public WaitForLinkRegistration(PipeWireRegistry registry, uint feedPortId, uint sinkPortId)
        {
            _completion = new TaskCompletionSource<PipeWireLink>();

            _registry = registry;
            _feedPortId = feedPortId;
            _sinkPortId = sinkPortId;

            registry.LinkAdded += OnLinkRegister;
        }

        internal void CheckAlreadyExists()
        {
            var existing = _registry._links.Values
                .FirstOrDefault(link => link.LinkOutputPort == _feedPortId && link.LinkInputPort == _sinkPortId);
            if (existing != null)
                _completion.TrySetResult(existing);
        }

        private void OnLinkRegister(PipeWireLink link)
        {
            if (_completion.Task.IsCompleted) return;
            if (link.LinkOutputPort != _feedPortId || link.LinkInputPort != _sinkPortId) return;

            _completion.TrySetResult(link);
            _registry.LinkAdded -= OnLinkRegister;
        }
    }
}
