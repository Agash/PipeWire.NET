using PipeWire.NET.Spa;

namespace PipeWire.NET.Graph;

/// <summary>How a node's ports are laid out, and what they carry.</summary>
/// <remarks>
/// This is the parameter that decides a node's shape rather than its settings: whether it exposes
/// one port carrying interleaved audio or one port per channel, whether it converts, and whether it
/// has monitor and control ports at all. Changing it destroys the node's ports and creates new ones,
/// so anything holding a port id across the change is holding a stale one.
/// <para>
/// It belongs to the <b>adapter node</b>, not to a device: it is implemented by
/// <c>spa/plugins/audioconvert/audioadapter.c</c> and its video counterpart. A device's shape is its
/// profile, which is a different parameter with a different meaning.
/// </para>
/// </remarks>
/// <param name="Direction">Which side of the node this configures.</param>
/// <param name="Mode">What the node does with the media between its ports.</param>
/// <param name="Monitor">Whether input ports get matching monitor outputs.</param>
/// <param name="Control">Whether the node exposes control ports.</param>
/// <param name="Format">
/// A format filter narrowing what the configured ports accept, or null for no filter. In
/// <see cref="SpaParamPortConfigMode.Dsp"/> this is where the channel count comes from.
/// </param>
public sealed record PipeWirePortConfig(
    SpaDirection Direction,
    SpaParamPortConfigMode Mode,
    bool Monitor = false,
    bool Control = false,
    SpaObject? Format = null)
{
    /// <summary>Reads one out of a <c>SPA_PARAM_PortConfig</c> object, or null if it is not one.</summary>
    /// <remarks>
    /// Absent members read as their defaults. Only direction and mode are ever really sent; the
    /// booleans are omitted when false, which is how the adapter writes them.
    /// </remarks>
    public static PipeWirePortConfig? From(SpaObject? param)
    {
        if (param is null || param.ObjectType != SpaType.ObjectParamPortConfig) return null;

        return new PipeWirePortConfig(
            (SpaDirection)(param[(uint)SpaParamPortConfig.Direction] is SpaId d ? d.Value : 0),
            (SpaParamPortConfigMode)(param[(uint)SpaParamPortConfig.Mode] is SpaId m ? m.Value : 0),
            param[(uint)SpaParamPortConfig.Monitor] is SpaBool mon && mon.Value,
            param[(uint)SpaParamPortConfig.Control] is SpaBool ctl && ctl.Value,
            param[(uint)SpaParamPortConfig.Format] as SpaObject);
    }

    /// <summary>This configuration as the parameter object the daemon expects.</summary>
    /// <remarks>
    /// The format filter is written only when there is one. An empty Object property is not the same
    /// as no property: the adapter reads it as a filter that matches nothing.
    /// </remarks>
    public SpaObject ToParameter()
    {
        var properties = new List<SpaProperty>(5)
        {
            new((uint)SpaParamPortConfig.Direction, 0, new SpaId((uint)Direction)),
            new((uint)SpaParamPortConfig.Mode, 0, new SpaId((uint)Mode)),
            new((uint)SpaParamPortConfig.Monitor, 0, new SpaBool(Monitor)),
            new((uint)SpaParamPortConfig.Control, 0, new SpaBool(Control)),
        };

        if (Format is not null)
            properties.Add(new SpaProperty((uint)SpaParamPortConfig.Format, 0, Format));

        return new SpaObject(SpaType.ObjectParamPortConfig, SpaParamType.PortConfig, [.. properties]);
    }
}
