namespace PipeWire.NET.Interop;

/// <summary>
/// The <c>spa_device</c> constants that are preprocessor defines, so nothing generates them.
/// </summary>
/// <remarks>
/// The structs and the method table are generated from <c>spa/monitor/device.h</c>. What is left
/// here is what ClangSharp cannot emit: the interface type string and the version and change-mask
/// values, which are <c>#define</c>s rather than declarations.
/// <para>
/// The header is <c>spa/monitor/device.h</c>, not <c>spa/device/device.h</c>; the latter does not
/// exist and looking for it is the first thing that goes wrong here.
/// </para>
/// </remarks>
internal static class SpaDevice
{
    /// <summary>The interface type string, as <c>pw_core_export</c> expects it.</summary>
    internal const string InterfaceType = "Spa:Pointer:Interface:Device";

    internal const uint Version = 0;
    internal const uint VersionInfo = 0;
    internal const uint VersionEvents = 0;
    internal const uint VersionMethods = 0;
    internal const uint VersionObjectInfo = 0;

    internal const ulong ChangeMaskFlags = 1UL << 0;
    internal const ulong ChangeMaskProps = 1UL << 1;
    internal const ulong ChangeMaskParams = 1UL << 2;

    /// <summary>The result type an <c>enum_params</c> answer carries.</summary>
    internal const uint ResultTypeParams = 1;
}
