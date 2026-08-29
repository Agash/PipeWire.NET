namespace PipeWire.NET;

/// <summary>
/// todo: write docs
/// </summary>
/// <param name="PortId"></param>
/// <param name="NodeId"></param>
/// <param name="PortName"></param>
/// <param name="PortDirection"></param>
/// <param name="Monitor"></param>
/// <param name="Exclusive"></param>
public sealed record PipeWirePort(
    uint PortId,
    uint NodeId,
    string? PortName,
    PipeWirePortDirection PortDirection,
    bool Monitor,
    bool Exclusive)
{
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
