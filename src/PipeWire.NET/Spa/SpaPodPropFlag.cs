using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Spa;

/// <summary>
/// Flags carried in a <c>spa_pod_prop</c> header (spa/pod/pod.h). Used when offering DRM format
/// modifiers: the first negotiation pass advertises the modifier choice with
/// <see cref="Mandatory"/> | <see cref="DontFixate"/> so the producer returns the filtered set of
/// modifiers it supports without collapsing it to one, letting the consumer fixate on a modifier
/// its GPU can actually import (the canonical two-step dmabuf modifier handshake).
/// </summary>
public static class SpaPodPropFlag
{
    /// <summary>The property may be read but not written.</summary>
    public const uint Readonly = 1u << 0;

    /// <summary>The property is backed by hardware rather than applied in software.</summary>
    public const uint Hardware = 1u << 1;

    /// <summary>The value carries a dictionary of hints rather than a plain value.</summary>
    public const uint HintDict = 1u << 2;

    /// <summary>The peer must honour this property rather than treating it as a preference.</summary>
    public const uint Mandatory = 1u << 3;

    /// <summary>
    /// Narrow the choice but do not collapse it to a single value.
    /// </summary>
    /// <remarks>
    /// The first half of the DRM modifier handshake: the peer replies with the modifiers it
    /// supports, and the caller then fixates on one its GPU can import.
    /// </remarks>
    public const uint DontFixate = 1u << 4;

    /// <summary>Drop the property when filtering, on both sides.</summary>
    public const uint Drop = 1u << 5;
}
