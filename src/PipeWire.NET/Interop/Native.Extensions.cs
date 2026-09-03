// Hand-maintained extension of the generated `Native` partial class.
//
// Why this file lives outside generated/: generate/generate.sh wipes the
// generated/ directory on each regeneration. Anything we hand-write must live
// elsewhere. We keep the same `PipeWire.NET.Generated` namespace and the same
// `static partial class Native` so consumers see these symbols seamlessly
// alongside generated declarations (e.g. `Native.PW_VERSION_STREAM_EVENTS`).
//
// Contents:
//   - PW_VERSION_* and PW_ID_* macro constants. ClangSharp 21 cannot translate
//     function-like macros + CompoundLiteralExpr macros like PW_MAP_RANGE_INIT
//     prevent generate-macro-bindings from running at all.
//   - SPA interface VTBL dispatch helpers (pw_core_get_registry,
//     pw_registry_add_listener) - these are C macros, not exported symbols.
//
// Verify against /usr/include/pipewire-0.3/pipewire/*.h when bumping PipeWire.

#pragma warning disable CS1591 // Missing XML comment - matches the suppression in generated/*.g.cs
#pragma warning disable CA1707 // Identifiers should not contain underscores (matches generated style)
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix

namespace PipeWire.NET.Interop;

public static unsafe partial class Native
{
    // - Property keys (pipewire/keys.h) -
    // These are #define string macros; ClangSharp can't emit them without
    // generate-macro-bindings (fatal on this header set - see pipewire.rsp).
    // Hand-declared here. Most are stable across 0.3.x and 1.x; any that are not
    // carry a note naming the version that introduced them.

    /// <summary><c>factory.name</c> - name of a factory to use for node creation.</summary>
    public const string PW_KEY_FACTORY_NAME   = "factory.name";
    /// <summary><c>media.class</c> - node media class (e.g. "Video/Source").</summary>
    public const string PW_KEY_MEDIA_CLASS    = "media.class";
    /// <summary><c>media.type</c> - "Video" / "Audio".</summary>
    public const string PW_KEY_MEDIA_TYPE     = "media.type";
    /// <summary><c>media.category</c> - "Capture" / "Playback" / "Duplex".</summary>
    public const string PW_KEY_MEDIA_CATEGORY = "media.category";
    /// <summary><c>media.role</c> - "Camera" / "Music" / "Screen" / ...</summary>
    public const string PW_KEY_MEDIA_ROLE     = "media.role";
    /// <summary><c>node.id</c> - node identifier.</summary>
    public const string PW_KEY_NODE_ID        = "node.id";
    /// <summary><c>node.name</c> - stable node name.</summary>
    public const string PW_KEY_NODE_NAME      = "node.name";
    /// <summary><c>node.description</c> - human-readable node name.</summary>
    public const string PW_KEY_NODE_DESCRIPTION = "node.description";
    /// <summary><c>node.nick</c> - short display name.</summary>
    public const string PW_KEY_NODE_NICK      = "node.nick";
    /// <summary><c>port.name</c> - stable port name.</summary>
    public const string PW_KEY_PORT_NAME      = "port.name";
    /// <summary><c>port.direction</c> - port direction.</summary>
    public const string PW_KEY_PORT_DIRECTION = "port.direction";
    /// <summary><c>port.monitor</c> - if this is a monitor port.</summary>
    public const string PW_KEY_PORT_MONITOR   = "port.monitor";
    /// <summary><c>port.exclusive</c> - link this port only once. Since PipeWire 1.6.0.</summary>
    public const string PW_KEY_PORT_EXCLUSIVE = "port.exclusive";
    /// <summary><c>link.input.node</c> - the node a link feeds into.</summary>
    public const string PW_KEY_LINK_INPUT_NODE  = "link.input.node";
    /// <summary><c>link.input.port</c> - the port a link feeds into.</summary>
    public const string PW_KEY_LINK_INPUT_PORT  = "link.input.port";
    /// <summary><c>link.output.node</c> - the node a link starts from.</summary>
    public const string PW_KEY_LINK_OUTPUT_NODE = "link.output.node";
    /// <summary><c>link.output.port</c> - the port a link starts from.</summary>
    public const string PW_KEY_LINK_OUTPUT_PORT = "link.output.port";
    /// <summary><c>object.linger</c> - keep the object alive after this client disconnects.</summary>
    public const string PW_KEY_OBJECT_LINGER    = "object.linger";
    /// <summary><c>link.passive</c> - the link does not keep its endpoints active when idle.</summary>
    public const string PW_KEY_LINK_PASSIVE     = "link.passive";
    /// <summary><c>target.object</c> - bind a stream to a specific node by serial/name.</summary>
    public const string PW_KEY_TARGET_OBJECT  = "target.object";

