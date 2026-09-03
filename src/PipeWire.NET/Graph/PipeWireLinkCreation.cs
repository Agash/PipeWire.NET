using System.Collections.Immutable;
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

    /// <summary>Sets any creation property the daemon understands.</summary>
    /// <param name="key">The property name, such as <c>link.passive</c> or <c>object.linger</c>.</param>
    /// <param name="value">Its value.</param>
    /// <remarks>
    /// The general form, for the same reason as on node creation: the useful keys are PipeWire's
    /// and grow with each release. A caller's value wins over the library's default for the same
    /// key. The endpoints and the factory are refused rather than overridden - they are what the
    /// link is, not properties of it, and setting one here would route the link somewhere the
    /// caller did not ask for or hand the request to a different factory.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> is empty, or names the factory or one of the endpoints.
    /// </exception>
    public PipeWireLinkCreation WithProperty(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        PipeWireObjectOptions.ThrowIfReserved(key, forLink: true);

        ImmutableArray<KeyValuePair<string, string>> existing =
            _options.Properties.IsDefault ? [] : _options.Properties;

        return new(_registry, _output, _input,
            _options with { Properties = existing.Add(new(key, value)) });
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
