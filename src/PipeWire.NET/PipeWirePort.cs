using System.Runtime.Versioning;
namespace PipeWire.NET;

/// <summary>
/// todo: write docs
/// </summary>
/// <param name="_registry"></param>
/// <param name="PortId"></param>
/// <param name="NodeId"></param>
/// <param name="PortName"></param>
/// <param name="PortDirection"></param>
/// <param name="Monitor"></param>
/// <param name="Exclusive"></param>
[SupportedOSPlatform("linux")]
public sealed record PipeWirePort(
    PipeWireRegistry _registry,
    uint PortId,
    uint NodeId,
    string? PortName,
    PipeWirePortDirection PortDirection,
    bool Monitor,
    bool Exclusive)
{
    internal readonly PipeWireRegistry _registry = _registry;

    /// <summary>
    /// todo: write docs
    /// </summary>
    public PipeWireSource Node => _registry._sources[NodeId];

    /// <summary>
    /// todo: write docs
    /// </summary>
    public IEnumerable<PipeWireLink> InputLinks => _registry._links
        .Where(link => link.Value.InputPort.PortId == PortId)
        .Select(link => link.Value);

    /// <summary>
    /// todo: write docs
    /// </summary>
    public IEnumerable<PipeWireLink> OutputLinks => _registry._links
        .Where(link => link.Value.InputPort.PortId == PortId)
        .Select(link => link.Value);
}

/// <summary>
/// todo: write docs
/// </summary>
public enum PipeWirePortDirection
{
    /// <summary>
    /// todo: write docs
    /// </summary>
    In,
    /// <summary>
    /// todo: write docs
    /// </summary>
    Out
}