    // - SPA property keys (spa/param/audio/raw.h, spa/support/plugin.h) -
    // Distinct namespace from PW_KEY_*: these have no pipewire/keys.h equivalent.

    /// <summary><c>audio.position</c> - channel layout, e.g. "[ FL FR ]".</summary>
    public const string SPA_KEY_AUDIO_POSITION = "audio.position";

    // - Interface type ids -

    public const string PW_TYPE_INFO_INTERFACE_BASE = "PipeWire:Interface:";
    public const string PW_TYPE_INTERFACE_NODE = PW_TYPE_INFO_INTERFACE_BASE + "Node";
    public const string PW_TYPE_INTERFACE_PORT = PW_TYPE_INFO_INTERFACE_BASE + "Port";
    public const string PW_TYPE_INTERFACE_LINK = PW_TYPE_INFO_INTERFACE_BASE + "Link";
    public const string PW_TYPE_INTERFACE_DEVICE = PW_TYPE_INFO_INTERFACE_BASE + "Device";
    public const string PW_TYPE_INTERFACE_CLIENT = PW_TYPE_INFO_INTERFACE_BASE + "Client";
    public const string PW_TYPE_INTERFACE_FACTORY = PW_TYPE_INFO_INTERFACE_BASE + "Factory";
    public const string PW_TYPE_INTERFACE_MODULE = PW_TYPE_INFO_INTERFACE_BASE + "Module";
    public const string PW_TYPE_INTERFACE_METADATA = PW_TYPE_INFO_INTERFACE_BASE + "Metadata";
    public const string PW_TYPE_INTERFACE_PROFILER = PW_TYPE_INFO_INTERFACE_BASE + "Profiler";
    public const string PW_TYPE_INTERFACE_SECURITY_CONTEXT = PW_TYPE_INFO_INTERFACE_BASE + "SecurityContext";

    // - Sentinel ids -

    /// <summary>Wildcard node id - passed to pw_stream_connect to let the daemon auto-select.</summary>
    public const uint PW_ID_ANY  = 0xFFFFFFFFu;

    /// <summary>The well-known id of the PipeWire core object.</summary>
    public const uint PW_ID_CORE = 0u;

    /// <summary>Placeholder ID for when a proxy id could not be fetched.</summary>
    public const uint SPA_ID_INVALID = 0xffffffffu;

    // - Interface versions (struct pw_*_methods.version / pw_*_events.version) -

