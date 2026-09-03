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
/// <para>
/// <strong>Do not write permissions to a client you do not manage.</strong> On PipeWire 1.6.8 the
/// daemon does not refuse it, it dies: a default-deny entry against a client the caller has no
/// manager rights over segfaults inside <c>pw_impl_client_update_permissions</c> and takes the
/// session with it, so the round-trip never answers and the caller sees a cancellation rather than
/// the documented refusal. Restricting the connection's own client is safe, and is what
/// <see cref="ConfineToAsync"/> is normally for.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class PipeWireClientControl : IDisposable, IAsyncDisposable
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
            ctx, registry, id, Native.PW_TYPE_INTERFACE_CLIENT, version, Native.PW_VERSION_CLIENT,
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
    /// <para>
    /// Absolute, not a delta: an object listed with fewer bits than it had loses the difference. An
    /// object not listed at all keeps what it had, which is why confining a client starts by setting
    /// <see cref="AnyObject"/> to <see cref="PipeWirePermissions.None"/>.
    /// </para>
    /// <para>
    /// Three rules of the daemon's own, none of them visible from the call. An
    /// <see cref="AnyObject"/> entry changes the default and re-applies it only to objects that have
    /// no entry of their own, so it does not undo grants made in the same array whichever order they
    /// appear in. An id naming an object the daemon does not have is skipped with a log line rather
    /// than reported, so a stale id looks like success. And a client changing its <em>own</em>
    /// permissions can only ever reduce them - the daemon intersects the request with what the
    /// client already had - so a self-directed grant silently does nothing.
    /// </para>
    /// <para>
    /// Writing permissions to a client this connection does not manage can kill the daemon on
    /// 1.6.8; see the remarks on this class.
    /// </para>
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

        // Bits the daemon does not define are a caller mistake, not a forward-compatible extension:
        // permission.h has held the same five since 0.3.77, and a stray bit is either a cast from
        // the wrong enum or arithmetic that went wrong. Forwarding it asks the daemon to interpret
        // a number this library cannot describe.
        foreach (PipeWireObjectPermission entry in permissions.Span)
        {
            if ((entry.Permissions & ~PipeWirePermissions.All) == 0) continue;

            throw new ArgumentException(
                $"object {entry.ObjectId} carries permission bits this library does not define: "
                + $"0x{(uint)(entry.Permissions & ~PipeWirePermissions.All):x}.",
                nameof(permissions));
        }

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
    /// <para>
    /// The safe shape of <see cref="UpdatePermissionsAsync"/>: it writes the deny-everything default
    /// first, so nothing is left permitted by omission.
    /// </para>
    /// <para>
    /// Safe against the connection's own client. Against a client this connection does not manage it
    /// is the exact shape that kills the daemon on 1.6.8; see the remarks on this class.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="allowed"/> names <see cref="AnyObject"/>, which is not a grant.
    /// </exception>
    public Task ConfineToAsync(
        ReadOnlySpan<PipeWireObjectPermission> allowed,
        CancellationToken cancellationToken = default)
    {
        var all = new PipeWireObjectPermission[allowed.Length + 1];
        all[0] = new PipeWireObjectPermission(AnyObject, PipeWirePermissions.None);

        // The default is this method's own, and a second one in the grants contradicts the confining
        // it exists to do. Sending both leaves which one the daemon ends on to the array order,
        // which is not something a caller should have to reason about to get a sandbox.
        foreach (PipeWireObjectPermission grant in allowed)
        {
            if (grant.ObjectId == AnyObject)
            {
                throw new ArgumentException(
                    "AnyObject is the default this method writes for you; it cannot also be a grant. "
                    + "Use UpdatePermissionsAsync to set a default of your own.",
                    nameof(allowed));
            }
        }

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
            throw new ObjectDisposedException(nameof(PipeWireClientControl));

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
            builder.Add(key, value);

        // Referenced for the duration of the call. Destroying a proxy clears its pointer before it
        // takes the loop lock, so the lock alone does not stop this becoming null mid-call.
        if (!_bound!.TryUse(out BoundProxy.Use proxy))
            throw new ObjectDisposedException(nameof(PipeWireClientControl));

        using (proxy)
        using (_ctx.Lock())
        {
            spa_dict dict = builder.Build();
            return Native.pw_client_update_properties((pw_client*)proxy.Object, &dict);
        }
    }

    /// <summary>Tears the binding down. Disposal here does no I/O.</summary>
    /// <remarks>
    /// Offered alongside the async form because nothing about this disposal is asynchronous,
    /// so a caller should not be forced to write "await using" for it.
    /// </remarks>
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

        GC.SuppressFinalize(this);
    }

}
