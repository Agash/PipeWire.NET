using System.Runtime.Versioning;
using System.Text;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Media;

/// <summary>
/// Type-safe builder for the PipeWire property dictionary passed to <c>pw_stream_new</c>.
/// Replaces hand-formatted <c>media.type=Video media.category=Capture ...</c> strings.
/// </summary>
/// <remarks>
/// Construct with the required keys, optionally chain <see cref="WithRole"/> /
/// <see cref="WithTargetObject"/> / <see cref="With"/>, then the stream classes call
/// <see cref="ToNativeProperties"/> internally.
/// </remarks>
public sealed class StreamProperties
{
    private readonly Dictionary<string, string> _props = new(StringComparer.Ordinal);

    /// <param name="mediaType">Video or audio.</param>
    /// <param name="category">Capture or playback.</param>
    public StreamProperties(StreamMediaType mediaType, StreamCategory category)
    {
        _props["media.type"]     = mediaType == StreamMediaType.Video ? "Video" : "Audio";
        _props["media.category"] = category == StreamCategory.Capture ? "Capture" : "Playback";
    }

    /// <summary>Sets <c>media.role</c> (e.g. "Camera", "Music", "Screen").</summary>
    public StreamProperties WithRole(string role)
    {
        ArgumentException.ThrowIfNullOrEmpty(role);
        _props["media.role"] = role;
        return this;
    }

    /// <summary>Sets <c>target.object</c> to bind the stream to a specific node by serial/name.</summary>
    public StreamProperties WithTargetObject(string targetObject)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetObject);
        _props["target.object"] = targetObject;
        return this;
    }

    /// <summary>Sets <c>node.name</c> - the stable name this stream advertises in the graph.</summary>
    public StreamProperties WithNodeName(string nodeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeName);
        _props["node.name"] = nodeName;
        return this;
    }

    /// <summary>Sets <c>node.description</c> - the human-readable name shown to users.</summary>
    public StreamProperties WithNodeDescription(string description)
    {
        ArgumentException.ThrowIfNullOrEmpty(description);
        _props["node.description"] = description;
        return this;
    }

    /// <summary>Sets an arbitrary PipeWire property key/value.</summary>
    public StreamProperties With(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        _props[key] = value;
        return this;
    }

    /// <summary>The property key/value pairs accumulated so far.</summary>
    public IReadOnlyDictionary<string, string> Values => _props;

    /// <summary>
    /// Builds a native <c>pw_properties*</c>. Ownership transfers to <c>pw_stream_new</c>
    /// (which consumes it); callers do not free the result.
    /// </summary>
    [SupportedOSPlatform("linux")]
    internal unsafe pw_properties* ToNativeProperties()
    {
        // Built as a real dictionary rather than a "key=value key2=value2" string. That string is
        // reparsed by PipeWire on whitespace, so any value containing a quote, tab or newline is
        // split or mis-terminated - quoting only values with spaces is not enough, and nothing
        // escapes an embedded quote at all.
        int bytes = 0;
        foreach ((string key, string value) in _props)
            bytes += Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value) + 2;

        byte[] scratch = new byte[bytes];
        var items = new spa_dict_item[_props.Count];

        var builder = new SpaDictBuilder(scratch, items);
        foreach ((string key, string value) in _props)
            builder.Add(Encoding.UTF8.GetBytes(key), value);

        spa_dict dict = builder.Build();
        return Native.pw_properties_new_dict(&dict);
    }
}
