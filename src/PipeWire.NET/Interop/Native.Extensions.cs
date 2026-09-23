// Hand-maintained extension of the generated `Native` partial class.
//
// Why this file lives outside generated/: generate/generate.sh wipes the
// generated/ directory on each regeneration. Anything we hand-write must live
// elsewhere. We keep the same `PipeWire.NET.Interop` namespace and the same
// `static partial class Native` so the rest of this assembly sees these symbols seamlessly
// alongside generated declarations (e.g. `Native.pw_stream_new`).
//
// Contents: what cannot be generated. The PW_VERSION_* interface versions and the PW_ID_* /
// SPA_ID_INVALID sentinels used to be copied here; they are generated now (NativeConstants), and the
// copy held one version name upstream never had.
//   - SPA interface VTBL dispatch helpers (pw_core_get_registry,
//     pw_registry_add_listener) - these are C macros, not exported symbols.
//
// Verify against /usr/include/pipewire-0.3/pipewire/*.h when bumping PipeWire.

#pragma warning disable CS1591 // Missing XML comment - matches the suppression in generated/*.g.cs
#pragma warning disable CA1707 // Identifiers should not contain underscores (matches generated style)
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix

using System.Runtime.InteropServices;

namespace PipeWire.NET.Interop;

internal static unsafe partial class Native
{
    // - SPA interface dispatch -
    // The PipeWire C API exposes many methods as macros that dispatch through
    // an SPA interface VTBL (struct spa_interface { spa_callbacks { funcs, data } }).
    // Each pw_* object (pw_core, pw_registry, ...) begins with a spa_interface,
    // so we cast object* -> spa_interface* and walk the callback table.

    /// <summary>Reads the SPA interface VTBL from a PipeWire object.</summary>
    /// <typeparam name="TMethods">The methods VTBL struct (e.g. <c>pw_core_methods</c>).</typeparam>
    /// <param name="obj">The PipeWire object (pw_core*, pw_registry*, etc.).</param>
    /// <param name="methods">[out] The typed methods VTBL.</param>
    /// <param name="userData">[out] User-data pointer to pass as the first arg of each method call.</param>
    internal static void GetInterface<TMethods>(
        void* obj,
        out TMethods* methods,
        out void* userData
    )
        where TMethods : unmanaged
    {
        ArgumentNullException.ThrowIfNull(obj);
        var iface = (spa_interface*)obj;
        methods = (TMethods*)iface->cb.funcs;
        userData = iface->cb.data;
    }

    // - Loop control -
    //
    // pw_loop_get_fd and friends are PW_API_LOOP_IMPL: static inlines that dispatch through the
    // loop's spa_loop_control interface. The generator is set to funcs-with-body=false, so it
    // cannot emit them and they are dispatched here instead - the same walk as every other SPA
    // method above, over the now-generated spa_loop_control_methods vtable.

    /// <summary>
    /// The descriptor that becomes readable when the loop has work pending.
    /// </summary>
    /// <remarks>
    /// What lets a host application drive this loop from its own event loop instead of leaving it
    /// to a thread of its own: poll this alongside everything else the host already waits on, and
    /// call <see cref="pw_loop_iterate"/> with a zero timeout when it signals. Upstream's
    /// <c>gmain</c> example wraps exactly this fd in a GSource.
    /// </remarks>
    internal static int pw_loop_get_fd(pw_loop* loop)
    {
        if (loop is null || loop->control is null)
            return -1;
        GetInterface(loop->control, out spa_loop_control_methods* m, out void* data);
        return m is null || m->get_fd is null ? -1 : m->get_fd(data);
    }

    /// <summary>Dispatches whatever the loop has ready, waiting up to <paramref name="timeoutMs"/>.</summary>
    /// <remarks>A zero timeout is the non-blocking form a host loop uses after the fd signalled.</remarks>
    internal static int pw_loop_iterate(pw_loop* loop, int timeoutMs)
    {
        if (loop is null || loop->control is null)
            return -1;
        GetInterface(loop->control, out spa_loop_control_methods* m, out void* data);
        return m is null || m->iterate is null ? -1 : m->iterate(data, timeoutMs);
    }

