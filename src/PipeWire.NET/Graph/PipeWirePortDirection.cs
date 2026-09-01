namespace PipeWire.NET.Graph;

/// <summary>
/// The direction a port is registered for, interpreted relative to the node that owns it.
/// </summary>
/// <remarks>
/// These are the values of the <c>port.direction</c> property, which is a richer classification than
/// the native <c>spa_direction</c> enum: that has only input and output, while control ports report
/// <see cref="Control"/> or <see cref="Notify"/>. Do not map either onto a plain data direction -
/// nothing upstream establishes that equivalence.
/// </remarks>
public enum PipeWirePortDirection
{
    /// <summary>A data input to the owning node (<c>port.direction=in</c>).</summary>
    In,

    /// <summary>A data output of the owning node (<c>port.direction=out</c>).</summary>
    Out,

    /// <summary>A control input of the owning node (<c>port.direction=control</c>).</summary>
    Control,

    /// <summary>A notification output of the owning node (<c>port.direction=notify</c>).</summary>
    Notify,
}
