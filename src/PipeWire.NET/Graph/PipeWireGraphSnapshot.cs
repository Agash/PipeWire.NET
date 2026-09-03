using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

/// <summary>
/// An immutable, internally consistent view of the PipeWire graph at one moment.
/// </summary>
/// <remarks>
/// <para>
/// The registry publishes a new snapshot on every graph change. Holding one is free and reading it
/// needs no lock, so a consumer can walk the whole graph without racing the loop thread or seeing
/// membership shift mid-enumeration.
/// </para>
/// <para>
/// Relationships are resolved here rather than on the entities, which keeps those pure data with no
/// reference back to the registry.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class PipeWireGraphSnapshot
{
    /// <summary>An empty graph, published before the first enumeration completes.</summary>
    public static PipeWireGraphSnapshot Empty { get; } = new(0, [], [], [], []);

    internal PipeWireGraphSnapshot(
        long version,
        IEnumerable<PipeWireNode> nodes,
        IEnumerable<PipeWirePort> ports,
        IEnumerable<PipeWireLink> links,
        IEnumerable<IPipeWireObject>? others = null)
    {
        Version = version;
        Nodes = [.. nodes];
        Ports = [.. ports];
        Links = [.. links];
        Objects = others is null ? [] : [.. others];
    }

    // Built on first read: a snapshot is published per registry event and most are never queried.
    // EnsureInitialized rather than "??=" because a racing "??=" built the index once per reader.

    private FrozenDictionary<uint, PipeWireNode>? _nodesById;
    private object? _nodesByIdLock;

    private FrozenDictionary<uint, PipeWirePort>? _portsById;
    private object? _portsByIdLock;

    private FrozenDictionary<uint, PipeWireLink>? _linksById;
    private object? _linksByIdLock;

    private FrozenDictionary<uint, ImmutableArray<PipeWirePort>>? _portsByNode;
    private object? _portsByNodeLock;

    private FrozenDictionary<uint, ImmutableArray<PipeWireLink>>? _linksByNode;
    private object? _linksByNodeLock;

    private FrozenDictionary<uint, ImmutableArray<PipeWireLink>>? _inputLinksByPort;
    private object? _inputLinksByPortLock;

    private FrozenDictionary<uint, ImmutableArray<PipeWireLink>>? _outputLinksByPort;
    private object? _outputLinksByPortLock;

    private FrozenDictionary<uint, IPipeWireObject>? _objectsById;
    private object? _objectsByIdLock;

    private FrozenDictionary<uint, PipeWireNode> NodesById =>
        LazyInitializer.EnsureInitialized(ref _nodesById, ref _nodesByIdLock,
            () => Nodes.ToFrozenDictionary(static n => n.NodeId));

    private FrozenDictionary<uint, PipeWirePort> PortsById =>
        LazyInitializer.EnsureInitialized(ref _portsById, ref _portsByIdLock,
            () => Ports.ToFrozenDictionary(static p => p.PortId));

    private FrozenDictionary<uint, PipeWireLink> LinksById =>
        LazyInitializer.EnsureInitialized(ref _linksById, ref _linksByIdLock,
            () => Links.ToFrozenDictionary(static l => l.LinkId));

    private FrozenDictionary<uint, ImmutableArray<PipeWireLink>> LinksByNode =>
        LazyInitializer.EnsureInitialized(ref _linksByNode, ref _linksByNodeLock,
            () => Links.SelectMany(l => new[] { (Node: l.LinkInputNode, Link: l), (Node: l.LinkOutputNode, Link: l) })
                       .GroupBy(static x => x.Node)
                       .ToFrozenDictionary(static g => g.Key,
                                           static g => g.Select(static x => x.Link).Distinct().ToImmutableArray()));

    private FrozenDictionary<uint, ImmutableArray<PipeWirePort>> PortsByNode =>
        LazyInitializer.EnsureInitialized(ref _portsByNode, ref _portsByNodeLock,
            () => Ports.GroupBy(static p => p.NodeId)
                       .ToFrozenDictionary(static g => g.Key, static g => g.ToImmutableArray()));

    private FrozenDictionary<uint, ImmutableArray<PipeWireLink>> InputLinksByPort =>
        LazyInitializer.EnsureInitialized(ref _inputLinksByPort, ref _inputLinksByPortLock,
            () => Links.GroupBy(static l => l.LinkInputPort)
                       .ToFrozenDictionary(static g => g.Key, static g => g.ToImmutableArray()));

    private FrozenDictionary<uint, ImmutableArray<PipeWireLink>> OutputLinksByPort =>
        LazyInitializer.EnsureInitialized(ref _outputLinksByPort, ref _outputLinksByPortLock,
            () => Links.GroupBy(static l => l.LinkOutputPort)
                       .ToFrozenDictionary(static g => g.Key, static g => g.ToImmutableArray()));

    private FrozenDictionary<uint, IPipeWireObject> ObjectsById =>
        LazyInitializer.EnsureInitialized(ref _objectsById, ref _objectsByIdLock,
            () => Objects.ToFrozenDictionary(static o => o.Id));

    /// <summary>
    /// Increases by one per published snapshot. A local counter for spotting missed publications;
    /// unrelated to any PipeWire sequence number.
    /// </summary>
    public long Version { get; }

    /// <summary>Every node in the graph.</summary>
    public ImmutableArray<PipeWireNode> Nodes { get; }

    /// <summary>Every port in the graph.</summary>
    public ImmutableArray<PipeWirePort> Ports { get; }

    /// <summary>Every link in the graph.</summary>
    public ImmutableArray<PipeWireLink> Links { get; }

    /// <summary>
    /// Every object that is not a node, port or link: devices, clients, factories, modules,
    /// metadata stores, and the daemon singletons.
    /// </summary>
    /// <remarks>
    /// Kept as one collection because these do not participate in routing - nothing links to a
    /// module. The typed properties below filter it for the kind a caller actually wants.
    /// </remarks>
    public ImmutableArray<IPipeWireObject> Objects { get; }

    /// <summary>Every hardware device the daemon knows of.</summary>
    public ImmutableArray<PipeWireDevice> Devices => OfKind<PipeWireDevice>(ref _devices);

    /// <summary>Every client connected to the daemon, including this one.</summary>
    public ImmutableArray<PipeWireClient> Clients => OfKind<PipeWireClient>(ref _clients);

    /// <summary>Every factory the daemon can create objects with.</summary>
    public ImmutableArray<PipeWireFactory> Factories => OfKind<PipeWireFactory>(ref _factories);

    /// <summary>Every module loaded into the daemon.</summary>
    public ImmutableArray<PipeWireModule> Modules => OfKind<PipeWireModule>(ref _modules);

    /// <summary>Every metadata store, such as <c>default</c> and <c>settings</c>.</summary>
    public ImmutableArray<PipeWireMetadataObject> MetadataStores =>
        OfKind<PipeWireMetadataObject>(ref _metadata);

    /// <summary>The daemon core, or <see langword="null"/> if it has not been seen yet.</summary>
    public PipeWireCoreObject? Core => Single<PipeWireCoreObject>();

    /// <summary>The daemon profiler, or <see langword="null"/> if the daemon has none.</summary>
    public PipeWireProfiler? Profiler => Single<PipeWireProfiler>();

    /// <summary>The security context, or <see langword="null"/> if the daemon offers none.</summary>
    public PipeWireSecurityContext? SecurityContext => Single<PipeWireSecurityContext>();

    // One lock for all of them, created with the snapshot. Creating one lazily per collection races:
    // "lock (gate ??= new object())" is two operations, so two threads arriving together each make
    // their own object, each locks it, and both enter the section at once.

    private ImmutableArray<PipeWireDevice> _devices;
    private ImmutableArray<PipeWireClient> _clients;
    private ImmutableArray<PipeWireFactory> _factories;
    private ImmutableArray<PipeWireModule> _modules;
    private ImmutableArray<PipeWireMetadataObject> _metadata;

    // Filtered on first read and kept, for the same reason the indexes are: most snapshots are
    // published and replaced without anyone asking.
    //
    // Published with a compare-exchange rather than under a lock. Two readers arriving together
    // both build the array and one of them wins, which costs a wasted filter and gives every
    // reader the same instance; a lock would serialise readers of an immutable snapshot for no
    // benefit, and the unlocked fast path it needed was reading a field the lock was meant to
    // protect.
    private ImmutableArray<T> OfKind<T>(ref ImmutableArray<T> cache)
        where T : class, IPipeWireObject
    {
        ImmutableArray<T> current = cache;
        if (!current.IsDefault) return current;

        ImmutableInterlocked.InterlockedInitialize(ref cache, [.. Objects.OfType<T>()]);
        return cache;
    }

    private T? Single<T>() where T : class, IPipeWireObject
    {
        foreach (IPipeWireObject candidate in Objects)
        {
            if (candidate is T match) return match;
        }

        return null;
    }

    /// <summary>The node with this id, or <see langword="null"/> if the graph has none.</summary>
    public PipeWireNode? GetNode(uint id) => NodesById.GetValueOrDefault(id);

    /// <summary>The port with this id, or <see langword="null"/> if the graph has none.</summary>
    public PipeWirePort? GetPort(uint id) => PortsById.GetValueOrDefault(id);

    /// <summary>The link with this id, or <see langword="null"/> if the graph has none.</summary>
    public PipeWireLink? GetLink(uint id) => LinksById.GetValueOrDefault(id);

    /// <summary>The node with this id, if the graph has one.</summary>
    public bool TryGetNode(uint id, [NotNullWhen(true)] out PipeWireNode? value) =>
        NodesById.TryGetValue(id, out value);

    /// <summary>The port with this id, if the graph has one.</summary>
    public bool TryGetPort(uint id, [NotNullWhen(true)] out PipeWirePort? value) =>
        PortsById.TryGetValue(id, out value);

    /// <summary>The link with this id, if the graph has one.</summary>
    public bool TryGetLink(uint id, [NotNullWhen(true)] out PipeWireLink? value) =>
        LinksById.TryGetValue(id, out value);

    /// <summary>The device with this id, or <see langword="null"/> if the graph has none.</summary>
    public PipeWireDevice? GetDevice(uint id) => ObjectsById.GetValueOrDefault(id) as PipeWireDevice;

    /// <summary>The client with this id, or <see langword="null"/> if the graph has none.</summary>
    public PipeWireClient? GetClient(uint id) => ObjectsById.GetValueOrDefault(id) as PipeWireClient;

    /// <summary>The factory with this id, or <see langword="null"/> if the graph has none.</summary>
    public PipeWireFactory? GetFactory(uint id) => ObjectsById.GetValueOrDefault(id) as PipeWireFactory;

    /// <summary>The module with this id, or <see langword="null"/> if the graph has none.</summary>
    public PipeWireModule? GetModule(uint id) => ObjectsById.GetValueOrDefault(id) as PipeWireModule;

    /// <summary>The metadata store with this name, or <see langword="null"/> if there is none.</summary>
    /// <param name="name">The store name, such as <c>default</c>.</param>
    public PipeWireMetadataObject? GetMetadataStore(string name)
    {
        foreach (PipeWireMetadataObject store in MetadataStores)
        {
            if (string.Equals(store.MetadataName, name, StringComparison.Ordinal))
                return store;
        }

        return null;
    }

    /// <summary>Looks up any object by id, whatever kind it is.</summary>
    public bool TryGetObject(uint id, [NotNullWhen(true)] out IPipeWireObject? value)
    {
        value = GetNode(id)
                ?? (IPipeWireObject?)GetPort(id)
                ?? GetLink(id)
                ?? ObjectsById.GetValueOrDefault(id);
        return value is not null;
    }

    /// <summary>The ports belonging to a node, or empty if it has none.</summary>
    public ImmutableArray<PipeWirePort> GetPortsForNode(uint nodeId) =>
        PortsByNode.GetValueOrDefault(nodeId, []);

    /// <summary>The links feeding into a port.</summary>
    public ImmutableArray<PipeWireLink> GetInputLinksForPort(uint portId) =>
        InputLinksByPort.GetValueOrDefault(portId, []);

    /// <summary>The links sourcing from a port.</summary>
    public ImmutableArray<PipeWireLink> GetOutputLinksForPort(uint portId) =>
        OutputLinksByPort.GetValueOrDefault(portId, []);

    /// <summary>Every link attached to a node, in either direction, each reported once.</summary>
    /// <remarks>
    /// A link whose two ends belong to the same node - an internal loopback, which a filter creates
    /// - is reachable from that node's input port and from its output port. Walking the ports alone
    /// would report it twice and make a caller counting connections double-count exactly that case,
    /// so the ids seen are tracked.
    /// </remarks>
    public ImmutableArray<PipeWireLink> GetLinksForNode(uint nodeId) =>
        LinksByNode.TryGetValue(nodeId, out ImmutableArray<PipeWireLink> links) ? links : [];
}
