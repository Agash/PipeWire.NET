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

    /// <summary>
    /// Every property the daemon sent for this object, whether or not this library models it.
    /// </summary>
    /// <remarks>
    /// The typed members cover what this library has an opinion about. PipeWire's property set is
    /// open, so this is the rest: keys a module or session manager added, and keys a newer daemon
    /// sends that no release here models yet.
    /// </remarks>
    PipeWireProperties Properties { get; }

    /// <summary>
    /// The daemon's serial for this object, which is never reused.
    /// </summary>
    /// <remarks>
    /// <see cref="Id"/> is reused: the daemon assigns the lowest free one, so an id observed twice
    /// may be two unrelated objects. A serial is monotonic for the life of the daemon, which makes
    /// it the only safe way to tell "still the same object" from "something else took the id".
    /// Null only if the daemon did not send it.
    /// </remarks>
    ulong? ObjectSerial { get; }
}
