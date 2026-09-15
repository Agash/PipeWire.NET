using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.Versioning;
using PipeWire.NET.Interop;

namespace PipeWire.NET.Media;

/// <summary>
/// A DRM device, identified the way PipeWire's DMA-BUF device-ID negotiation identifies one: by its
/// <c>dev_t</c>.
/// </summary>
/// <remarks>
/// <para>
/// Upstream carries a device as the bytes of its <c>dev_t</c> - in the <c>SPA_FORMAT_VIDEO_deviceId</c>
/// property of a format and, hex-encoded, in the <c>pipewire.device-ids</c> capability
/// (<c>doc/dox/internals/dma-buf.dox</c>). Two values are the same device when their ids are equal;
/// the render node path is descriptive only and plays no part in equality.
/// </para>
/// <para>
/// The number is read from sysfs (<c>/sys/class/drm/renderD128/dev</c> holds <c>226:128</c>) rather
/// than from <c>stat</c>, whose <c>struct stat</c> layout differs by architecture, and combined with
/// glibc's own <c>gnu_dev_makedev</c>.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public readonly record struct DrmDevice
{
    /// <summary>A device with the given <c>dev_t</c>.</summary>
    /// <param name="id">The device number.</param>
    /// <param name="renderNodePath">Where its render node is, when known.</param>
    public DrmDevice(ulong id, string? renderNodePath = null)
    {
        Id = id;
        RenderNodePath = renderNodePath;
    }

    /// <summary>The device number (<c>dev_t</c>).</summary>
    public ulong Id { get; }

    /// <summary>The render node's path, such as <c>/dev/dri/renderD128</c>, when known.</summary>
    public string? RenderNodePath { get; }

    /// <summary>The major number (<c>major(dev_t)</c>).</summary>
    public uint Major => NativeConstants.gnu_dev_major((nuint)Id);

    /// <summary>The minor number (<c>minor(dev_t)</c>).</summary>
    public uint Minor => NativeConstants.gnu_dev_minor((nuint)Id);

    /// <summary>The device with the given major and minor numbers (<c>makedev</c>).</summary>
    public static DrmDevice FromNumbers(uint major, uint minor) =>
        new(NativeConstants.gnu_dev_makedev(major, minor));

    /// <summary>The device behind a render node.</summary>
    /// <param name="path">The render node, such as <c>/dev/dri/renderD128</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> does not name a DRM render node.</exception>
    /// <exception cref="IOException">The kernel does not describe the node.</exception>
    public static DrmDevice FromRenderNode(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string name = Path.GetFileName(path);
        if (!name.StartsWith("renderD", StringComparison.Ordinal))
            throw new ArgumentException($"{path} is not a DRM render node.", nameof(path));

        string numbers = File.ReadAllText($"/sys/class/drm/{name}/dev").Trim();
        int colon = numbers.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0
            || !uint.TryParse(numbers.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out uint major)
            || !uint.TryParse(numbers.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out uint minor))
        {
            throw new IOException($"the kernel describes {name} as '{numbers}', not major:minor.");
        }

        return new DrmDevice(NativeConstants.gnu_dev_makedev(major, minor), path);
    }

    /// <summary>Every render node on this machine, in order of minor number.</summary>
    public static ImmutableArray<DrmDevice> EnumerateRenderNodes()
    {
        if (!Directory.Exists("/dev/dri")) return [];

        var found = ImmutableArray.CreateBuilder<DrmDevice>();
        foreach (string path in Directory.EnumerateFiles("/dev/dri", "renderD*"))
        {
            try
            {
                found.Add(FromRenderNode(path));
            }
            catch (IOException)
            {
                // Deliberately not logged: a node the kernel does not describe is not one a
                // negotiation could name, and the caller sees it missing from the list.
            }
        }

        found.Sort(static (a, b) => a.Minor.CompareTo(b.Minor));
        return found.ToImmutable();
    }

    /// <summary>Equal when the device numbers are; the path is descriptive.</summary>
    public bool Equals(DrmDevice other) => Id == other.Id;

    /// <inheritdoc/>
    public override int GetHashCode() => Id.GetHashCode();

    /// <inheritdoc/>
    public override string ToString() =>
        RenderNodePath is null
            ? string.Create(CultureInfo.InvariantCulture, $"{Major}:{Minor}")
            : string.Create(CultureInfo.InvariantCulture, $"{Major}:{Minor} ({RenderNodePath})");
}

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
