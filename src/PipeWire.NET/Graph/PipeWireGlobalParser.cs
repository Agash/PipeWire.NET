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
            props.Text(PipeWireKeys.PW_KEY_NODE_NAME),
            props.Text(PipeWireKeys.PW_KEY_NODE_DESCRIPTION),
            props.Text(PipeWireKeys.SPA_KEY_MEDIA_CLASS),
            props.Text(PipeWireKeys.PW_KEY_NODE_NICK),
            permissions,
            version,
            props.Id(PipeWireKeys.PW_KEY_DEVICE_ID),
            props.Id(PipeWireKeys.PW_KEY_CLIENT_ID),
            props.Id(PipeWireKeys.PW_KEY_FACTORY_ID),
            props.Text(PipeWireKeys.SPA_KEY_OBJECT_PATH),
            props.Int(PipeWireKeys.PW_KEY_PRIORITY_DRIVER),
            props.Int(PipeWireKeys.PW_KEY_PRIORITY_SESSION),
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

        string? nodeId = props.GetValueOrDefault(PipeWireKeys.PW_KEY_NODE_ID);
        if (!uint.TryParse(nodeId, out uint parsedNodeId))
        {
            reason = "unusable node id";
            offendingValue = nodeId;
            return false;
        }

        string? direction = props.GetValueOrDefault(PipeWireKeys.PW_KEY_PORT_DIRECTION);
        if (!TryParseDirection(direction, out PipeWirePortDirection parsedDirection))
        {
            reason = "unusable direction";
            offendingValue = direction;
            return false;
        }

        port = new PipeWirePort(
            id, parsedNodeId,
            props.Text(PipeWireKeys.PW_KEY_PORT_NAME),
            parsedDirection,
            props.Flag(PipeWireKeys.PW_KEY_PORT_MONITOR),
            props.Id(PipeWireKeys.PW_KEY_PORT_ID),
            props.Flag(PipeWireKeys.PW_KEY_PORT_CONTROL) || parsedDirection is PipeWirePortDirection.Control,
            props.Flag(PipeWireKeys.PW_KEY_PORT_PHYSICAL),
            props.Flag(PipeWireKeys.PW_KEY_PORT_TERMINAL),
            props.Text(PipeWireKeys.PW_KEY_FORMAT_DSP),
            props.Text(PipeWireKeys.PW_KEY_AUDIO_CHANNEL),
            props.Text(PipeWireKeys.PW_KEY_PORT_ALIAS),
            props.Text(PipeWireKeys.PW_KEY_PORT_GROUP),
            props.Text(PipeWireKeys.SPA_KEY_OBJECT_PATH),
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

        if (!TryReadId(props, PipeWireKeys.PW_KEY_LINK_OUTPUT_NODE, out uint outputNode, out reason, out offendingValue) ||
            !TryReadId(props, PipeWireKeys.PW_KEY_LINK_OUTPUT_PORT, out uint outputPort, out reason, out offendingValue) ||
            !TryReadId(props, PipeWireKeys.PW_KEY_LINK_INPUT_NODE, out uint inputNode, out reason, out offendingValue) ||
            !TryReadId(props, PipeWireKeys.PW_KEY_LINK_INPUT_PORT, out uint inputPort, out reason, out offendingValue))
            return false;

        link = new PipeWireLink(
            id, inputNode, inputPort, outputNode, outputPort, permissions, version,
            props.Flag(PipeWireKeys.PW_KEY_LINK_PASSIVE),
            props.Id(PipeWireKeys.PW_KEY_FACTORY_ID),
            props.Id(PipeWireKeys.PW_KEY_CLIENT_ID),
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
            props.Text(PipeWireKeys.SPA_KEY_DEVICE_NAME),
            props.Text(PipeWireKeys.SPA_KEY_DEVICE_DESCRIPTION),
            props.Text(PipeWireKeys.SPA_KEY_DEVICE_NICK),
            props.Text(PipeWireKeys.SPA_KEY_DEVICE_API),
            props.Text(PipeWireKeys.SPA_KEY_MEDIA_CLASS),
            props.Text(PipeWireKeys.SPA_KEY_OBJECT_PATH),
            props.Id(PipeWireKeys.PW_KEY_FACTORY_ID),
            props.Id(PipeWireKeys.PW_KEY_CLIENT_ID),
            props.Id(PipeWireKeys.PW_KEY_MODULE_ID),
            props);

    /// <summary>Builds a client. Always succeeds; every field is optional.</summary>
    internal static PipeWireClient ParseClient(
        uint id, PipeWirePermissions permissions, uint version, PipeWireProperties props) =>
        new(id, permissions, version,
            props.Text(PipeWireKeys.PW_KEY_APP_NAME),
            props.Int(PipeWireKeys.PW_KEY_SEC_PID),
            props.Id(PipeWireKeys.PW_KEY_SEC_UID),
            props.Id(PipeWireKeys.PW_KEY_SEC_GID),
            props.Text(PipeWireKeys.PW_KEY_ACCESS),
            props.Text(PipeWireKeys.PW_KEY_PROTOCOL),
            props.Id(PipeWireKeys.PW_KEY_MODULE_ID),
            props.Text(PipeWireKeys.PW_KEY_SEC_SOCKET),
            props.Text(PipeWireKeys.PW_KEY_SEC_LABEL),
            props.Text(PipeWireKeys.PW_KEY_SEC_APP_ID),
            props.Text(PipeWireKeys.PW_KEY_SEC_INSTANCE_ID),
            props);

    /// <summary>Builds a factory. Always succeeds; every field is optional.</summary>
    internal static PipeWireFactory ParseFactory(
        uint id, PipeWirePermissions permissions, uint version, PipeWireProperties props) =>
        new(id, permissions, version,
            props.Text(PipeWireKeys.PW_KEY_FACTORY_NAME),
            props.Text(PipeWireKeys.PW_KEY_FACTORY_TYPE_NAME),
            props.Id(PipeWireKeys.PW_KEY_FACTORY_TYPE_VERSION),
            props.Id(PipeWireKeys.PW_KEY_MODULE_ID),
            props);

    /// <summary>Builds a module. Always succeeds; every field is optional.</summary>
    internal static PipeWireModule ParseModule(
        uint id, PipeWirePermissions permissions, uint version, PipeWireProperties props) =>
        new(id, permissions, version,
            props.Text(PipeWireKeys.PW_KEY_MODULE_NAME),
            props.Text(PipeWireKeys.PW_KEY_MODULE_DESCRIPTION),
            props.Text(PipeWireKeys.PW_KEY_MODULE_AUTHOR),
            props.Text(PipeWireKeys.PW_KEY_MODULE_VERSION),
            props);

    /// <summary>Builds a metadata store. Always succeeds; the name is optional.</summary>
    internal static PipeWireMetadataObject ParseMetadata(
        uint id, PipeWirePermissions permissions, uint version, PipeWireProperties props) =>
        new(id, permissions, version, props.Text(PipeWireKeys.PW_KEY_METADATA_NAME),
            props.Id(PipeWireKeys.PW_KEY_CLIENT_ID),
            props.Id(PipeWireKeys.PW_KEY_FACTORY_ID),
            props.Id(PipeWireKeys.PW_KEY_MODULE_ID),
            props);

    /// <summary>Builds the core object. Always succeeds; every field is optional.</summary>
    internal static PipeWireCoreObject ParseCore(
        uint id, PipeWirePermissions permissions, uint version, PipeWireProperties props) =>
        new(id, permissions, version,
            props.Text(PipeWireKeys.PW_KEY_CORE_NAME),
            props.Text(PipeWireKeys.PW_KEY_CORE_VERSION),
            props.Text(PipeWireKeys.PW_KEY_APP_PROCESS_HOST),
            props.Text(PipeWireKeys.PW_KEY_APP_PROCESS_USER),
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
        value.SequenceEqual(PipeWireValues.True) || value.SequenceEqual("1"u8);

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
