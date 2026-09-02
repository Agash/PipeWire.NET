namespace PipeWire.NET.Spa;

/// <summary>
/// What may be done with one of an object's parameters.
/// </summary>
/// <remarks>
/// Declared as <c>#define</c> macros in <c>spa/param/param.h</c> rather than as an enum, which is
/// why these are hand-written while the rest of the SPA constants are generated.
/// </remarks>
public static class SpaParamInfoFlags
{
    /// <summary>
    /// The parameter is re-sent even when its value has not changed.
    /// </summary>
    /// <remarks>
    /// Set on parameters whose meaning is the event rather than the value, so a subscriber sees each
    /// one rather than only the transitions.
    /// </remarks>
    public const uint Serial = 1u << 0;

    /// <summary>The parameter can be enumerated.</summary>
    public const uint Read = 1u << 1;

    /// <summary>The parameter can be written.</summary>
    public const uint Write = 1u << 2;
}
