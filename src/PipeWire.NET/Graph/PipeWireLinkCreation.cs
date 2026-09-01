using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

/// <summary>
/// A link that has been described but not yet created.
/// </summary>
/// <remarks>
/// A <see langword="struct"/>, so chaining costs no allocation. Nothing reaches the daemon until
/// <see cref="ExecuteAsync"/> is awaited.
/// </remarks>
/// <example>
/// <code>
/// PipeWireLink link = await registry.CreateLink(output, input)
///                                   .WithLinger()
///                                   .Passive()
///                                   .ExecuteAsync(ct);
/// </code>
/// </example>
[SupportedOSPlatform("linux")]
public readonly struct PipeWireLinkCreation
{
    private readonly PipeWireRegistry _registry;
    private readonly PipeWirePort _output;
    private readonly PipeWirePort _input;
    private readonly PipeWireObjectOptions _options;

    internal PipeWireLinkCreation(
        PipeWireRegistry registry, PipeWirePort output, PipeWirePort input, PipeWireObjectOptions options)
    {
        _registry = registry;
        _output = output;
        _input = input;
        _options = options;
    }

    /// <summary>
    /// Keeps the link alive after this client disconnects (<c>object.linger</c>).
    /// </summary>
    /// <remarks>
    /// This is what a patchbay wants: routing the user set up should survive closing the app. A
    /// lingering link cannot be removed by disconnecting - call
    /// <see cref="PipeWireRegistry.RemoveLinkAsync(PipeWireLink, CancellationToken)"/>.
    /// </remarks>
    public PipeWireLinkCreation WithLinger() =>
        new(_registry, _output, _input, _options with { Linger = true });

    /// <summary>
    /// Marks the link passive (<c>link.passive</c>), so it does not by itself keep its endpoints
    /// running when nothing else is driving them.
    /// </summary>
    public PipeWireLinkCreation Passive() =>
        new(_registry, _output, _input, _options with { Passive = true });

    /// <summary>Creates the link and returns it once the graph reports it.</summary>
    /// <exception cref="InvalidOperationException">The daemon refused the request.</exception>
    public Task<PipeWireLink> ExecuteAsync(CancellationToken cancellationToken = default) =>
        _registry.ExecuteLinkCreationAsync(_output, _input, _options, cancellationToken);
}
