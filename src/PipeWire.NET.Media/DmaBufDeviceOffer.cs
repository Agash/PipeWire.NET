using System.Collections.Immutable;
using System.Runtime.Versioning;

namespace PipeWire.NET.Media;

/// <summary>The DRM format modifiers one device can use for one pixel format.</summary>
/// <remarks>
/// Modifiers are per format as well as per device: a tiling layout a GPU imports as BGRA it may not
/// import as NV12, whose planes it samples as separate single- and two-channel images.
/// </remarks>
/// <param name="Format">The pixel format.</param>
/// <param name="Modifiers">Its modifiers, in priority order.</param>
[SupportedOSPlatform("linux")]
public readonly record struct DmaBufFormatModifiers(
    PixelFormat Format,
    ImmutableArray<long> Modifiers
);

/// <summary>
/// One device and the formats and DRM format modifiers it can use, offered as one candidate in a
/// DMA-BUF negotiation.
/// </summary>
/// <remarks>
/// Modifiers are per device: a tiling layout one GPU exports another may not import. Upstream's
/// video-src-fixate builds one format per device, each with that device's own modifiers; an offer of
/// several formats becomes one format per device and pixel format, in the order given.
/// </remarks>
/// <param name="Device">The device.</param>
/// <param name="Formats">The pixel formats it takes, most preferred first, each with its modifiers.</param>
[SupportedOSPlatform("linux")]
public readonly record struct DmaBufDeviceOffer(
    DrmDevice Device,
    ImmutableArray<DmaBufFormatModifiers> Formats
)
{
    /// <summary>An offer of one pixel format.</summary>
    /// <param name="device">The device.</param>
    /// <param name="format">The pixel format.</param>
    /// <param name="modifiers">Its modifiers, in priority order.</param>
    public DmaBufDeviceOffer(DrmDevice device, PixelFormat format, ImmutableArray<long> modifiers)
        : this(device, [new DmaBufFormatModifiers(format, modifiers)]) { }

    /// <summary>The modifiers offered for a format; empty when the format is not offered.</summary>
    /// <param name="format">The pixel format.</param>
    /// <returns>The modifiers, in priority order.</returns>
    public ImmutableArray<long> ModifiersFor(PixelFormat format)
    {
        foreach (DmaBufFormatModifiers entry in Formats.IsDefault ? [] : Formats)
        {
            if (entry.Format == format)
                return entry.Modifiers.IsDefault ? [] : entry.Modifiers;
        }

        return [];
    }
}
