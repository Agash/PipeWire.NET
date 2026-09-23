using System.Collections.Immutable;
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
/// The daemon checks the manager permission (<c>M</c>) on the client object being changed. A
/// restricted caller without it is refused, out of band, on the core's error stream, which is why
/// <see cref="UpdatePermissionsAsync"/> round-trips rather than returning as soon as the call is
/// made. An unrestricted client holds <c>M</c> on every other client by default
/// (<c>module-access</c>), so in an ordinary session the change is simply applied: this is a
/// session manager's tool, and it acts on whatever client it is pointed at.
/// </para>
/// <para>
/// <strong>On PipeWire 1.6.8, withdrawing read access can abort the daemon.</strong> When a change
/// takes <c>R</c> away, <c>pw_global_update_permissions</c> destroys the client's resources on that
/// object while walking the object's resource list, called from a walk over every object. A destroy
/// can take other resources with it, and the object itself when the client exported it, so the walks
/// continue into destroyed or freed memory (<c>assert(!resource->destroyed)</c>, or a segfault).
/// Whether it happens depends on what the target holds and serves, the caller's own client included;
/// the round-trip then never answers and the caller sees a cancellation. Fixed by the upstream patch this
/// repository carries (<c>repro/libpipewire-permissions.patch</c>).
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class PipeWireClientProxy : IDisposable, IAsyncDisposable
{
    /// <summary>The object id that means "everything not named individually".</summary>
    /// <remarks>
    /// Permissions are matched most-specific-first, so a default of <c>None</c> plus a handful of
    /// explicit grants is how a client is confined to exactly the objects it needs.
    /// </remarks>
    public const uint AnyObject = uint.MaxValue;

    private readonly PipeWireContext _ctx;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    private BoundProxy? _bound;
    private volatile bool _disposed;

    // Written on the loop thread from the info event, read from anywhere.
    private volatile PipeWireProperties _properties = PipeWireProperties.Empty;

    // One read at a time: the reply arrives on the loop thread as a permissions event, and the
    // waiter is completed from there.
    private readonly Lock _permissionsGate = new();
    private TaskCompletionSource<ImmutableArray<PipeWireObjectPermission>>? _permissionsWaiter;

    private PipeWireClientProxy(PipeWireContext ctx, uint id, ILogger logger)
    {
        _ctx = ctx;
        Id = id;
        _logger = logger;
    }

    /// <summary>The global id of the client this is bound to.</summary>
    public uint Id { get; }

    internal static unsafe PipeWireClientProxy Bind(
        PipeWireContext ctx,
        pw_registry* registry,
        uint id,
        uint version,
        ILogger logger,
        Action<uint, PipeWireProperties>? propertiesObserved = null
    )
    {
        var control = new PipeWireClientProxy(ctx, id, logger)
        {
            PropertiesObserved = propertiesObserved,
        };
        control._bound = BoundProxy.Bind(
            ctx,
            registry,
            id,
            PipeWireKeys.PW_TYPE_INTERFACE_Client,
            version,
            NativeConstants.PW_VERSION_CLIENT,
            sizeof(pw_client_events),
            events =>
            {
                var table = (pw_client_events*)events;
                table->version = NativeConstants.PW_VERSION_CLIENT_EVENTS;
                table->info = &OnInfoCallback;
                table->permissions = &OnPermissionsCallback;
            },
            static (proxy, hook, events, data) =>
                Native.pw_client_add_listener(
                    (pw_client*)proxy,
                    (spa_hook*)hook,
                    (pw_client_events*)events,
                    (void*)data
                ),
            control
        );

        control._bound.Removed = control.RaiseRemoved;

        return control;
    }

    /// <summary>Raised on the loop thread when the daemon destroys the object behind this proxy.</summary>
    /// <remarks>
    /// A bound object can go at any time. The proxy survives as a zombie and every call through it
    /// fails from here on, so this is the signal to stop using it. A caller watching the whole graph
    /// sees the same thing through the registry; one holding only this proxy has nothing else.
    /// </remarks>
    public event Action? Removed;

    /// <summary>Whether the daemon has destroyed the object behind this proxy.</summary>
    public bool IsRemoved => _bound?.IsRemoved ?? false;

    private void RaiseRemoved()
    {
        Action? handler = Removed;
        if (handler is null)
            return;

        // A native callback frame, so nothing may escape it.
        try
        {
            handler();
        }
        catch (Exception)
        { /* a subscriber that throws must not reach the daemon */
        }
    }

    /// <summary>This client's properties, as the daemon last reported them.</summary>
    /// <remarks>
    /// Empty until the first <c>info</c> event arrives; <see cref="ReadyAsync"/> waits for it. A
    /// client's properties change while it runs - an application that retags itself through
    /// <see cref="PipeWireContext.UpdateProperties"/> is the ordinary case - and the daemon sends a
    /// fresh <c>info</c> to every bound resource when they do, which is what keeps this current.
    /// </remarks>
    public PipeWireProperties Properties => _properties;

    /// <summary>Waits for the daemon to send this client's first <c>info</c> event.</summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// Binding is a request; until the daemon answers it there are no properties to read. Every
    /// other bound type in this library reports readiness the same way.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The proxy has been disposed.</exception>
    public Task ReadyAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _ready.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Where an <c>info</c> event's properties go, set by the registry that made this object.
    /// </summary>
    internal Action<uint, PipeWireProperties>? PropertiesObserved { get; set; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnInfoCallback(void* data, pw_client_info* info)
    {
        // An exception escaping a reverse P/Invoke aborts the process, so nothing here may throw.
        try
        {
            if (info is null)
                return;
            if (GCHandle.FromIntPtr((nint)data).Target is not PipeWireClientProxy self)
                return;
            if (self._disposed)
                return;

            if (info->props is not null)
            {
                PipeWireProperties properties = PipeWireProperties.From(info->props);
                self._properties = properties;

                try
                {
                    self.PropertiesObserved?.Invoke(self.Id, properties);
                }
                catch (Exception ex)
                {
                    self.LogHandlerFaulted(self.Id, ex);
                }
            }

            self._ready.TrySetResult();
        }
        catch
        {
            // Deliberately not logged: the instance the logger belongs to is what failed to resolve.
        }
    }

    /// <summary>Reads what this client is currently permitted to do.</summary>
    /// <param name="index">The first entry to read; 0 for the start of the list.</param>
    /// <param name="count">How many entries to ask for.</param>
    /// <param name="cancellationToken">Abandons the wait for the daemon's answer.</param>
    /// <returns>The entries the daemon answered with, in its order.</returns>
    /// <remarks>
    /// <para>
    /// The counterpart to <see cref="UpdatePermissionsAsync"/>: without it a caller can confine a
    /// client and never see what it actually holds, which is the half that matters when checking
    /// that a sandbox came out as intended. The daemon answers on the client's <c>permissions</c>
    /// event, so this is a round trip rather than a read of local state.
    /// </para>
    /// <para>
    /// An entry with <see cref="AnyObject"/> as its id is the default applied to everything with no
    /// entry of its own.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The proxy has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Another read is already in flight.</exception>
    public async Task<ImmutableArray<PipeWireObjectPermission>> GetPermissionsAsync(
        uint index = 0,
        uint count = 64,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var waiter = new TaskCompletionSource<ImmutableArray<PipeWireObjectPermission>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        lock (_permissionsGate)
        {
            if (_permissionsWaiter is not null)
                throw new InvalidOperationException("a permissions read is already in flight.");

            _permissionsWaiter = waiter;
        }

        try
        {
            int res = Read(index, count);
            if (res < 0)
                throw new PipeWireException("pw_client_get_permissions", res, Id);

            return await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_permissionsGate)
            {
                if (ReferenceEquals(_permissionsWaiter, waiter))
                    _permissionsWaiter = null;
            }
        }

        unsafe int Read(uint from, uint howMany)
        {
            BoundProxy proxy =
                _bound ?? throw new ObjectDisposedException(nameof(PipeWireClientProxy));

            using (_ctx.Lock())
                return Native.pw_client_get_permissions((pw_client*)proxy.Object, from, howMany);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnPermissionsCallback(
        void* data,
        uint index,
        uint count,
        pw_permission* permissions
    )
    {
        // An exception escaping a reverse P/Invoke aborts the process, so nothing here may throw.
        try
        {
            if (GCHandle.FromIntPtr((nint)data).Target is not PipeWireClientProxy self)
                return;

            var entries = ImmutableArray.CreateBuilder<PipeWireObjectPermission>((int)count);
            for (uint i = 0; i < count && permissions is not null; i++)
            {
                entries.Add(
                    new PipeWireObjectPermission(
                        permissions[i].id,
                        (PipeWirePermissions)permissions[i].permissions
                    )
                );
            }

            TaskCompletionSource<ImmutableArray<PipeWireObjectPermission>>? waiter;
            lock (self._permissionsGate)
                waiter = self._permissionsWaiter;

            waiter?.TrySetResult(entries.ToImmutable());
        }
        catch
        {
            // Deliberately not logged: the instance the logger belongs to is what failed to resolve.
        }
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
    /// Withdrawing read access can abort a 1.6.8 daemon; see the remarks on this class.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="permissions"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">The daemon refused.</exception>
    public async Task UpdatePermissionsAsync(
        ReadOnlyMemory<PipeWireObjectPermission> permissions,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (permissions.IsEmpty)
            throw new ArgumentException(
                "at least one permission is required.",
                nameof(permissions)
            );

        // Bits the daemon does not define are a caller mistake, not a forward-compatible extension:
        // permission.h has held the same five since 0.3.77, and a stray bit is either a cast from
        // the wrong enum or arithmetic that went wrong. Forwarding it asks the daemon to interpret
        // a number this library cannot describe.
        //
        // The mask is RWXML, not All: upstream's PW_PERM_ALL is RWXM and leaves out L, so masking
        // with All would refuse a link grant that the daemon accepts.
        const PipeWirePermissions defined = PipeWirePermissions.ReadWriteExecuteMetadataLink;

        foreach (PipeWireObjectPermission entry in permissions.Span)
        {
            if ((entry.Permissions & ~defined) == 0)
                continue;

            throw new ArgumentException(
                $"object {entry.ObjectId} carries permission bits this library does not define: "
                    + $"0x{(uint)(entry.Permissions & ~defined):x}.",
                nameof(permissions)
            );
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Inside the round-trip: a permission change the daemon refuses is answered out of band,
        // and a listener attached afterwards can miss it entirely.
        await CoreSync
            .RoundTripAsync(_ctx, () => Write(permissions.Span), cancellationToken)
            .ConfigureAwait(false);
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
    /// The deny-everything default withdraws read access from everything not granted, which is the
    /// change that can abort a 1.6.8 daemon; see the remarks on this class.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="allowed"/> names <see cref="AnyObject"/>, which is not a grant.
    /// </exception>
    public Task ConfineToAsync(
        ReadOnlySpan<PipeWireObjectPermission> allowed,
        CancellationToken cancellationToken = default
    )
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
                    nameof(allowed)
                );
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
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(properties);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await CoreSync
            .RoundTripAsync(_ctx, () => WriteProperties(properties), cancellationToken)
            .ConfigureAwait(false);
    }

    private unsafe int Write(ReadOnlySpan<PipeWireObjectPermission> permissions)
    {
        Span<pw_permission> native =
            permissions.Length <= 16
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
            throw new ObjectDisposedException(nameof(PipeWireClientProxy));

        using (proxy)
        using (_ctx.Lock())
        {
            fixed (pw_permission* p = native)
                return Native.pw_client_update_permissions(
                    (pw_client*)proxy.Object,
                    (uint)native.Length,
                    p
                );
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
        Span<byte> scratch =
            bytes <= 1024
                ? stackalloc byte[bytes]
                : GC.AllocateUninitializedArray<byte>(bytes, pinned: true);
        Span<spa_dict_item> items =
            properties.Count <= 32
                ? stackalloc spa_dict_item[properties.Count]
                : GC.AllocateArray<spa_dict_item>(properties.Count, pinned: true);

        var builder = new SpaDictBuilder(scratch, items);
        foreach ((string key, string value) in properties)
            builder.Add(key, value);

        // Referenced for the duration of the call. Destroying a proxy clears its pointer before it
        // takes the loop lock, so the lock alone does not stop this becoming null mid-call.
        if (!_bound!.TryUse(out BoundProxy.Use proxy))
            throw new ObjectDisposedException(nameof(PipeWireClientProxy));

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
        if (_disposed)
            return;
        _disposed = true;

        _bound?.Dispose();
        _bound = null;

        GC.SuppressFinalize(this);
    }

    [LoggerMessage(
        EventId = 34700,
        Level = LogLevel.Warning,
        Message = "a properties handler for client {ClientId} threw"
    )]
    private partial void LogHandlerFaulted(uint clientId, Exception exception);
}
