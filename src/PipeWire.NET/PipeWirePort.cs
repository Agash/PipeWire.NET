using System.Runtime.Versioning;
using System.Text;
using PipeWire.NET.Generated;

namespace PipeWire.NET;

/// <summary>
/// todo: write docs
/// </summary>
[SupportedOSPlatform("linux")]
public sealed record PipeWirePort
{
    internal readonly PipeWireRegistry _registry;

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
    internal PipeWirePort(
        PipeWireRegistry _registry,
        uint PortId,
        uint NodeId,
        string? PortName,
        PipeWirePortDirection PortDirection,
        bool Monitor,
        bool Exclusive)
    {
        this.PortId = PortId;
        this.NodeId = NodeId;
        this.PortName = PortName;
        this.PortDirection = PortDirection;
        this.Monitor = Monitor;
        this.Exclusive = Exclusive;
        this._registry = _registry;
    }

    /// <summary>
    /// todo: write docs
    /// </summary>
    public PipeWireSource? Node => _registry._sources.GetValueOrDefault(NodeId);

    /// <summary>
    /// todo: write docs
    /// </summary>
    public IEnumerable<PipeWireLink> InputLinks => _registry._links
        .Where(link => link.Value.InputPort?.PortId == PortId)
        .Select(link => link.Value);

    /// <summary>
    /// todo: write docs
    /// </summary>
    public IEnumerable<PipeWireLink> OutputLinks => _registry._links
        .Where(link => link.Value.InputPort?.PortId == PortId)
        .Select(link => link.Value);

    /// <summary></summary>
    public uint PortId { get; }

    /// <summary></summary>
    public uint NodeId { get; }

    /// <summary></summary>
    public string? PortName { get; }

    /// <summary></summary>
    public PipeWirePortDirection PortDirection { get; }

    /// <summary></summary>
    public bool Monitor { get; }

    /// <summary></summary>
    public bool Exclusive { get; }
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
