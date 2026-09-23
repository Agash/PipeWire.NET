using System.Collections.Immutable;

namespace PipeWire.NET.Media;

/// <summary>One of a stream's control ports, as the daemon last reported it.</summary>
/// <remarks>
/// Controls are the knobs a stream exposes to whatever is driving it: a volume, a mute, a filter
/// parameter. The daemon reports them as they appear and again whenever a value changes, so an
/// instance of this is a snapshot rather than a live view.
/// <para>
/// The id is a SPA property id, the same numbering as the keys inside a <c>Props</c> object.
/// </para>
/// </remarks>
/// <param name="Id">The SPA property id this control is addressed by.</param>
/// <param name="Name">What the control is called, as the producer named it.</param>
/// <param name="Default">The value it starts at.</param>
/// <param name="Minimum">The lowest value it accepts.</param>
/// <param name="Maximum">The highest value it accepts.</param>
/// <param name="Values">
/// Its current values. One entry for a scalar control, one per channel for a control that has been
/// configured per channel.
/// </param>
/// <param name="MaximumValues">How many values it can be set to at once.</param>
public sealed record PipeWireStreamControl(
    uint Id,
    string Name,
    float Default,
    float Minimum,
    float Maximum,
    ImmutableArray<float> Values,
    uint MaximumValues);
