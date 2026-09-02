namespace PipeWire.NET.Graph;

/// <summary>
/// What a client may do with one object.
/// </summary>
/// <param name="ObjectId">
/// The global id, or <see cref="PipeWireClientControl.AnyObject"/> for the default applied to
/// everything not named individually.
/// </param>
/// <param name="Permissions">What is permitted. Absolute, not added to what was there before.</param>
/// <remarks>
/// A client that cannot <see cref="PipeWirePermissions.Read"/> an object does not see it in the
/// registry at all - it is hidden rather than refused, so a confined client observes a smaller graph
/// instead of an erroring one.
/// </remarks>
public readonly record struct PipeWireObjectPermission(uint ObjectId, PipeWirePermissions Permissions);
