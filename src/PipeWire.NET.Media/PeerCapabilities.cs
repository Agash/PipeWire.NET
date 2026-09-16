using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Media;

/// <summary>What a peer said about device-ID negotiation in its <c>SPA_PARAM_PeerCapability</c>.</summary>
/// <param name="NegotiatesDeviceIds">Whether it takes part (<c>pipewire.device-id-negotiation</c> >= 1).</param>
/// <param name="AvailableDevices">
/// The devices it can work with (<c>pipewire.device-ids</c>), empty when it named none - which means
/// any, as upstream's video-play-fixate reads it.
/// </param>
internal readonly record struct PeerCapabilities(bool NegotiatesDeviceIds, ImmutableArray<ulong> AvailableDevices)
{
    /// <summary>Whether <paramref name="device"/> is one the peer can work with.</summary>
    public bool Accepts(ulong device) => AvailableDevices.IsDefaultOrEmpty || AvailableDevices.Contains(device);
}