    /// <summary>Claims the loop for the calling thread. Paired with <see cref="pw_loop_leave"/>.</summary>
    /// <remarks>
    /// SPA requires enter and leave once each from the thread that will iterate, so the loop knows
    /// which thread its callbacks run on. Skipping it makes every "am I on the loop thread" check
    /// inside PipeWire answer wrongly.
    /// </remarks>
    internal static void pw_loop_enter(pw_loop* loop)
    {
        if (loop is null || loop->control is null)
            return;
        GetInterface(loop->control, out spa_loop_control_methods* m, out void* data);
        if (m is not null && m->enter is not null)
            m->enter(data);
    }

    /// <inheritdoc cref="pw_loop_enter"/>
    internal static void pw_loop_leave(pw_loop* loop)
    {
        if (loop is null || loop->control is null)
            return;
        GetInterface(loop->control, out spa_loop_control_methods* m, out void* data);
        if (m is not null && m->leave is not null)
            m->leave(data);
    }

    /// <summary>
    /// <c>pw_loop_unlock</c>: releases one hold of the loop's lock and says whether it did.
    /// </summary>
    /// <returns>0, or the negative errno the loop refused with.</returns>
    /// <remarks>
    /// Upstream's <c>pw_thread_loop_unlock</c> calls this and discards the result, so a refusal is
    /// silent - and it can refuse: SPA's <c>loop_unlock</c> returns <c>-EIO</c> without unlocking
    /// when the loop's hold count (<c>impl-&gt;recurse</c>, shared by every thread) is already 0
    /// (spa/plugins/support/loop.c). The mutex then stays held by the calling thread, and the next
    /// thread to need it - the loop thread itself, or a join in <c>pw_thread_loop_stop</c> - waits
    /// for ever. Checked here so that state is reported where it starts instead of found later as
    /// a deadlock.
    /// </remarks>
    internal static int pw_loop_unlock(pw_loop* loop)
    {
        if (loop is null || loop->control is null)
            return -NativeLibc.EINVAL;
        GetInterface(loop->control, out spa_loop_control_methods* m, out void* data);
        if (m is null || m->unlock is null)
            return -NativeLibc.EOPNOTSUPP;
        return m->unlock(data);
    }

    /// <summary><c>pw_thread_loop_unlock</c>, with the result upstream's discards.</summary>
    /// <inheritdoc cref="pw_loop_unlock" path="/remarks"/>
    internal static int pw_thread_loop_unlock_checked(pw_thread_loop* loop) =>
        loop is null ? -NativeLibc.EINVAL : pw_loop_unlock(pw_thread_loop_get_loop(loop));

    /// <summary>
    /// Runs <paramref name="func"/> with the loop's lock held, so it cannot overlap that loop's
    /// own callbacks. Synchronous: <paramref name="func"/> has returned when this does.
    /// </summary>
    /// <returns>What <paramref name="func"/> returned, or <c>-ENOTSUP</c> if the loop has no
    /// <c>locked</c> method.</returns>
    /// <remarks>
    /// How upstream changes state a realtime callback reads. <c>spa_node.port_set_io</c> may be
    /// called while the node is running, and its contract says the change is "normally done by
    /// synchronizing the port io updates with the data processing loop"; audioconvert does exactly
    /// that with <c>spa_loop_locked(this->data_loop, do_set_port_io, ...)</c>. Safe from any thread.
    /// </remarks>
    internal static int pw_loop_locked(
        pw_loop* loop,
        delegate* unmanaged[Cdecl]<spa_loop*, bool, uint, void*, nuint, void*, int> func,
        void* userData
    )
    {
        if (loop is null || loop->loop is null)
            return -NativeLibc.EOPNOTSUPP;
        GetInterface(loop->loop, out spa_loop_methods* m, out void* data);
        if (m is null || m->locked is null)
            return -NativeLibc.EOPNOTSUPP;

        return m->locked(data, func, NativeConstants.SPA_ID_INVALID, null, 0, userData);
    }

    /// <summary>True when a result is a queued request rather than a completed one.</summary>
    /// <remarks>
    /// SPA encodes "request accepted, answer comes later" in the return value rather than in a
    /// separate channel: <c>SPA_ASYNC_BIT</c> set means the low bits are the request's sequence
    /// number. This is why testing a method result for <c>0</c> or for <c>&lt; 0</c> proves nothing
    /// about an asynchronous request - a queued call returns neither. The outcome arrives on the
    /// core's <c>done</c> or <c>error</c> event carrying the same sequence number.
    /// Hand-written because upstream spells it as a function-like macro, which cannot be generated;
    /// the constants it tests are the generated ones.
    /// </remarks>
    internal static bool SPA_RESULT_IS_ASYNC(int result) =>
        (result & NativeConstants.SPA_ASYNC_MASK) == NativeConstants.SPA_ASYNC_BIT;

