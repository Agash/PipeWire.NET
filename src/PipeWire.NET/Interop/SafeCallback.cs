namespace PipeWire.NET.Interop;

/// <summary>
/// Invokes subscriber delegates so that one that throws cannot take the others with it.
/// </summary>
/// <remarks>
/// Events are raised from the PipeWire loop thread, inside a native callback frame. An exception
/// leaving that frame aborts the process rather than unwinding, and one handler throwing must not
/// stop the rest from being told.
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

    /// <summary>The same, with the event's arguments passed rather than captured.</summary>
    /// <typeparam name="TDelegate">The event's delegate type.</typeparam>
    /// <typeparam name="TState">What the invocation needs, passed through untouched.</typeparam>
    /// <param name="handlers">The event's delegate, or null when nothing is subscribed.</param>
    /// <param name="state">Handed to <paramref name="invoke"/> and <paramref name="onFault"/>.</param>
    /// <param name="invoke">Calls one subscriber. Pass a static lambda.</param>
    /// <param name="onFault">Records a subscriber that threw. Must not throw itself.</param>
    /// <remarks>
    /// The overload above closes over whatever the caller's lambdas reference, which is two
    /// allocations on every raise. These run on the loop thread, so the state form exists for the
    /// paths that fire per graph change or per processing cycle rather than per user action.
    /// </remarks>
    internal static void Raise<TDelegate, TState>(
        TDelegate? handlers,
        TState state,
        Action<TDelegate, TState> invoke,
        Action<TState, Exception> onFault)
        where TDelegate : Delegate
    {
        if (handlers is null) return;

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                invoke((TDelegate)handler, state);
            }
            catch (Exception ex)
            {
                try { onFault(state, ex); }
                catch (Exception) { /* Deliberately not logged: nothing left that could report it. */ }
            }
        }
    }
}
