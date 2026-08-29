using System.Runtime.Versioning;
namespace PipeWire.NET;

/// <summary>
/// todo: write docs
/// </summary>
/// <param name="_registry"></param>
/// <param name="LinkId"></param>
/// <param name="LinkInputNode"></param>
/// <param name="LinkInputPort"></param>
/// <param name="LinkOutputNode"></param>
/// <param name="LinkOutputPort"></param>
[SupportedOSPlatform("linux")]
public sealed record PipeWireLink(
    PipeWireRegistry _registry,
    uint LinkId,
    uint LinkInputNode,
    uint LinkInputPort,
    uint LinkOutputNode,
    uint LinkOutputPort)
{
    internal readonly PipeWireRegistry _registry = _registry;

    /// <summary>
    /// todo: write docs
    /// </summary>
    public PipeWireSource InputNode => _registry._sources[LinkInputNode];

    /// <summary>
    /// todo: write docs
    /// </summary>
    public PipeWirePort InputPort => _registry._ports[LinkInputPort];

    /// <summary>
    /// todo: write docs
    /// </summary>
    public PipeWireSource OutputNode => _registry._sources[LinkOutputNode];

    /// <summary>
    /// todo: write docs
    /// </summary>
    public PipeWirePort OutputPort => _registry._ports[LinkOutputPort];
}