    /// <summary>The sequence number carried by an async result.</summary>
    internal static int SPA_RESULT_ASYNC_SEQ(int result) =>
        result & NativeConstants.SPA_ASYNC_SEQ_MASK;

    /// <summary>
    /// Calls <c>pw_core_methods.get_registry</c> via SPA interface dispatch.
    /// Equivalent to the C macro <c>pw_core_get_registry()</c>.
    /// </summary>
    internal static pw_registry* pw_core_get_registry(
        pw_core* core,
        uint version,
        nuint userDataSize
    )
    {
        GetInterface(core, out pw_core_methods* methods, out void* data);
        if (methods is null || methods->get_registry is null)
            throw new PipeWireInteropException("pw_core_get_registry", -NativeLibc.ENOSYS);
        return methods->get_registry(data, version, userDataSize);
    }

    /// <summary>
    /// Calls <c>pw_registry_methods.add_listener</c> via SPA interface dispatch.
    /// Equivalent to the C macro <c>pw_registry_add_listener()</c>.
    /// </summary>
    internal static int pw_registry_add_listener(
        pw_registry* registry,
        spa_hook* listener,
        pw_registry_events* events,
        void* data
    )
    {
        GetInterface(registry, out pw_registry_methods* methods, out void* userData);
        if (methods is null || methods->add_listener is null)
            return -1;
        return methods->add_listener(userData, listener, events, data);
    }

    /// <summary>
    /// Calls <c>pw_core_methods.create_object</c> via SPA interface dispatch.
    /// Equivalent to the C macro <c>pw_core_create_object()</c>.
    /// </summary>
    /// <returns>The new object's proxy, or <see langword="null"/> if the daemon refused it.</returns>
    internal static pw_proxy* pw_core_create_object(
        pw_core* core,
        sbyte* factoryName,
        sbyte* type,
        uint version,
        spa_dict* props,
        nuint userDataSize
    )
    {
        GetInterface(core, out pw_core_methods* methods, out void* data);
        if (methods is null || methods->create_object is null)
            throw new PipeWireInteropException("pw_core_create_object", -NativeLibc.ENOSYS);
        return (pw_proxy*)
            methods->create_object(data, factoryName, type, version, props, userDataSize);
    }

    /// <summary>
    /// Calls <c>pw_registry_methods.destroy</c> via SPA interface dispatch, asking the daemon to
    /// destroy a global by id. Use this for objects this client does not hold a proxy for; destroy
    /// objects we created with <see cref="pw_proxy_destroy"/> instead.
    /// </summary>
    /// <returns>0 on success, or a negative errno.</returns>
    internal static int pw_registry_destroy_global(pw_registry* registry, uint id)
    {
        GetInterface(registry, out pw_registry_methods* methods, out void* data);
        if (methods is null || methods->destroy is null)
            return -1;
        return methods->destroy(data, id);
    }

    /// <summary>
    /// Calls <c>pw_core_methods.sync</c> via SPA interface dispatch. The daemon answers with a
    /// <c>done</c> event carrying the same sequence number once it has processed everything
    /// requested before this point.
    /// </summary>
    internal static int pw_core_sync(pw_core* core, uint id, int seq)
    {
        GetInterface(core, out pw_core_methods* methods, out void* data);
        if (methods is null || methods->sync is null)
            return -1;
        return methods->sync(data, id, seq);
    }

    /// <summary>
    /// Calls <c>pw_core_methods.add_listener</c> via SPA interface dispatch.
    /// Equivalent to the C macro <c>pw_core_add_listener()</c>.
    /// </summary>
    internal static int pw_core_add_listener(
        pw_core* core,
        spa_hook* listener,
        pw_core_events* events,
        void* data
    )
    {
        GetInterface(core, out pw_core_methods* methods, out void* userData);
        if (methods is null || methods->add_listener is null)
            return -1;
        return methods->add_listener(userData, listener, events, data);
    }

