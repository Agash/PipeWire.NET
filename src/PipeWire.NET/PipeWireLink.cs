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

    /// <summary>
    /// todo: write docs
    /// </summary>
    /// <exception cref="Exception"></exception>
    public unsafe void Deconstruct(out uint id)
    {
        int result;
        using (_registry._ctx.Lock())
        {
            Native.GetInterface(_registry._registry, out pw_registry_methods* methods, out void* data);
            result = methods->destroy(data, LinkId);
        }

        if (result == 0)
        {
            throw new Exception($"Removing link {LinkId} failed");
        }

        id = LinkId;
    }
}
