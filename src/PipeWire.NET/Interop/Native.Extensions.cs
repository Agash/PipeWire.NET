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

namespace PipeWire.NET.Generated;

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
    public const uint PW_VERSION_MODULE           = 3;
    public const uint PW_VERSION_NODE             = 3;
    public const uint PW_VERSION_PORT             = 3;
    public const uint PW_VERSION_PROXY_EVENTS     = 1;
    public const uint PW_VERSION_STREAM_EVENTS    = 2;
    public const uint PW_VERSION_FILTER_EVENTS    = 1;
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
            throw new InvalidOperationException("pw_core has no get_registry method (interface VTBL is null).");
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
            throw new InvalidOperationException("pw_core has no create_object method (interface VTBL is null).");
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

        // spa_list_is_initialized: a hook that was never attached has a null prev.
        if (hook->link.prev is not null)
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
