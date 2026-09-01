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
/// PipeWireNode node = await registry.CreateVirtualStereoNode("Monitor mix")
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
    /// A lingering object cannot be removed by disconnecting - destroy it explicitly.
    /// </remarks>
    public PipeWireNodeCreation WithLinger() =>
        new(_registry, _description, _name, _options with { Linger = true });

    /// <summary>Creates the node and returns it once the graph reports it.</summary>
    /// <exception cref="InvalidOperationException">The daemon refused the request.</exception>
    public Task<PipeWireNode> ExecuteAsync(CancellationToken cancellationToken = default) =>
        _registry.ExecuteNodeCreationAsync(_description, _name, _options, cancellationToken);
}