    public const uint PW_VERSION_CLIENT          = 3;
    public const uint PW_VERSION_CLIENT_EVENTS   = 0;
    public const uint PW_VERSION_CLIENT_METHODS  = 0;
    public const uint PW_VERSION_CONTEXT_EVENTS  = 1;
    public const uint PW_VERSION_CONTROL_EVENTS  = 0;
    public const uint PW_VERSION_CORE            = 4;
    public const uint PW_VERSION_CORE_EVENTS     = 1;
    public const uint PW_VERSION_CORE_METHODS    = 0;
    public const uint PW_VERSION_REGISTRY         = 3;
    public const uint PW_VERSION_REGISTRY_EVENTS  = 0;
    public const uint PW_VERSION_REGISTRY_METHODS = 0;
    public const uint PW_VERSION_DEVICE           = 3;
    public const uint PW_VERSION_DEVICE_EVENTS    = 0;
    public const uint PW_VERSION_FACTORY          = 3;
    public const uint PW_VERSION_FACTORY_EVENTS   = 0;
    public const uint PW_VERSION_GLOBAL_EVENTS    = 0;
    public const uint PW_VERSION_LINK             = 3;
    public const uint PW_VERSION_LINK_EVENTS      = 0;
    public const uint PW_VERSION_MODULE           = 3;
    public const uint PW_VERSION_NODE             = 3;
    public const uint PW_VERSION_PORT             = 3;
    public const uint PW_VERSION_PORT_EVENTS      = 0;
    public const uint PW_VERSION_PROXY_EVENTS     = 1;
    public const uint PW_VERSION_STREAM_EVENTS    = 2;
    public const uint PW_VERSION_FILTER_EVENTS    = 1;
    public const uint PW_VERSION_NODE_EVENTS      = 0;
    public const uint PW_VERSION_NODE_METHODS     = 0;
    public const uint PW_VERSION_DEVICE_METHODS   = 0;
    public const uint PW_VERSION_CLIENT_METHODS2  = 0;
    public const uint PW_VERSION_METADATA         = 3;
    public const uint PW_VERSION_IMPL_METADATA_EVENTS = 0;
    public const uint PW_VERSION_PROFILER         = 3;
    public const uint PW_VERSION_PROFILER_EVENTS  = 0;
    public const uint PW_VERSION_SECURITY_CONTEXT = 3;
    public const uint PW_VERSION_METADATA_EVENTS  = 0;
    public const uint PW_VERSION_METADATA_METHODS = 0;
    public const uint PW_VERSION_DATA_LOOP_EVENTS = 0;
    public const uint PW_VERSION_MAIN_LOOP_EVENTS = 0;

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
    public static void GetInterface<TMethods>(
        void* obj,
        out TMethods* methods,
        out void* userData) where TMethods : unmanaged
    {
        ArgumentNullException.ThrowIfNull(obj);
        var iface = (spa_interface*)obj;
        methods  = (TMethods*)iface->cb.funcs;
        userData = iface->cb.data;
    }

    /// <summary>
    /// SPA encodes "request accepted, answer comes later" in the return value rather than in a
    /// separate channel: bit 30 set means the low 30 bits are the request's sequence number.
    /// </summary>
    /// <remarks>
    /// This is why testing a method result for <c>0</c> or for <c>&lt; 0</c> proves nothing about an
    /// asynchronous request - a queued call returns neither. The outcome arrives on the core's
    /// <c>done</c> or <c>error</c> event carrying the same sequence number.
    /// </remarks>
    public const int SPA_ASYNC_BIT = 1 << 30;

    /// <summary>Mask selecting the sequence number out of an async result.</summary>
    public const int SPA_ASYNC_SEQ_MASK = SPA_ASYNC_BIT - 1;

    /// <summary>True when a result is a queued request rather than a completed one.</summary>
    public static bool SPA_RESULT_IS_ASYNC(int result) =>
        (result & ~SPA_ASYNC_SEQ_MASK) == SPA_ASYNC_BIT;

    /// <summary>The sequence number carried by an async result.</summary>
    public static int SPA_RESULT_ASYNC_SEQ(int result) => result & SPA_ASYNC_SEQ_MASK;

    /// <summary>
    /// Calls <c>pw_core_methods.get_registry</c> via SPA interface dispatch.
    /// Equivalent to the C macro <c>pw_core_get_registry()</c>.
    /// </summary>
    public static pw_registry* pw_core_get_registry(pw_core* core, uint version, nuint userDataSize)
    {
        GetInterface(core, out pw_core_methods* methods, out void* data);
        if (methods is null || methods->get_registry is null)
            throw new PipeWireException("pw_core_get_registry", -38);   // ENOSYS
        return methods->get_registry(data, version, userDataSize);
    }

    /// <summary>
    /// Calls <c>pw_registry_methods.add_listener</c> via SPA interface dispatch.
    /// Equivalent to the C macro <c>pw_registry_add_listener()</c>.
    /// </summary>
    public static int pw_registry_add_listener(
        pw_registry* registry,
        spa_hook* listener,
        pw_registry_events* events,
        void* data)
    {
        GetInterface(registry, out pw_registry_methods* methods, out void* userData);
        if (methods is null || methods->add_listener is null)
            return -1;
        return methods->add_listener(userData, listener, events, data);
    }

    /// <summary>
    /// Calls <c>pw_proxy_methods.destroy</c> equivalent - pw_proxy_destroy IS exported,
    /// so this just forwards. Kept here for symmetry with the dispatch helpers above.
    /// </summary>
    public static void pw_registry_destroy(pw_registry* registry) =>
        pw_proxy_destroy((pw_proxy*)registry);

