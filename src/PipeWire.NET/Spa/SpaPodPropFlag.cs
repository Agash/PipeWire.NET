using PipeWire.NET.Generated;

namespace PipeWire.NET.Spa;

/// <summary>
/// Flags carried in a <c>spa_pod_prop</c> header (spa/pod/pod.h). Used when offering DRM format
/// modifiers: the first negotiation pass advertises the modifier choice with
/// <see cref="Mandatory"/> | <see cref="DontFixate"/> so the producer returns the filtered set of
/// modifiers it supports without collapsing it to one, letting the consumer fixate on a modifier
/// its GPU can actually import (the canonical two-step dmabuf modifier handshake).
/// </summary>
internal static class SpaPodPropFlag
{
    internal const uint Readonly   = 1u << 0;
    internal const uint Hardware   = 1u << 1;
    internal const uint HintDict   = 1u << 2;
    internal const uint Mandatory  = 1u << 3;
    internal const uint DontFixate = 1u << 4;
}
