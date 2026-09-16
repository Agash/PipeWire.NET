namespace PipeWire.NET.Graph;

/// <summary>
/// The property keys this library defines itself, alongside the generated ones.
/// </summary>
/// <remarks>
/// <para>
/// Everything else on <see cref="PipeWireKeys"/> is derived from a <c>PW_KEY_*</c> or
/// <c>SPA_KEY_*</c> macro. These two are not: a module's filename and arguments reach a client as
/// fields of <c>struct pw_module_info</c>, not as dictionary entries, so no header declares a name
/// for them. <c>PipeWireModuleProxy</c> publishes them into the module's property bag under these
/// names so a caller reads them the same way as every other module property.
/// </para>
/// <para>
/// They live here rather than in the generated file because the generator emits only what a header
/// declares. A name invented by this library that appeared among the generated ones would read as
/// upstream's, and the next regeneration would silently drop it.
/// </para>
/// </remarks>
public static partial class PipeWireKeys
{
    /// <summary><c>module.filename</c> - the module's shared object path.</summary>
    public const string MODULE_FILENAME = "module.filename";

    /// <summary><c>module.args</c> - the arguments the module was loaded with.</summary>
    public const string MODULE_ARGS = "module.args";
}
