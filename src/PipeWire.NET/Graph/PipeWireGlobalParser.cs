using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PipeWire.NET.Graph;

/// <summary>
/// Turns the property dictionary from a registry <c>global</c> event into a graph entity.
/// </summary>
/// <remarks>
/// <para>
/// Pure: no registry, no locks, no events. Everything here runs on the loop thread inside a reverse
/// P/Invoke, so it must not throw and must not read past the buffers the daemon handed it.
/// </para>
/// <para>
/// The dictionary comes off the wire, so nothing in it is trusted. A property may be absent, its
/// value pointer may be null, and <c>n_items</c> may not agree with <c>items</c>. A global whose
/// mandatory properties do not parse is dropped rather than half-built, and the reason travels back
/// to the caller so it can be logged where the ids are in scope.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal static unsafe class PipeWireGlobalParser
{
    /// <summary>
    /// Builds a node. Always succeeds: a node carries no mandatory properties, and dropping one for
    /// having no name would hide it from the graph entirely.
    /// </summary>
    internal static PipeWireNode ParseNode(
        uint id, PipeWirePermissions permissions, uint version, spa_dict* props) =>
        new(id,
            ReadString(props, PipeWireKeys.NodeName),
            ReadString(props, PipeWireKeys.NodeDescription),
            ReadString(props, PipeWireKeys.MediaClass),
            ReadString(props, PipeWireKeys.NodeNick),
            permissions,
            version);

    /// <summary>
    /// Builds a port, or explains why it cannot. A port needs an owning node and a direction; with
    /// either missing there is nowhere to file it and no way to say which way it faces.
    /// </summary>
    internal static bool TryParsePort(
        uint id, PipeWirePermissions permissions, uint version, spa_dict* props,
        out PipeWirePort? port, out string reason, out string? offendingValue)
    {
        port = null;

        TryReadValue(props, PipeWireKeys.NodeId, out ReadOnlySpan<byte> nodeId);
        if (!uint.TryParse(nodeId, out uint parsedNodeId))
        {
            reason = "unusable node id";
            offendingValue = Utf8ToString(nodeId);
            return false;
        }

        TryReadValue(props, PipeWireKeys.PortDirection, out ReadOnlySpan<byte> direction);
        if (!TryParseDirection(direction, out PipeWirePortDirection parsedDirection))
        {
            reason = "unusable direction";
            offendingValue = Utf8ToString(direction);
            return false;
        }

        port = new PipeWirePort(
            id, parsedNodeId,
            ReadString(props, PipeWireKeys.PortName),
            parsedDirection,
            ReadBool(props, PipeWireKeys.PortMonitor),
            ReadBool(props, PipeWireKeys.PortExclusive),
            permissions,
            version);

        reason = string.Empty;
        offendingValue = null;
        return true;
    }

    /// <summary>
    /// Builds a link, or explains why it cannot. All four endpoint ids are mandatory: a link missing
    /// any of them describes no route.
    /// </summary>
    internal static bool TryParseLink(
        uint id, PipeWirePermissions permissions, uint version, spa_dict* props,
        out PipeWireLink? link, out string reason, out string? offendingValue)
    {
        link = null;

        if (!TryReadId(props, PipeWireKeys.LinkOutputNode, out uint outputNode, out reason, out offendingValue) ||
            !TryReadId(props, PipeWireKeys.LinkOutputPort, out uint outputPort, out reason, out offendingValue) ||
            !TryReadId(props, PipeWireKeys.LinkInputNode, out uint inputNode, out reason, out offendingValue) ||
            !TryReadId(props, PipeWireKeys.LinkInputPort, out uint inputPort, out reason, out offendingValue))
            return false;

        link = new PipeWireLink(id, inputNode, inputPort, outputNode, outputPort, permissions, version);
        reason = string.Empty;
        offendingValue = null;
        return true;
    }

    private static bool TryReadId(
        spa_dict* props, ReadOnlySpan<byte> key, out uint value, out string reason, out string? offendingValue)
    {
        TryReadValue(props, key, out ReadOnlySpan<byte> raw);
        if (uint.TryParse(raw, out value))
        {
            reason = string.Empty;
            offendingValue = null;
            return true;
        }

        reason = $"unusable {Encoding.UTF8.GetString(key)}";
        offendingValue = Utf8ToString(raw);
        return false;
    }

    /// <remarks>
    /// PipeWire writes "in", "out", "control" or "notify". Enum.TryParse would also accept numeric
    /// strings and any future value that happens to match a member name, so match explicitly.
    /// </remarks>
    internal static bool TryParseDirection(ReadOnlySpan<byte> value, out PipeWirePortDirection direction)
    {
        if (value.SequenceEqual(PipeWireKeys.DirectionIn)) { direction = PipeWirePortDirection.In; return true; }
        if (value.SequenceEqual(PipeWireKeys.DirectionOut)) { direction = PipeWirePortDirection.Out; return true; }
        if (value.SequenceEqual(PipeWireKeys.DirectionControl)) { direction = PipeWirePortDirection.Control; return true; }
        if (value.SequenceEqual(PipeWireKeys.DirectionNotify)) { direction = PipeWirePortDirection.Notify; return true; }

        direction = default;
        return false;
    }

    /// <remarks>Mirrors spa_atob: only "true" and "1" are true, and absence is false.</remarks>
    internal static bool ParseBool(ReadOnlySpan<byte> value) =>
        value.SequenceEqual(PipeWireKeys.True) || value.SequenceEqual("1"u8);

    /// <summary>
    /// Finds a property and hands back its value as UTF-8 over the daemon's own buffer.
    /// </summary>
    /// <remarks>
    /// No copy and no transcoding: a caller that only needs to compare or parse the value never
    /// allocates. The span is valid only for the duration of the callback that supplied
    /// <paramref name="dict"/> - materialise it with <see cref="Utf8ToString"/> to keep it.
    /// </remarks>
    internal static bool TryReadValue(spa_dict* dict, ReadOnlySpan<byte> keyUtf8, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (dict is null) return false;

        int nItems = (int)dict->n_items;
        if (nItems == 0 || dict->items is null) return false;

        for (int i = 0; i < nItems; i++)
        {
            spa_dict_item* item = dict->items + i;
            if (item->key is null) continue;
            if (!MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)item->key).SequenceEqual(keyUtf8))
                continue;

            if (item->value is not null)
                value = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)item->value);
            return true;
        }
        return false;
    }

    /// <summary>Reads a property that the entity keeps, transcoding it once.</summary>
    internal static string? ReadString(spa_dict* dict, ReadOnlySpan<byte> keyUtf8) =>
        TryReadValue(dict, keyUtf8, out ReadOnlySpan<byte> value) ? Utf8ToString(value) : null;

    /// <summary>Reads a boolean property without materialising it.</summary>
    internal static bool ReadBool(spa_dict* dict, ReadOnlySpan<byte> keyUtf8) =>
        TryReadValue(dict, keyUtf8, out ReadOnlySpan<byte> value) && ParseBool(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static string? Utf8ToString(ReadOnlySpan<byte> value) =>
        value.IsEmpty ? null : Encoding.UTF8.GetString(value);
}
