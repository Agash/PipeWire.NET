using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PipeWire.NET.Interop;

namespace PipeWire.NET.Graph;

/// <summary>
/// Binds a module for long enough to read the properties its registry global does not carry.
/// </summary>
/// <remarks>
/// A module's global announces <c>module.name</c> and little else; its description, author, version,
/// filename and arguments only arrive on the <c>info</c> event of a bound proxy. This exists so the
/// registry can fetch those on request rather than binding every module of every session for data
/// most callers never read.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class PipeWireModuleReader : IDisposable
{
    private readonly TaskCompletionSource<PipeWireProperties> _info =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private BoundProxy? _bound;

    /// <summary>Completes with the module's full properties when the daemon sends them.</summary>
    internal Task<PipeWireProperties> Properties => _info.Task;

    internal static unsafe PipeWireModuleReader Bind(
        PipeWireContext ctx, pw_registry* registry, uint id, uint version)
    {
        var reader = new PipeWireModuleReader();
        reader._bound = BoundProxy.Bind(
            ctx, registry, id, Native.PW_TYPE_INTERFACE_MODULE, version, Native.PW_VERSION_MODULE,
            sizeof(pw_module_events),
            events =>
            {
                var table = (pw_module_events*)events;
                table->version = Native.PW_VERSION_MODULE_EVENTS;
                table->info = &OnInfoCallback;
            },
            static (proxy, hook, events, data) => Native.pw_module_add_listener(
                (pw_module*)proxy, (spa_hook*)hook, (pw_module_events*)events, (void*)data),
            reader);

        return reader;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnInfoCallback(void* data, pw_module_info* info)
    {
        // A native callback frame: an escaping exception aborts the process.
        try
        {
            if (data is null || info is null) return;
            if (GCHandle.FromIntPtr((nint)data).Target is not PipeWireModuleReader self) return;

            // filename and args are not properties, but they are the two facts a reader of a module
            // most often wants and they arrive nowhere else, so they travel as properties here.
            var extra = new Dictionary<string, string>(StringComparer.Ordinal);
            if (info->filename is not null) extra[PipeWireNames.ModuleFilename] = DaemonText.String(info->filename)!;
            if (info->args is not null) extra[PipeWireNames.ModuleArguments] = DaemonText.String(info->args)!;

            self._info.TrySetResult(
                PipeWireProperties.From(info->props).MergedWith(PipeWireProperties.FromItems(extra)));
        }
        catch (Exception)
        {
            // Deliberately not logged: this frame has no logger, and the caller's wait times out.
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _bound?.Dispose();
        _bound = null;
    }
}
