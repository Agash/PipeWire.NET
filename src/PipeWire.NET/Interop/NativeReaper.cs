using System.Collections.Concurrent;

namespace PipeWire.NET.Interop;

/// <summary>
/// Off-thread teardown for natively owned handles the finalizer reclaims.
/// </summary>
/// <remarks>
/// <para>
/// Destroying a proxy, stream, core, context or loop needs the PipeWire loop mutex, and taking
/// it can block behind a busy loop. From deterministic disposal that wait is correct; from the
/// finalizer thread it stalls every finalizer behind one abandoned handle. The finalizer therefore
/// never destroys: it hands the whole release to this single background thread and returns, and
/// the reaper waits whatever the loop needs.
/// </para>
/// <para>
/// A loop is destroyed only after the objects bound to it have drained. That wait must never hold
/// the only worker hostage: a loop item whose objects are still queued behind it goes back to the
/// back of the queue and the worker keeps draining instead. Abandonment is finite - every dropped
/// handle enqueues exactly once - so a deferred loop item always reaches an empty count.
/// Finalizer order is still arbitrary, so the guards inside every release stay load-bearing: an
/// object whose loop already went skips its destroy and just releases its references.
/// </para>
/// </remarks>
internal static class NativeReaper
{
    private sealed record WorkItem(Func<bool> Release, nint Loop, bool WaitForDrain);

    private static readonly BlockingCollection<WorkItem> _queue = new();
    private static readonly ConcurrentDictionary<nint, int> _pending = new();
    private static readonly Thread _thread = new(Run)
    {
        IsBackground = true,
        Name = "PipeWire.NET native reaper",
    };

    static NativeReaper() => _thread.Start();

    /// <summary>Enqueues a handle release, counted against its loop until it runs.</summary>
    internal static void Enqueue(object owner, nint loop, Func<bool> release)
    {
        if (loop != 0)
            _pending.AddOrUpdate(loop, 1, static (_, n) => n + 1);

        // The queue roots the owner until its release runs, which is what keeps the native
        // pointers and the SafeHandle references it carries valid in between.
        _queue.Add(new WorkItem(release, loop, WaitForDrain: false));
    }

    /// <summary>
    /// Enqueues a loop destroy, which runs once nothing bound to the loop is still queued.
    /// The loop's own item is deliberately not counted: waiting on a count that includes
    /// itself would never reach zero.
    /// </summary>
    internal static void EnqueueLoop(object owner, nint loop, Func<bool> release) =>
        _queue.Add(new WorkItem(release, loop, WaitForDrain: true));

    /// <summary>How many enqueued releases for this loop have not run yet.</summary>
    internal static int PendingFor(nint loop) =>
        _pending.TryGetValue(loop, out int n) ? n : 0;

    private static void Run()
    {
        foreach (WorkItem work in _queue.GetConsumingEnumerable())
        {
            if (work.WaitForDrain && PendingFor(work.Loop) > 0)
            {
                _queue.Add(work);
                Thread.Sleep(0);
                continue;
            }

            try
            {
                work.Release();
            }
            catch
            {
                // Last resort already: there is nothing to report a late teardown failure to,
                // and letting it escape would take the reaper - and every release behind it -
                // down with it.
            }
            finally
            {
                if (!work.WaitForDrain && work.Loop != 0
                    && _pending.AddOrUpdate(work.Loop, 0, static (_, n) => n - 1) <= 0)
                    _pending.TryRemove(work.Loop, out _);
            }
        }
    }
}
