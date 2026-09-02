namespace PipeWire.NET.Interop;

/// <summary>
/// Invokes subscriber delegates so that one that throws cannot take the others with it.
/// </summary>
/// <remarks>
/// Events are raised from the PipeWire loop thread, inside a native callback frame. An exception
/// leaving that frame aborts the process rather than unwinding, and one handler throwing must not
/// stop the rest from being told. Nine copies of this loop had drifted apart across the graph
/// types; this is the one shape they all wanted.
/// </remarks>
internal static class SafeCallback
{
    /// <summary>Invokes each subscriber, reporting any that throws to <paramref name="onFault"/>.</summary>
    /// <param name="handlers">The event's delegate, or null when nothing is subscribed.</param>
    /// <param name="invoke">Calls one subscriber.</param>
    /// <param name="onFault">Records a subscriber that threw. Must not throw itself.</param>
    internal static void Raise<TDelegate>(
        TDelegate? handlers,
        Action<TDelegate> invoke,
        Action<Exception> onFault)
        where TDelegate : Delegate
    {
        if (handlers is null) return;

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                invoke((TDelegate)handler);
            }
            catch (Exception ex)
            {
                // The reporter runs on the same native frame, so it has the same obligation.
                try { onFault(ex); }
                catch (Exception) { /* Deliberately not logged: nothing left that could report it. */ }
            }
        }
    }
}
