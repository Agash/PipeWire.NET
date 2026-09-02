using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Graph;

/// <summary>
/// A client bound for changing what it is permitted to do.
/// </summary>
/// <remarks>
/// <para>
/// This is the session manager's side of sandboxing: a restricted client connects, and something
/// with the manager permission decides which objects it may see, read, write or link. Without that
/// decision a restricted client sees an empty graph.
/// </para>
/// <para>
/// An ordinary application cannot do this to another client - the daemon refuses - and it is
/// refused out of band, on the core's error stream. That is why
/// <see cref="UpdatePermissionsAsync"/> round-trips rather than returning as soon as the call is
/// made.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class PipeWireClientControl : IAsyncDisposable
{
    /// <summary>The object id that means "everything not named individually".</summary>
    /// <remarks>
    /// Permissions are matched most-specific-first, so a default of <c>None</c> plus a handful of
    /// explicit grants is how a client is confined to exactly the objects it needs.
    /// </remarks>
    public const uint AnyObject = uint.MaxValue;

    private readonly PipeWireContext _ctx;
    private readonly ILogger _logger;
    private BoundProxy? _bound;
    private volatile bool _disposed;

    private PipeWireClientControl(PipeWireContext ctx, uint id, ILogger logger)
    {
        _ctx = ctx;
        Id = id;
        _logger = logger;
    }

    /// <summary>The global id of the client this is bound to.</summary>
    public uint Id { get; }

    internal static unsafe PipeWireClientControl Bind(
        PipeWireContext ctx, pw_registry* registry, uint id, uint version, ILogger logger)
    {
        var control = new PipeWireClientControl(ctx, id, logger);
        control._bound = BoundProxy.Bind(
            ctx, registry, id, Native.PW_TYPE_INTERFACE_CLIENT, version,
            sizeof(pw_client_events),
            events => ((pw_client_events*)events)->version = Native.PW_VERSION_CLIENT_EVENTS,
            static (proxy, hook, events, data) => Native.pw_client_add_listener(
                (pw_client*)proxy, (spa_hook*)hook, (pw_client_events*)events, (void*)data),
            control);

        return control;
    }

    /// <summary>
    /// Replaces what this client may do with the objects named.
    /// </summary>
    /// <param name="permissions">
    /// One entry per object, or <see cref="AnyObject"/> for the default applied to everything else.
    /// </param>
    /// <param name="cancellationToken">Abandons the wait for the daemon to catch up.</param>
    /// <remarks>
    /// Absolute, not a delta: an object listed with fewer bits than it had loses the difference. An
    /// object not listed at all keeps what it had, which is why confining a client starts by setting
    /// <see cref="AnyObject"/> to <see cref="PipeWirePermissions.None"/>.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="permissions"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">The daemon refused.</exception>
    public async Task UpdatePermissionsAsync(
        ReadOnlyMemory<PipeWireObjectPermission> permissions,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (permissions.IsEmpty)
            throw new ArgumentException("at least one permission is required.", nameof(permissions));

        cancellationToken.ThrowIfCancellationRequested();

        // Inside the round-trip: a permission change the daemon refuses is answered out of band,
        // and a listener attached afterwards can miss it entirely.
        await CoreSync.RoundTripAsync(
            _ctx, () => Write(permissions.Span), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Confines the client to exactly the objects listed, and nothing else.
    /// </summary>
    /// <param name="allowed">The objects it may see, and what it may do with each.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// The safe shape of <see cref="UpdatePermissionsAsync"/>: it writes the deny-everything default
    /// first, so nothing is left permitted by omission.
    /// </remarks>
    public Task ConfineToAsync(
        ReadOnlySpan<PipeWireObjectPermission> allowed,
        CancellationToken cancellationToken = default)
    {
        var all = new PipeWireObjectPermission[allowed.Length + 1];
        all[0] = new PipeWireObjectPermission(AnyObject, PipeWirePermissions.None);
        allowed.CopyTo(all.AsSpan(1));
        return UpdatePermissionsAsync(all, cancellationToken);
    }

    /// <summary>
    /// Adds or replaces properties on the client, such as its reported application name.
    /// </summary>
    /// <param name="properties">The properties to write.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <exception cref="ArgumentNullException"><paramref name="properties"/> is <see langword="null"/>.</exception>
    public async Task UpdatePropertiesAsync(
        IReadOnlyDictionary<string, string> properties,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await CoreSync.RoundTripAsync(
            _ctx, () => WriteProperties(properties), cancellationToken).ConfigureAwait(false);
    }

    private unsafe int Write(ReadOnlySpan<PipeWireObjectPermission> permissions)
    {
        Span<pw_permission> native = permissions.Length <= 16
            ? stackalloc pw_permission[permissions.Length]
            : new pw_permission[permissions.Length];

        for (int i = 0; i < permissions.Length; i++)
        {
            native[i].id = permissions[i].ObjectId;
            native[i].permissions = (uint)permissions[i].Permissions;
        }

        // Referenced for the duration of the call. Destroying a proxy clears its pointer before it
        // takes the loop lock, so the lock alone does not stop this becoming null mid-call.
        if (!_bound!.TryUse(out BoundProxy.Use proxy))
            throw new ObjectDisposedException(GetType().Name);

        using (proxy)
        using (_ctx.Lock())
        {
            fixed (pw_permission* p = native)
                return Native.pw_client_update_permissions((pw_client*)proxy.Object, (uint)native.Length, p);
        }
    }

    private unsafe int WriteProperties(IReadOnlyDictionary<string, string> properties)
    {
        int bytes = 0;
        foreach ((string key, string value) in properties)
            bytes += Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value) + 2;

        // The dictionary holds raw pointers into these buffers, so they must not move between being
        // filled and the native call reading them. A stackalloc cannot move; a plain array can, so
        // the heap fallback allocates out of the pinned object heap rather than the normal one.
        Span<byte> scratch = bytes <= 1024
            ? stackalloc byte[bytes]
            : GC.AllocateUninitializedArray<byte>(bytes, pinned: true);
        Span<spa_dict_item> items = properties.Count <= 32
            ? stackalloc spa_dict_item[properties.Count]
            : GC.AllocateArray<spa_dict_item>(properties.Count, pinned: true);

        var builder = new SpaDictBuilder(scratch, items);
        foreach ((string key, string value) in properties)
            builder.Add(Encoding.UTF8.GetBytes(key), value);

        // Referenced for the duration of the call. Destroying a proxy clears its pointer before it
        // takes the loop lock, so the lock alone does not stop this becoming null mid-call.
        if (!_bound!.TryUse(out BoundProxy.Use proxy))
            throw new ObjectDisposedException(GetType().Name);

        using (proxy)
        using (_ctx.Lock())
        {
            spa_dict dict = builder.Build();
            return Native.pw_client_update_properties((pw_client*)proxy.Object, &dict);
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        _bound?.Dispose();
        _bound = null;

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
