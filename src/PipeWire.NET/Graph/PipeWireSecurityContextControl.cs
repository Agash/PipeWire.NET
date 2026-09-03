using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Graph;

/// <summary>
/// Creates sandboxed connection points on the daemon's security context.
/// </summary>
/// <remarks>
/// <para>
/// Hands a sandbox a connection it cannot escape. The caller passes a listening socket and a
/// lifetime descriptor. Clients connecting through that socket get the permissions named here, not
/// the creator's, and the daemon drops them all when the lifetime descriptor closes.
/// </para>
/// <para>
/// The descriptors stay the caller's. The daemon duplicates what it needs, so closing the lifetime
/// one is how you end a sandbox.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class PipeWireSecurityContextControl : IDisposable, IAsyncDisposable
{
    private readonly PipeWireContext _ctx;
    private readonly ILogger _logger;
    private BoundProxy? _bound;
    private volatile bool _disposed;

    private PipeWireSecurityContextControl(PipeWireContext ctx, uint id, ILogger logger)
    {
        _ctx = ctx;
        Id = id;
        _logger = logger;
    }

    /// <summary>The security context's global id.</summary>
    public uint Id { get; }

    internal static unsafe PipeWireSecurityContextControl Bind(
        PipeWireContext ctx, pw_registry* registry, uint id, uint version, ILogger logger)
    {
        var control = new PipeWireSecurityContextControl(ctx, id, logger);

        // No events are subscribed: the interface's only event is a lifecycle signal this type has
        // no use for, and the zeroed table keeps the binding shape the same as every other.
        control._bound = BoundProxy.Bind(
            ctx, registry, id, Native.PW_TYPE_INTERFACE_SECURITY_CONTEXT, version, Native.PW_VERSION_SECURITY_CONTEXT,
            sizeof(pw_security_context_events),
            events => ((pw_security_context_events*)events)->version = 0,
            static (_, _, _, _) => 0,
            control);

        return control;
    }

    /// <summary>
    /// Opens a sandboxed connection point.
    /// </summary>
    /// <param name="listenFd">A listening socket the daemon accepts sandboxed clients on.</param>
    /// <param name="closeFd">Closing this tells the daemon the sandbox is gone.</param>
    /// <param name="properties">
    /// The properties every client connecting through <paramref name="listenFd"/> is given, such as
    /// <c>pipewire.access</c> and <c>pipewire.sec.engine</c>.
    /// </param>
    /// <param name="cancellationToken">
    /// Abandons the wait. The request is already on its way, so cancelling does not recall
    /// it: the daemon can still apply the change after this throws.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">A descriptor is negative.</exception>
    /// <exception cref="ObjectDisposedException">This control has been disposed.</exception>
    /// <exception cref="PipeWireException">The daemon refused the request.</exception>
    public async Task CreateAsync(
        int listenFd,
        int closeFd,
        IReadOnlyDictionary<string, string> properties,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentOutOfRangeException.ThrowIfNegative(listenFd);
        ArgumentOutOfRangeException.ThrowIfNegative(closeFd);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        await CoreSync.RoundTripAsync(_ctx, () => Create(listenFd, closeFd, properties), cancellationToken)
            .ConfigureAwait(false);

        LogCreated(Id, properties.Count);
    }

    private unsafe int Create(int listenFd, int closeFd, IReadOnlyDictionary<string, string> properties)
    {
        int bytes = 0;
        foreach ((string key, string value) in properties)
            bytes += Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value) + 2;

        // A stackalloc cannot move; a heap fallback can, and this array's address goes to native
        // code, so the fallback has to be pinned.
        Span<byte> scratch = bytes <= 512
            ? stackalloc byte[512]
            : GC.AllocateUninitializedArray<byte>(bytes, pinned: true);
        Span<spa_dict_item> items = properties.Count <= 16
            ? stackalloc spa_dict_item[16]
            : GC.AllocateUninitializedArray<spa_dict_item>(properties.Count, pinned: true);

        var builder = new SpaDictBuilder(scratch, items);
        foreach ((string key, string value) in properties)
            builder.Add(key, value);

        if (!_bound!.TryUse(out BoundProxy.Use proxy))
            throw new ObjectDisposedException(nameof(PipeWireSecurityContextControl));

        using (proxy)
        using (_ctx.Lock())
        {
            spa_dict dict = builder.Build();
            return Native.pw_security_context_create(proxy.Object, listenFd, closeFd, &dict);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => DisposeCore();

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        DisposeCore();
        return ValueTask.CompletedTask;
    }

    private void DisposeCore()
    {
        if (_disposed) return;
        _disposed = true;

        _bound?.Dispose();
        _bound = null;
    }

    [LoggerMessage(EventId = 34100, Level = LogLevel.Information,
                   Message = "security context {ContextId} opened a sandbox with {PropertyCount} propertie(s)")]
    private partial void LogCreated(uint contextId, int propertyCount);
}
