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
        uint id, PipeWirePermissions permissions, uint version, PipeWireProperties props) =>
        new(id,
            props.Text(PipeWireNames.NodeName),
            props.Text(PipeWireNames.NodeDescription),
            props.Text(PipeWireNames.MediaClass),
            props.Text(PipeWireNames.NodeNick),
            permissions,
            version,
            props.Id(PipeWireNames.DeviceId),
            props.Id(PipeWireNames.ClientId),
            props.Id(PipeWireNames.FactoryId),
            props.Text(PipeWireNames.ObjectPath),
            props.Int(PipeWireNames.PriorityDriver),
            props.Int(PipeWireNames.PrioritySession),
            props);

    /// <summary>
    /// Builds a port, or explains why it cannot. A port needs an owning node and a direction; with
    /// either missing there is nowhere to file it and no way to say which way it faces.
    /// </summary>
    internal static bool TryParsePort(
        uint id, PipeWirePermissions permissions, uint version, PipeWireProperties props,
        out PipeWirePort? port, out string reason, out string? offendingValue)
    {
        port = null;

        string? nodeId = props.GetValueOrDefault(PipeWireNames.NodeId);
        if (!uint.TryParse(nodeId, out uint parsedNodeId))
        {
            reason = "unusable node id";
            offendingValue = nodeId;
            return false;
        }

        string? direction = props.GetValueOrDefault(PipeWireNames.PortDirection);
        if (!TryParseDirection(direction, out PipeWirePortDirection parsedDirection))
        {
            reason = "unusable direction";
            offendingValue = direction;
            return false;
        }

        port = new PipeWirePort(
            id, parsedNodeId,
            props.Text(PipeWireNames.PortName),
            parsedDirection,
            props.Flag(PipeWireNames.PortMonitor),
            props.Id(PipeWireNames.PortIndex),
            props.Flag(PipeWireNames.PortControl) || parsedDirection is PipeWirePortDirection.Control,
            props.Flag(PipeWireNames.PortPhysical),
            props.Flag(PipeWireNames.PortTerminal),
            props.Text(PipeWireNames.FormatDsp),
            props.Text(PipeWireNames.AudioChannel),
            props.Text(PipeWireNames.PortAlias),
            props.Text(PipeWireNames.PortGroup),
            props.Text(PipeWireNames.ObjectPath),
            permissions,
            version,
            props);

        reason = string.Empty;
        offendingValue = null;
        return true;
    }

    /// <summary>
    /// Builds a link, or explains why it cannot. All four endpoint ids are mandatory: a link missing
    /// any of them describes no route.
    /// </summary>
    internal static bool TryParseLink(
        uint id, PipeWirePermissions permissions, uint version, PipeWireProperties props,
        out PipeWireLink? link, out string reason, out string? offendingValue)
    {
        link = null;

        if (!TryReadId(props, PipeWireNames.LinkOutputNode, out uint outputNode, out reason, out offendingValue) ||
            !TryReadId(props, PipeWireNames.LinkOutputPort, out uint outputPort, out reason, out offendingValue) ||
            !TryReadId(props, PipeWireNames.LinkInputNode, out uint inputNode, out reason, out offendingValue) ||
            !TryReadId(props, PipeWireNames.LinkInputPort, out uint inputPort, out reason, out offendingValue))
            return false;

        link = new PipeWireLink(
            id, inputNode, inputPort, outputNode, outputPort, permissions, version,
            props.Flag(PipeWireNames.LinkPassive),
            props.Id(PipeWireNames.FactoryId),
            props.Id(PipeWireNames.ClientId),
            props);
        reason = string.Empty;
        offendingValue = null;
        return true;
    }

    /// <summary>
    /// Builds a device. Always succeeds: like a node it carries nothing mandatory, and dropping one
    /// for having no name would hide a whole sound card from the graph.
    /// </summary>
    internal static PipeWireDevice ParseDevice(
        uint id, PipeWirePermissions permissions, uint version, PipeWireProperties props) =>
        new(id, permissions, version,
            props.Text(PipeWireNames.DeviceName),
            props.Text(PipeWireNames.DeviceDescription),
            props.Text(PipeWireNames.DeviceNick),
            props.Text(PipeWireNames.DeviceApi),
            props.Text(PipeWireNames.MediaClass),
            props.Text(PipeWireNames.ObjectPath),
            props.Id(PipeWireNames.FactoryId),
            props.Id(PipeWireNames.ClientId),
            props.Id(PipeWireNames.ModuleId),
            props);

    /// <summary>Builds a client. Always succeeds; every field is optional.</summary>
    internal static PipeWireClient ParseClient(
        uint id, PipeWirePermissions permissions, uint version, PipeWireProperties props) =>
        new(id, permissions, version,
            props.Text(PipeWireNames.ApplicationName),
            props.Int(PipeWireNames.SecurityPid),
            props.Id(PipeWireNames.SecurityUid),
            props.Id(PipeWireNames.SecurityGid),
            props.Text(PipeWireNames.Access),
            props.Text(PipeWireNames.Protocol),
            props.Id(PipeWireNames.ModuleId),
            props.Text(PipeWireNames.SecuritySocket),
            props.Text(PipeWireNames.SecurityLabel),
            props.Text(PipeWireNames.SecurityAppId),
            props.Text(PipeWireNames.SecurityInstanceId),
            props);

    /// <summary>Builds a factory. Always succeeds; every field is optional.</summary>
    internal static PipeWireFactory ParseFactory(
        uint id, PipeWirePermissions permissions, uint version, PipeWireProperties props) =>
        new(id, permissions, version,
            props.Text(PipeWireNames.FactoryName),
            props.Text(PipeWireNames.FactoryTypeName),
            props.Id(PipeWireNames.FactoryTypeVersion),
            props.Id(PipeWireNames.ModuleId),
            props);

    /// <summary>Builds a module. Always succeeds; every field is optional.</summary>
    internal static PipeWireModule ParseModule(
        uint id, PipeWirePermissions permissions, uint version, PipeWireProperties props) =>
        new(id, permissions, version,
            props.Text(PipeWireNames.ModuleName),
            props.Text(PipeWireNames.ModuleDescription),
            props.Text(PipeWireNames.ModuleAuthor),
            props.Text(PipeWireNames.ModuleVersion),
            props);

    /// <summary>Builds a metadata store. Always succeeds; the name is optional.</summary>
    internal static PipeWireMetadataObject ParseMetadata(
        uint id, PipeWirePermissions permissions, uint version, PipeWireProperties props) =>
        new(id, permissions, version, props.Text(PipeWireNames.MetadataName),
            props.Id(PipeWireNames.ClientId),
            props.Id(PipeWireNames.FactoryId),
            props.Id(PipeWireNames.ModuleId),
            props);

    /// <summary>Builds the core object. Always succeeds; every field is optional.</summary>
    internal static PipeWireCoreObject ParseCore(
        uint id, PipeWirePermissions permissions, uint version, PipeWireProperties props) =>
        new(id, permissions, version,
            props.Text(PipeWireNames.CoreName),
            props.Text(PipeWireNames.CoreVersion),
            props.Text(PipeWireNames.HostName),
            props.Text(PipeWireNames.UserName),
            props);

    /// <summary>
    /// Reads a property that names another object and must be there.
    /// </summary>
    /// <remarks>
    /// Absent and unparseable are the same answer here: the object cannot be built, and the raw
    /// value travels back so the refusal can name what the daemon actually sent.
    /// </remarks>
    private static bool TryReadId(
        PipeWireProperties props, string key, out uint value, out string reason, out string? offendingValue)
    {
        string? raw = props.GetValueOrDefault(key);
        if (uint.TryParse(raw, out value))
        {
            reason = string.Empty;
            offendingValue = null;
            return true;
        }

        reason = $"unusable {key}";
        offendingValue = raw;
        return false;
    }

    /// <remarks>
    /// PipeWire writes "in", "out", "control" or "notify". Enum.TryParse would also accept numeric
    /// strings and any future value that happens to match a member name, so match explicitly.
    /// </remarks>
    internal static bool TryParseDirection(string? value, out PipeWirePortDirection direction)
    {
        switch (value)
        {
            case "in": direction = PipeWirePortDirection.In; return true;
            case "out": direction = PipeWirePortDirection.Out; return true;
            case "control": direction = PipeWirePortDirection.Control; return true;
            case "notify": direction = PipeWirePortDirection.Notify; return true;
            default: direction = default; return false;
        }
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
            if (!DaemonText.Bytes(item->key).SequenceEqual(keyUtf8))
                continue;

            // A null value is the property not being there. spa_dict_item allows it, and every
            // caller would turn the empty span it used to produce back into null anyway - so the
            // three states the daemon can express (absent, present and null, present and empty)
            // collapse to two here, and only the genuinely empty one reads as an empty string.
            if (item->value is null) return false;

            value = DaemonText.Bytes(item->value);
            return true;
        }
        return false;
    }

    /// <summary>Reads a property that the entity keeps, transcoding it once.</summary>
    /// <remarks>
    /// Null means the property is not there. A property that is there and empty comes back as an
    /// empty string, because those are different facts about the object and the daemon can report
    /// either.
    /// </remarks>
    internal static string? ReadString(spa_dict* dict, ReadOnlySpan<byte> keyUtf8) =>
        TryReadValue(dict, keyUtf8, out ReadOnlySpan<byte> value)
            ? (value.IsEmpty ? string.Empty : Encoding.UTF8.GetString(value))
            : null;

    /// <summary>Reads a boolean property without materialising it.</summary>
    internal static bool ReadBool(spa_dict* dict, ReadOnlySpan<byte> keyUtf8) =>
        TryReadValue(dict, keyUtf8, out ReadOnlySpan<byte> value) && ParseBool(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static string? Utf8ToString(ReadOnlySpan<byte> value) =>
        value.IsEmpty ? null : Encoding.UTF8.GetString(value);
}
