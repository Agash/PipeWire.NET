using System.Runtime.Versioning;
using System.Text;
using PipeWire.NET.Generated;

namespace PipeWire.NET;

/// <summary>
/// todo: write docs
/// </summary>
[SupportedOSPlatform("linux")]
public sealed record PipeWireLink
{
    /// <summary>
    /// todo: write docs
    /// </summary>
    /// <param name="feed"></param>
    /// <param name="sink"></param>
    /// <exception cref="Exception"></exception>
    public static unsafe Task<PipeWireLink> Create(PipeWirePort feed, PipeWirePort sink)
    {
        if (feed._registry != sink._registry)
            throw new ArgumentException($"Linking ports {feed.PortId} and {sink.PortId} not possible; must be in same registry");
        if (feed.PortDirection != PipeWirePortDirection.Out)
            throw new ArgumentException($"Linking ports {feed.PortId} and {sink.PortId} not possible; {nameof(feed)} is not an output port");
        if (sink.PortDirection != PipeWirePortDirection.In)
            throw new ArgumentException($"Linking ports {feed.PortId} and {sink.PortId} not possible; {nameof(sink)} is not an input port");

        spa_interface* result;

        fixed (byte* pkin = Encoding.UTF8.GetBytes(Native.PW_KEY_LINK_OUTPUT_NODE))
        fixed (byte* pvin = Encoding.UTF8.GetBytes(feed.NodeId.ToString()))
        fixed (byte* pkip = Encoding.UTF8.GetBytes(Native.PW_KEY_LINK_OUTPUT_PORT))
        fixed (byte* pvip = Encoding.UTF8.GetBytes(feed.PortId.ToString()))
        fixed (byte* pkon = Encoding.UTF8.GetBytes(Native.PW_KEY_LINK_INPUT_NODE))
        fixed (byte* pvon = Encoding.UTF8.GetBytes(sink.NodeId.ToString()))
        fixed (byte* pkop = Encoding.UTF8.GetBytes(Native.PW_KEY_LINK_INPUT_PORT))
        fixed (byte* pvop = Encoding.UTF8.GetBytes(sink.PortId.ToString()))
        {
            var inputNode = new spa_dict_item { key = (sbyte*)pkin, value = (sbyte*)pvin };
            var inputPort = new spa_dict_item { key = (sbyte*)pkip, value = (sbyte*)pvip };
            var outputNode = new spa_dict_item { key = (sbyte*)pkon, value = (sbyte*)pvon };
            var outputPort = new spa_dict_item { key = (sbyte*)pkop, value = (sbyte*)pvop };

            fixed (spa_dict_item* ptr = new[] { inputNode, inputPort, outputNode, outputPort })
            {
                var dict = new spa_dict { flags = 0, items = ptr, n_items = 4 };

                Native.GetInterface(feed._registry._ctx._core, out pw_core_methods* methods, out void* data);
                fixed (byte* key = "link-factory"u8.ToArray())
                fixed (byte* iface = Encoding.UTF8.GetBytes(Native.PW_TYPE_INTERFACE_Link))
                fixed (spa_dict* props = new[] { dict })
                    using (feed._registry._ctx.Lock())
                        result = (spa_interface*) methods->create_object(data, (sbyte*)key, (sbyte*)iface, Native.PW_VERSION_LINK, props, 0);
            }
        }

        if ((byte)result == 0)
        {
            throw new Exception($"Creating new link from port {feed.PortId} to port {sink.PortId} failed!");
        }

        return feed._registry.WaitForLink(feed.PortId, sink.PortId);
    }

    internal readonly PipeWireRegistry _registry;

    /// <summary>
    /// todo: write docs
    /// </summary>
    /// <param name="_registry"></param>
    /// <param name="LinkId"></param>
    /// <param name="LinkInputNode"></param>
    /// <param name="LinkInputPort"></param>
    /// <param name="LinkOutputNode"></param>
    /// <param name="LinkOutputPort"></param>
    internal PipeWireLink(
        PipeWireRegistry _registry,
        uint LinkId,
        uint LinkInputNode,
        uint LinkInputPort,
        uint LinkOutputNode,
        uint LinkOutputPort)
    {
        this.LinkId = LinkId;
        this.LinkInputNode = LinkInputNode;
        this.LinkInputPort = LinkInputPort;
        this.LinkOutputNode = LinkOutputNode;
        this.LinkOutputPort = LinkOutputPort;
        this._registry = _registry;
    }

    /// <summary>
    /// todo: write docs
    /// </summary>
    public PipeWireSource? InputNode => _registry._sources.GetValueOrDefault(LinkInputNode);

    /// <summary>
    /// todo: write docs
    /// </summary>
    public PipeWirePort? InputPort => _registry._ports.GetValueOrDefault(LinkInputPort);

    /// <summary>
    /// todo: write docs
    /// </summary>
    public PipeWireSource? OutputNode => _registry._sources.GetValueOrDefault(LinkOutputNode);

    /// <summary>
    /// todo: write docs
    /// </summary>
    public PipeWirePort? OutputPort => _registry._ports.GetValueOrDefault(LinkOutputPort);

    /// <summary></summary>
    public uint LinkId { get; }

    /// <summary></summary>
    public uint LinkInputNode { get; }

    /// <summary></summary>
    public uint LinkInputPort { get; }

    /// <summary></summary>
    public uint LinkOutputNode { get; }

    /// <summary></summary>
    public uint LinkOutputPort { get; }
}
