using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.Versioning;
using PipeWire.NET.Interop;

namespace PipeWire.NET.Media;

/// <summary>
/// One device and the DRM format modifiers it can use, offered as one candidate in a DMA-BUF
/// negotiation.
/// </summary>
/// <remarks>
/// Modifiers are per device: a tiling layout one GPU exports another may not import. Upstream's
/// video-src-fixate builds one format per device, each with that device's own modifiers, which is
/// what one of these becomes.
/// </remarks>
/// <param name="Device">The device.</param>
/// <param name="Modifiers">Its modifiers, in priority order.</param>
[SupportedOSPlatform("linux")]
public readonly record struct DmaBufDeviceOffer(DrmDevice Device, ImmutableArray<long> Modifiers);
