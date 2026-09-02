namespace PipeWire.NET.Graph;

/// <summary>
/// Identity shared by every object the registry surfaces.
/// </summary>
/// <remarks>
/// Deliberately carries identity and nothing else. Navigation belongs to whatever owns the
/// relationships and mutation belongs to <see cref="PipeWireRegistry"/>; putting either here would
/// require every object to hold a reference back to the registry, which is what makes object
/// lifetime hard to reason about.
/// </remarks>
public interface IPipeWireObject
{
    /// <summary>The PipeWire global id, unique among live objects.</summary>
    uint Id { get; }

    /// <summary>Which kind of object this is, for dispatching without a type test.</summary>
    PipeWireObjectKind Kind { get; }

    /// <summary>What this client is permitted to do with the object.</summary>
    PipeWirePermissions Permissions { get; }

    /// <summary>
    /// The interface version the daemon announced for this object.
    /// </summary>
    /// <remarks>
    /// Determines which methods and events the object supports. A version-gated feature must check
    /// this rather than assume the compile-time constant, because the daemon may be older.
    /// </remarks>
    uint InterfaceVersion { get; }
}