    /// <summary>
    /// Calls <c>pw_core_methods.create_object</c> via SPA interface dispatch.
    /// Equivalent to the C macro <c>pw_core_create_object()</c>.
    /// </summary>
    /// <returns>The new object's proxy, or <see langword="null"/> if the daemon refused it.</returns>
    public static pw_proxy* pw_core_create_object(
        pw_core* core,
        sbyte* factoryName,
        sbyte* type,
        uint version,
        spa_dict* props,
        nuint userDataSize)
    {
        GetInterface(core, out pw_core_methods* methods, out void* data);
        if (methods is null || methods->create_object is null)
            throw new PipeWireException("pw_core_create_object", -38);  // ENOSYS
        return (pw_proxy*)methods->create_object(data, factoryName, type, version, props, userDataSize);
    }

    /// <summary>
    /// Calls <c>pw_registry_methods.destroy</c> via SPA interface dispatch, asking the daemon to
    /// destroy a global by id. Use this for objects this client does not hold a proxy for; destroy
    /// objects we created with <see cref="pw_proxy_destroy"/> instead.
    /// </summary>
    /// <returns>0 on success, or a negative errno.</returns>
    public static int pw_registry_destroy_global(pw_registry* registry, uint id)
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
    public static int pw_core_sync(pw_core* core, uint id, int seq)
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
    public static int pw_core_add_listener(
        pw_core* core,
        spa_hook* listener,
        pw_core_events* events,
        void* data)
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
    public static pw_proxy* pw_registry_bind(
        pw_registry* registry, uint id, sbyte* type, uint version, nuint userDataSize)
    {
        GetInterface(registry, out pw_registry_methods* methods, out void* data);
        if (methods is null || methods->bind is null)
            return null;
        return (pw_proxy*)methods->bind(data, id, type, version, userDataSize);
    }

    // - Node -

    public static int pw_node_add_listener(
        pw_node* node, spa_hook* listener, pw_node_events* events, void* data)
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
    public static int pw_node_enum_params(
        pw_node* node, int seq, uint id, uint start, uint num, spa_pod* filter)
    {
        GetInterface(node, out pw_node_methods* methods, out void* data);
        if (methods is null || methods->enum_params is null)
            return -1;
        return methods->enum_params(data, seq, id, start, num, filter);
    }

    public static int pw_node_set_param(pw_node* node, uint id, uint flags, spa_pod* param)
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
    public static int pw_node_subscribe_params(pw_node* node, uint* ids, uint nIds)
    {
        GetInterface(node, out pw_node_methods* methods, out void* data);
        if (methods is null || methods->subscribe_params is null)
            return -1;
        return methods->subscribe_params(data, ids, nIds);
    }

    // - Device -

    public static int pw_device_add_listener(
        pw_device* device, spa_hook* listener, pw_device_events* events, void* data)
    {
        GetInterface(device, out pw_device_methods* methods, out void* userData);
        if (methods is null || methods->add_listener is null)
            return -1;
        return methods->add_listener(userData, listener, events, data);
    }

    /// <inheritdoc cref="pw_node_enum_params"/>
    public static int pw_device_enum_params(
        pw_device* device, int seq, uint id, uint start, uint num, spa_pod* filter)
    {
        GetInterface(device, out pw_device_methods* methods, out void* data);
        if (methods is null || methods->enum_params is null)
            return -1;
        return methods->enum_params(data, seq, id, start, num, filter);
    }

    public static int pw_device_set_param(pw_device* device, uint id, uint flags, spa_pod* param)
    {
        GetInterface(device, out pw_device_methods* methods, out void* data);
        if (methods is null || methods->set_param is null)
            return -1;
        return methods->set_param(data, id, flags, param);
    }

    /// <inheritdoc cref="pw_node_subscribe_params"/>
    public static int pw_device_subscribe_params(pw_device* device, uint* ids, uint nIds)
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
        CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial void pw_log_set_level(int level);

    // - Port -

