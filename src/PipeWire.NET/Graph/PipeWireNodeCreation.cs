using System.Collections.Immutable;
using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

/// <summary>
/// A virtual stereo sink that has been described but not yet created.
/// </summary>
/// <remarks>
/// A <see langword="struct"/>, so chaining costs no allocation. Nothing reaches the daemon until
/// <see cref="ExecuteAsync"/> is awaited.
/// </remarks>
/// <example>
/// <code>
/// PipeWireNode node = await registry.CreateVirtualNode("Monitor mix")
///                                   .WithName("monitor_mix")
///                                   .WithLinger()
///                                   .ExecuteAsync(ct);
/// </code>
/// </example>
[SupportedOSPlatform("linux")]
public readonly struct PipeWireNodeCreation
{
    private readonly PipeWireRegistry _registry;
    private readonly string _description;
    private readonly string? _name;
    private readonly PipeWireObjectOptions _options;

    internal PipeWireNodeCreation(
        PipeWireRegistry registry, string description, string? name, PipeWireObjectOptions options)
    {
        _registry = registry;
        _description = description;
        _name = name;
        _options = options;
    }

    /// <summary>Sets <c>node.name</c>. A random name is generated when this is not called.</summary>
    public PipeWireNodeCreation WithName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new(_registry, _description, name, _options);
    }

    /// <summary>
    /// Keeps the node alive after this client disconnects (<c>object.linger</c>).
    /// </summary>
    /// <remarks>
    /// Without this the daemon destroys the node when the connection that made it goes away, which
    /// is the right default for a stream but wrong for a routing setup meant to outlive the app.
    /// A lingering object cannot be removed by disconnecting - not even by disposing the registry
    /// or context that made it - destroy it explicitly. <see cref="PipeWireRegistry.LingeringIds"/>
    /// lists what is left behind, so nothing has to be remembered by hand.
    /// </remarks>
    public PipeWireNodeCreation WithLinger() =>
        new(_registry, _description, _name, _options with { Linger = true });

    /// <summary>Sets any creation property the daemon understands.</summary>
    /// <param name="key">The property name, such as <c>media.class</c> or <c>node.nick</c>.</param>
    /// <param name="value">Its value.</param>
    /// <remarks>
    /// <para>
    /// The general form, because the set of useful keys is PipeWire's rather than this library's:
    /// <c>media.class</c> decides whether the node is read from or written to, <c>audio.position</c>
    /// decides how many ports it has and what they are called, and beyond those are
    /// <c>node.nick</c>, <c>node.virtual</c>, <c>node.group</c>, <c>priority.driver</c>,
    /// <c>target.object</c> and the audio format keys, with more arriving each release. Naming a
    /// method per key would be out of date the first time that happened.
    /// </para>
    /// <para>
    /// A caller's value wins over the library's default for the same key. The factory is refused
    /// rather than overridden: the node is made by the null-audio-sink factory, and a different
    /// factory is a different kind of object rather than a property of this one.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> is empty, or names the factory.
    /// </exception>
    public PipeWireNodeCreation WithProperty(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        PipeWireObjectOptions.ThrowIfReserved(key, forLink: false);

        ImmutableArray<KeyValuePair<string, string>> existing =
            _options.Properties.IsDefault ? [] : _options.Properties;

        return new(_registry, _description, _name,
            _options with { Properties = existing.Add(new(key, value)) });
    }

    /// <summary>Sets <c>media.class</c>: whether the node is read from or written to.</summary>
    /// <param name="mediaClass">
    /// <c>Audio/Sink</c> by default. <c>Audio/Source</c> makes a node other clients capture from,
    /// which is how a virtual microphone is built rather than a virtual speaker.
    /// </param>
    /// <remarks>
    /// Sugar over <see cref="WithProperty"/> for one of the two keys that change the node's shape
    /// rather than its labelling. The factory behind this is the null audio sink, so the node
    /// carries audio whatever the class says.
    /// </remarks>
    public PipeWireNodeCreation WithMediaClass(string mediaClass) =>
        WithProperty("media.class", mediaClass);

    /// <summary>Sets <c>audio.position</c>: the channel map, and so the port count.</summary>
    /// <param name="positions">
    /// SPA's notation, such as <c>[ MONO ]</c>, <c>[ FL FR ]</c> or
    /// <c>[ FL FR FC LFE SL SR ]</c>. Stereo by default.
    /// </param>
    /// <remarks>
    /// One port per entry, named after it. A caller linking by port name depends on this, so it
    /// changes the node's whole shape rather than a detail of it.
    /// </remarks>
    public PipeWireNodeCreation WithChannelPositions(string positions) =>
        WithProperty("audio.position", positions);

    /// <summary>Sets <c>target.object</c>: what the session manager should link this node to.</summary>
    /// <param name="node">The node to link to. Its <c>node.name</c> is what travels.</param>
    /// <remarks>
    /// A request to the session manager, not a link. The node is linked when it is connected and
    /// only if the session manager agrees; <see cref="PipeWireRegistry.CreateLink(PipeWirePort, PipeWirePort)"/> is the way to
    /// make one unconditionally.
    /// <para>
    /// <c>node.target</c> is deliberately not offered. Upstream deprecated it in 0.3.64 in favour of
    /// this key (<c>pipewire/keys.h:390</c>), and a session manager that honours both will take
    /// whichever it reads first.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="node"/> has no name to target by.</exception>
    public PipeWireNodeCreation WithTarget(PipeWireNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (string.IsNullOrEmpty(node.NodeName))
        {
            throw new ArgumentException(
                $"node {node.NodeId} has no node.name, so there is nothing to target it by. "
                + "Target it by object.serial instead.",
                nameof(node));
        }

        return WithTarget(node.NodeName!);
    }

    /// <inheritdoc cref="WithTarget(PipeWireNode)"/>
    /// <param name="nameOrSerial">
    /// A <c>node.name</c> or an <c>object.serial</c>. The serial is the stable one: names are
    /// reused when a device is removed and reappears, serials are not.
    /// </param>
    public PipeWireNodeCreation WithTarget(string nameOrSerial)
    {
        ArgumentException.ThrowIfNullOrEmpty(nameOrSerial);
        return WithProperty("target.object", nameOrSerial);
    }

    /// <summary>Sets <c>node.autoconnect</c>: whether the session manager may link this node.</summary>
    /// <param name="autoConnect">
    /// True to let the session manager link it to a compatible node, false to leave it unlinked
    /// until something links it explicitly.
    /// </param>
    /// <remarks>
    /// False is the choice for a node whose links a caller is going to make itself: with it on, the
    /// session manager links the node the moment it appears, and those links then have to be found
    /// and removed before the intended ones can be made.
    /// </remarks>
    public PipeWireNodeCreation WithAutoConnect(bool autoConnect) =>
        WithProperty("node.autoconnect", autoConnect ? "true" : "false");

    /// <summary>Sets <c>node.dont-reconnect</c>: end the node rather than move it.</summary>
    /// <remarks>
    /// Without this, a node whose target disappears is relinked to whatever the session manager
    /// picks next, which is convenient for a media player and wrong for anything that cares which
    /// device it is on: audio keeps flowing, somewhere else, with nothing in the API to say so.
    /// <para>
    /// The cost is stated plainly upstream: with this set the node is <em>destroyed</em> when its
    /// target goes (<c>pipewire/keys.h:182-186</c>), not merely unlinked. A caller wanting the node
    /// to survive its target has to watch the graph and relink instead.
    /// </para>
    /// </remarks>
    public PipeWireNodeCreation WithStayWithTheTarget() =>
        WithProperty("node.dont-reconnect", "true");

    /// <summary>Creates the node and returns it once the graph reports it.</summary>
    /// <exception cref="InvalidOperationException">The daemon refused the request.</exception>
    public Task<PipeWireNode> ExecuteAsync(CancellationToken cancellationToken = default) =>
        _registry.ExecuteNodeCreationAsync(_description, _name, _options, cancellationToken);
}