    /// <summary>
    /// Calls <c>pw_registry_methods.bind</c> via SPA interface dispatch, asking the daemon for a
    /// proxy to an existing global so its own interface can be used.
    /// </summary>
    /// <remarks>
    /// The registry reports that an object exists and what its properties are; binding is what makes
    /// it addressable - enumerating a node's parameters, or writing a metadata entry, needs a proxy
    /// to that object rather than to the registry. The proxy is owned by the caller and must be
    /// destroyed exactly once.
    /// </remarks>
    /// <returns>The proxy, or <see langword="null"/> if the daemon refused.</returns>
    internal static pw_proxy* pw_registry_bind(
        pw_registry* registry,
        uint id,
        sbyte* type,
        uint version,
        nuint userDataSize
    )
    {
        GetInterface(registry, out pw_registry_methods* methods, out void* data);
        if (methods is null || methods->bind is null)
            return null;
        return (pw_proxy*)methods->bind(data, id, type, version, userDataSize);
    }

    // - Module -

    internal static int pw_module_add_listener(
        pw_module* module,
        spa_hook* listener,
        pw_module_events* events,
        void* data
    )
    {
        GetInterface(module, out pw_module_methods* methods, out void* userData);
        if (methods is null || methods->add_listener is null)
            return -1;
        return methods->add_listener(userData, listener, events, data);
    }

    // - Node -

    internal static int pw_node_add_listener(
        pw_node* node,
        spa_hook* listener,
        pw_node_events* events,
        void* data
    )
    {
        GetInterface(node, out pw_node_methods* methods, out void* userData);
        if (methods is null || methods->add_listener is null)
            return -1;
        return methods->add_listener(userData, listener, events, data);
    }

    /// <summary>
    /// Asks for a range of one parameter's values. The answers arrive on the <c>param</c> event,
    /// each carrying the sequence number given here.
    /// </summary>
    /// <remarks>
    /// There is no "that was the last one" event. The end of the answers is found by round-tripping
    /// the core afterwards: events are ordered, so the sync's <c>done</c> cannot arrive before every
    /// <c>param</c> the request produced.
    /// </remarks>
    internal static int pw_node_enum_params(
        pw_node* node,
        int seq,
        uint id,
        uint start,
        uint num,
        spa_pod* filter
    )
    {
        GetInterface(node, out pw_node_methods* methods, out void* data);
        if (methods is null || methods->enum_params is null)
            return -1;
        return methods->enum_params(data, seq, id, start, num, filter);
    }

    internal static int pw_node_set_param(pw_node* node, uint id, uint flags, spa_pod* param)
    {
        GetInterface(node, out pw_node_methods* methods, out void* data);
        if (methods is null || methods->set_param is null)
            return -1;
        return methods->set_param(data, id, flags, param);
    }

    /// <summary>
    /// Asks the daemon to push a <c>param</c> event whenever one of these parameters changes,
    /// instead of only when asked.
    /// </summary>
    internal static int pw_node_subscribe_params(pw_node* node, uint* ids, uint nIds)
    {
        GetInterface(node, out pw_node_methods* methods, out void* data);
        if (methods is null || methods->subscribe_params is null)
            return -1;
        return methods->subscribe_params(data, ids, nIds);
    }

    // - Device -

    internal static int pw_device_add_listener(
        pw_device* device,
        spa_hook* listener,
        pw_device_events* events,
        void* data
    )
    {
        GetInterface(device, out pw_device_methods* methods, out void* userData);
        if (methods is null || methods->add_listener is null)
            return -1;
        return methods->add_listener(userData, listener, events, data);
    }

    /// <inheritdoc cref="pw_node_enum_params"/>
    internal static int pw_device_enum_params(
        pw_device* device,
        int seq,
        uint id,
        uint start,
        uint num,
        spa_pod* filter
    )
    {
        GetInterface(device, out pw_device_methods* methods, out void* data);
        if (methods is null || methods->enum_params is null)
            return -1;
        return methods->enum_params(data, seq, id, start, num, filter);
    }

    internal static int pw_device_set_param(pw_device* device, uint id, uint flags, spa_pod* param)
    {
        GetInterface(device, out pw_device_methods* methods, out void* data);
        if (methods is null || methods->set_param is null)
            return -1;
        return methods->set_param(data, id, flags, param);
    }

    /// <inheritdoc cref="pw_node_subscribe_params"/>
    internal static int pw_device_subscribe_params(pw_device* device, uint* ids, uint nIds)
    {
        GetInterface(device, out pw_device_methods* methods, out void* data);
        if (methods is null || methods->subscribe_params is null)
            return -1;
        return methods->subscribe_params(data, ids, nIds);
    }

    // - Logging -

