using System.Runtime.Versioning;
using System.Text;
using PipeWire.NET.Interop;

namespace PipeWire.NET.Graph;

/// <summary>
/// Loads an object out of a SPA plugin and hands it to the graph.
/// </summary>
/// <remarks>
/// <para>
/// What upstream's <c>export-spa</c> and <c>export-spa-device</c> do, and the mechanism behind
/// <c>bluez-session</c>: the object already exists inside a plugin, so instead of implementing
/// <c>spa_node</c> or <c>spa_device</c> this process loads the plugin's handle, fetches the
/// interface out of it and exports that.
/// </para>
/// <para>
/// Node and device differ only in which interface type is asked for, which is why the two
/// providers share this rather than each carrying a copy of the loading sequence.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal static unsafe class SpaFactoryExport
{
    /// <summary>Loads a factory and exports the interface it provides.</summary>
    /// <param name="ctx">A started context.</param>
    /// <param name="factoryName">The SPA factory, e.g. <c>audiotestsrc</c>.</param>
    /// <param name="interfaceType">
    /// The interface to fetch and export, e.g. <c>NativeConstants.SPA_TYPE_INTERFACE_Node</c>.
    /// </param>
    /// <param name="interfaceLabel">What to call that interface in an error, e.g. <c>node</c>.</param>
    /// <param name="properties">Properties for both the factory and the exported object.</param>
    /// <param name="libraryName">The SPA library to load from, or null to resolve by name.</param>
    /// <param name="handle">The loaded plugin handle, which the caller owns and must clear.</param>
    /// <returns>The export proxy, which the caller owns.</returns>
    /// <exception cref="InvalidOperationException">
    /// The context is not connected, the factory is not installed, it provides no such interface,
    /// or the export was refused.
    /// </exception>
    internal static pw_proxy* Load(
        PipeWireContext ctx,
        string factoryName,
        ReadOnlySpan<byte> interfaceType,
        string interfaceLabel,
        IReadOnlyDictionary<string, string>? properties,
        string? libraryName,
        out spa_handle* handle)
    {
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        if (properties is not null)
        {
            foreach (KeyValuePair<string, string> pair in properties) props[pair.Key] = pair.Value;
        }

        // SPA_KEY_LIBRARY_NAME. A client's context.spa-libs map is far smaller than the daemon's -
        // on a stock install it covers only audio.convert.*, support.* and video.convert.* - so a
        // factory like audiotestsrc or api.v4l2.enum.udev cannot be resolved by name from a client
        // even though the plugin is installed, and the failure reads as "not installed". Naming the
        // library outright skips the lookup, which is what upstream's export-spa does: it takes the
        // library as one argument and the factory as the next.
        if (!string.IsNullOrEmpty(libraryName)) props["library.name"] = libraryName;

        ReadOnlySpan<byte> factoryUtf8 = Encoding.UTF8.GetBytes(factoryName + '\0');

        pw_proxy* proxy;
        spa_handle* loaded;

        using (ctx.Lock())
        {
            pw_core* core = ctx.CoreHandle;
            if (core is null)
                throw new InvalidOperationException("the context is not connected.");

            Span<byte> scratch = stackalloc byte[1024];
            Span<spa_dict_item> items = stackalloc spa_dict_item[16];
            var dict = new SpaDictBuilder(scratch, items);
            foreach (KeyValuePair<string, string> pair in props) dict.Add(pair.Key, pair.Value);
            spa_dict built = dict.Build();

            fixed (byte* f = factoryUtf8)
                loaded = Native.pw_context_load_spa_handle(ctx.ContextHandle, (sbyte*)f, &built);

            if (loaded is null)
                throw new InvalidOperationException($"the SPA factory '{factoryName}' is not installed.");

            void* iface = null;
            int res;
            fixed (byte* type = interfaceType)
                res = loaded->get_interface(loaded, (sbyte*)type, &iface);

            if (res < 0 || iface is null)
            {
                if (loaded->clear is not null) _ = loaded->clear(loaded);
                throw new InvalidOperationException(
                    $"the SPA factory '{factoryName}' provides no {interfaceLabel} interface.");
            }

            fixed (byte* type = interfaceType)
                proxy = Native.pw_core_export(core, (sbyte*)type, &built, iface, 0);
        }

        if (proxy is null)
        {
            if (loaded->clear is not null) _ = loaded->clear(loaded);
            throw new InvalidOperationException(
                $"pw_core_export refused the {interfaceLabel} from factory '{factoryName}'.");
        }

        handle = loaded;
        return proxy;
    }
}