    /// <summary>Attaches a listener to a port proxy.</summary>
    public static int pw_port_add_listener(
        pw_port* port, spa_hook* listener, pw_port_events* events, void* data)
    {
        GetInterface(port, out pw_port_methods* methods, out void* userData);
        if (methods is null || methods->add_listener is null)
            return -1;
        return methods->add_listener(userData, listener, events, data);
    }

    /// <summary>Asks a port for a parameter. The answers arrive on the param event.</summary>
    public static int pw_port_enum_params(
        pw_port* port, int seq, uint id, uint start, uint num, spa_pod* filter)
    {
        GetInterface(port, out pw_port_methods* methods, out void* userData);
        if (methods is null || methods->enum_params is null)
            return -1;
        return methods->enum_params(userData, seq, id, start, num, filter);
    }

    /// <summary>Asks a port to report the named parameters whenever they change.</summary>
    public static int pw_port_subscribe_params(pw_port* port, uint* ids, uint count)
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
    public static int pw_link_add_listener(
        pw_link* link, spa_hook* listener, pw_link_events* events, void* data)
    {
        GetInterface(link, out pw_link_methods* methods, out void* userData);
        if (methods is null || methods->add_listener is null)
            return -1;
        return methods->add_listener(userData, listener, events, data);
    }

    // - Client -

    public static int pw_client_add_listener(
        pw_client* client, spa_hook* listener, pw_client_events* events, void* data)
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
    public static int pw_client_update_permissions(
        pw_client* client, uint nPermissions, pw_permission* permissions)
    {
        GetInterface(client, out pw_client_methods* methods, out void* data);
        if (methods is null || methods->update_permissions is null)
            return -1;
        return methods->update_permissions(data, nPermissions, permissions);
    }

    /// <summary>Asks for a range of a client's permissions, answered on the <c>permissions</c> event.</summary>
    public static int pw_client_get_permissions(pw_client* client, uint index, uint num)
    {
        GetInterface(client, out pw_client_methods* methods, out void* data);
        if (methods is null || methods->get_permissions is null)
            return -1;
        return methods->get_permissions(data, index, num);
    }

    public static int pw_client_update_properties(pw_client* client, spa_dict* props)
    {
        GetInterface(client, out pw_client_methods* methods, out void* data);
        if (methods is null || methods->update_properties is null)
            return -1;
        return methods->update_properties(data, props);
    }

    // - Metadata -

    /// <summary>Attaches a listener to a profiler, whose only event is the profiling pod.</summary>
    public static int pw_profiler_add_listener(
        void* profiler, spa_hook* listener, pw_profiler_events* events, void* data)
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
    public static int pw_security_context_create(
        void* context, int listenFd, int closeFd, spa_dict* props)
    {
        GetInterface(context, out pw_security_context_methods* methods, out void* userData);
        if (methods is null || methods->create is null)
            return -1;
        return methods->create(userData, listenFd, closeFd, props);
    }

    public static int pw_metadata_add_listener(
        pw_metadata* metadata, spa_hook* listener, pw_metadata_events* events, void* data)
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
    /// <see cref="PW_ID_CORE"/> is the subject for daemon-wide settings such as the default sink.
    /// </remarks>
    public static int pw_metadata_set_property(
        pw_metadata* metadata, uint subject, sbyte* key, sbyte* type, sbyte* value)
    {
        GetInterface(metadata, out pw_metadata_methods* methods, out void* data);
        if (methods is null || methods->set_property is null)
            return -1;
        return methods->set_property(data, subject, key, type, value);
    }

    /// <summary>Removes every entry in a metadata store.</summary>
    public static int pw_metadata_clear(pw_metadata* metadata)
    {
        GetInterface(metadata, out pw_metadata_methods* methods, out void* data);
        if (methods is null || methods->clear is null)
            return -1;
        return methods->clear(data);
    }

    /// <summary>
    /// Detaches a listener, reimplementing <c>spa_hook_remove</c>. That is a static inline in
    /// spa/utils/hook.h and exports no symbol, so it cannot be called through P/Invoke.
    /// </summary>
    /// <remarks>
    /// Must run under the thread-loop lock: it edits a list the loop thread walks while dispatching.
    /// The caller still owns the hook's memory - PipeWire never allocated it and will not free it.
    /// </remarks>
    public static void spa_hook_remove(spa_hook* hook)
    {
        if (hook is null) return;

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
}