    /// <summary>
    /// Sets how much PipeWire's own library logging says.
    /// </summary>
    /// <remarks>
    /// Hand-declared rather than generated: the log headers are not traversed, and adding them to
    /// pull in one exported function with a trivial signature would drag the whole spa_log surface
    /// into the committed bindings.
    /// </remarks>
    [System.Runtime.InteropServices.LibraryImport("libpipewire-0.3")]
    [System.Runtime.InteropServices.UnmanagedCallConv(
        CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)]
    )]
    internal static partial void pw_log_set_level(int level);

    // - Port -

    /// <summary>Attaches a listener to a port proxy.</summary>
    internal static int pw_port_add_listener(
        pw_port* port,
        spa_hook* listener,
        pw_port_events* events,
        void* data
    )
    {
        GetInterface(port, out pw_port_methods* methods, out void* userData);
        if (methods is null || methods->add_listener is null)
            return -1;
        return methods->add_listener(userData, listener, events, data);
    }

    /// <summary>Asks a port for a parameter. The answers arrive on the param event.</summary>
    internal static int pw_port_enum_params(
        pw_port* port,
        int seq,
        uint id,
        uint start,
        uint num,
        spa_pod* filter
    )
    {
        GetInterface(port, out pw_port_methods* methods, out void* userData);
        if (methods is null || methods->enum_params is null)
            return -1;
        return methods->enum_params(userData, seq, id, start, num, filter);
    }

    /// <summary>Asks a port to report the named parameters whenever they change.</summary>
    internal static int pw_port_subscribe_params(pw_port* port, uint* ids, uint count)
    {
        GetInterface(port, out pw_port_methods* methods, out void* userData);
        if (methods is null || methods->subscribe_params is null)
            return -1;
        return methods->subscribe_params(userData, ids, count);
    }

    // - Link -

    /// <summary>
    /// Attaches a listener to a link proxy.
    /// </summary>
    /// <remarks>
    /// The header declares this inline over the interface vtable rather than exporting it, so it is
    /// dispatched here the same way the node, device and client listeners are.
    /// </remarks>
    internal static int pw_link_add_listener(
        pw_link* link,
        spa_hook* listener,
        pw_link_events* events,
        void* data
    )
    {
        GetInterface(link, out pw_link_methods* methods, out void* userData);
        if (methods is null || methods->add_listener is null)
            return -1;
        return methods->add_listener(userData, listener, events, data);
    }

    // - Client -

    internal static int pw_client_add_listener(
        pw_client* client,
        spa_hook* listener,
        pw_client_events* events,
        void* data
    )
    {
        GetInterface(client, out pw_client_methods* methods, out void* userData);
        if (methods is null || methods->add_listener is null)
            return -1;
        return methods->add_listener(userData, listener, events, data);
    }

    /// <summary>
    /// Replaces what a client is permitted to do with the objects named in
    /// <paramref name="permissions"/>.
    /// </summary>
    /// <remarks>
    /// Only a client with the manager permission may do this - normally a session manager, not an
    /// ordinary application. Permissions are absolute, not a delta: an object listed with fewer
    /// bits than it had loses the difference.
    /// </remarks>
    internal static int pw_client_update_permissions(
        pw_client* client,
        uint nPermissions,
        pw_permission* permissions
    )
    {
        GetInterface(client, out pw_client_methods* methods, out void* data);
        if (methods is null || methods->update_permissions is null)
            return -1;
        return methods->update_permissions(data, nPermissions, permissions);
    }

    /// <summary>Asks for a range of a client's permissions, answered on the <c>permissions</c> event.</summary>
    internal static int pw_client_get_permissions(pw_client* client, uint index, uint num)
    {
        GetInterface(client, out pw_client_methods* methods, out void* data);
        if (methods is null || methods->get_permissions is null)
            return -1;
        return methods->get_permissions(data, index, num);
    }

    internal static int pw_client_update_properties(pw_client* client, spa_dict* props)
    {
        GetInterface(client, out pw_client_methods* methods, out void* data);
        if (methods is null || methods->update_properties is null)
            return -1;
        return methods->update_properties(data, props);
    }

    // - Metadata -

    /// <summary>Attaches a listener to a profiler, whose only event is the profiling pod.</summary>
    internal static int pw_profiler_add_listener(
        void* profiler,
        spa_hook* listener,
        pw_profiler_events* events,
        void* data
    )
    {
        GetInterface(profiler, out pw_profiler_methods* methods, out void* userData);
        if (methods is null || methods->add_listener is null)
            return -1;
        return methods->add_listener(userData, listener, events, data);
    }

    /// <summary>
    /// Creates a sandboxed connection point on a security context.
    /// </summary>
    /// <remarks>
    /// The two descriptors are the point of the interface: <paramref name="listenFd"/> is a listening
    /// socket the daemon accepts sandboxed clients on, and <paramref name="closeFd"/> is what the
    /// daemon watches to know the sandbox is gone. Anything connecting through that socket gets the
    /// permissions described by the properties, not the creator's.
    /// </remarks>
    internal static int pw_security_context_create(
        void* context,
        int listenFd,
        int closeFd,
        spa_dict* props
    )
    {
        GetInterface(context, out pw_security_context_methods* methods, out void* userData);
        if (methods is null || methods->create is null)
            return -1;
        return methods->create(userData, listenFd, closeFd, props);
    }

    internal static int pw_metadata_add_listener(
        pw_metadata* metadata,
        spa_hook* listener,
        pw_metadata_events* events,
        void* data
    )
    {
        GetInterface(metadata, out pw_metadata_methods* methods, out void* userData);
        if (methods is null || methods->add_listener is null)
            return -1;
        return methods->add_listener(userData, listener, events, data);
    }

    /// <summary>
    /// Sets, or with a null value removes, one entry in a metadata store.
    /// </summary>
    /// <remarks>
    /// Entries are strings, not pods - which is why the metadata interface needs none of the POD
    /// machinery the parameter interfaces do. The subject is the id the entry is about, and
    /// <see cref="NativeConstants.PW_ID_CORE"/> is the subject for daemon-wide settings such as the default sink.
    /// </remarks>
    internal static int pw_metadata_set_property(
        pw_metadata* metadata,
        uint subject,
        sbyte* key,
        sbyte* type,
        sbyte* value
    )
    {
        GetInterface(metadata, out pw_metadata_methods* methods, out void* data);
        if (methods is null || methods->set_property is null)
            return -1;
        return methods->set_property(data, subject, key, type, value);
    }

    /// <summary>Removes every entry in a metadata store.</summary>
    internal static int pw_metadata_clear(pw_metadata* metadata)
    {
        GetInterface(metadata, out pw_metadata_methods* methods, out void* data);
        if (methods is null || methods->clear is null)
            return -1;
        return methods->clear(data);
    }

    /// <summary>
    /// Puts the stream into the error state and tells the daemon why.
    /// </summary>
    /// <remarks>
    /// Not generated: the C function is variadic (<c>const char *error, ...</c>) and the binding
    /// generator skips those. Declared here with the message as the format string and no varargs,
    /// which is why callers must escape any <c>%</c> in it - see the wrapper below.
    /// <para>
    /// This is what the reference consumers do when they cannot satisfy a negotiation: gstreamer's
    /// pipewiresrc calls it with <c>-EINVAL</c> for an unhandled format and <c>-EPIPE</c> when it
    /// has no formats in common with the peer. Without it a stream that cannot proceed simply goes
    /// quiet, and the peer waits for a negotiation that will never finish.
    /// </para>
    /// </remarks>
    [DllImport(
        "libpipewire-0.3",
        EntryPoint = "pw_stream_set_error",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true
    )]
    internal static extern unsafe int pw_stream_set_error_raw(
        pw_stream* stream,
        int res,
        sbyte* error
    );

    /// <summary>The version string of the libpipewire this process actually loaded.</summary>
    /// <remarks>
    /// Not generated: the declaration is in a header the generator does not read. It matters
    /// because the bindings are produced against one release and the library resolved at runtime is
    /// whatever the machine has, and the two disagreeing is not always a clean failure.
    /// </remarks>
    [DllImport(
        "libpipewire-0.3",
        EntryPoint = "pw_get_library_version",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true
    )]
    internal static extern unsafe sbyte* pw_get_library_version();

    /// <summary>
    /// Puts the filter into the error state and tells the daemon why.
    /// </summary>
    /// <remarks>
    /// Variadic in C for the same reason as the stream version, and skipped by the generator for
    /// the same reason. A filter that cannot proceed and says nothing leaves its peers waiting on
    /// a graph cycle that will not come.
    /// </remarks>
    [DllImport(
        "libpipewire-0.3",
        EntryPoint = "pw_filter_set_error",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true
    )]
    internal static extern unsafe int pw_filter_set_error_raw(
        pw_filter* filter,
        int res,
        sbyte* error
    );

    /// <summary>Reports a filter error, with the message escaped so it cannot be read as a format.</summary>
    internal static unsafe int pw_filter_set_error(pw_filter* filter, int res, string message)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(message.Replace("%", "%%") + '\0');
        fixed (byte* p = utf8)
            return pw_filter_set_error_raw(filter, res, (sbyte*)p);
    }

    /// <summary>Reports a stream error, with the message escaped so it cannot be read as a format.</summary>
    internal static unsafe int pw_stream_set_error(pw_stream* stream, int res, string message)
    {
        // The message goes in as the format string, so a stray % would make the callee read
        // arguments that were never passed. The trailing NUL is explicit: GetBytes does not add
        // one, and the callee would otherwise read past the end of the array.
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(message.Replace("%", "%%") + '\0');
        fixed (byte* p = utf8)
            return pw_stream_set_error_raw(stream, res, (sbyte*)p);
    }

    /// <summary>
    /// Detaches a listener, reimplementing <c>spa_hook_remove</c>. That is a static inline in
    /// spa/utils/hook.h and exports no symbol, so it cannot be called through P/Invoke.
    /// </summary>
    /// <remarks>
    /// Must run under the thread-loop lock: it edits a list the loop thread walks while dispatching.
    /// The caller still owns the hook's memory - PipeWire never allocated it and will not free it.
    /// </remarks>
    internal static void spa_hook_remove(spa_hook* hook)
    {
        if (hook is null)
            return;

        // spa_list_is_initialized: a hook that was never attached has a null prev. Both ends are
        // checked because a half-unlinked hook would otherwise be dereferenced through a null next.
        if (hook->link.prev is not null && hook->link.next is not null)
        {
            hook->link.prev->next = hook->link.next;
            hook->link.next->prev = hook->link.prev;
            hook->link.next = null;
            hook->link.prev = null;
        }

        if (hook->removed is not null)
            hook->removed(hook);
    }

    /// <summary>Adds a timer source to a loop, returning null when the loop cannot be dispatched.</summary>
    internal static unsafe spa_source* spa_loop_utils_add_timer(
        spa_loop_utils* utils,
        delegate* unmanaged[Cdecl]<void*, ulong, void> func,
        void* data
    )
    {
        spa_loop_utils_methods* m = LoopUtilsMethods(utils);
        if (m is null || m->add_timer is null)
            return null;
        return m->add_timer(utils->iface.cb.data, func, data);
    }

    /// <summary>Arms or disarms a timer source.</summary>
    /// <returns>0 on success, a negative errno otherwise.</returns>
    internal static unsafe int spa_loop_utils_update_timer(
        spa_loop_utils* utils,
        spa_source* source,
        PosixTimespec* value,
        PosixTimespec* interval,
        bool absolute
    )
    {
        spa_loop_utils_methods* m = LoopUtilsMethods(utils);
        if (m is null || m->update_timer is null)
            return -NativeLibc.EOPNOTSUPP;
        return m->update_timer(utils->iface.cb.data, source, value, interval, absolute);
    }

    /// <summary>Destroys a source previously added to a loop.</summary>
    internal static unsafe void spa_loop_utils_destroy_source(
        spa_loop_utils* utils,
        spa_source* source
    )
    {
        spa_loop_utils_methods* m = LoopUtilsMethods(utils);
        if (m is null || m->destroy_source is null)
            return;
        m->destroy_source(utils->iface.cb.data, source);
    }

    /// <summary>
    /// The dispatch table, or null when it is missing or announces a layout this does not know.
    /// </summary>
    /// <remarks>
    /// The table's layout is generated from spa/support/loop.h, which is only correct for the
    /// version it was generated against. A future PipeWire that inserts a member would leave every
    /// later slot naming a different function - a jump through a wrong pointer rather than anything
    /// that fails cleanly - so an unknown version is refused, which turns that into a no-op.
    /// </remarks>
    private static unsafe spa_loop_utils_methods* LoopUtilsMethods(spa_loop_utils* utils)
    {
        if (utils is null)
            return null;

        var m = (spa_loop_utils_methods*)utils->iface.cb.funcs;
        if (m is null || m->version != NativeConstants.SPA_VERSION_LOOP_UTILS_METHODS)
            return null;

        return m;
    }
}
