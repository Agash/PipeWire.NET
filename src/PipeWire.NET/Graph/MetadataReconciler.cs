using System.Collections.Concurrent;
using System.Diagnostics;

namespace PipeWire.NET.Graph;

/// <summary>
/// Decides whether a metadata change reported by the daemon is this client's own write coming back
/// or somebody else's change.
/// </summary>
/// <remarks>
/// <para>
/// The store applies a write locally as soon as it is issued, because the echo can arrive after the
/// round-trip that proves the write was processed. That optimism is what makes this necessary:
/// echoes of superseded writes must not put an old value back, and other clients' changes must
/// always be applied.
/// </para>
/// <para>
/// A write is tracked as an operation rather than a value, so two identical writes hold two records
/// and acknowledging one does not discard the other. Records leave by age, not by count: the daemon
/// coalesces echoes heavily and writing a value a key already holds produces none at all, so a
/// count-bounded set fills and never drains, while evicting one whose echo is still in flight makes
/// that echo read as an external change.
/// </para>
/// <para>
/// Separate from the store so it runs without a daemon. Its failure modes are orderings that are
/// hard to provoke live and trivial to enumerate in a test.
/// </para>
/// </remarks>
internal sealed class MetadataReconciler(TimeSpan window, Func<long>? clock = null)
{
    private readonly ConcurrentDictionary<(uint Subject, string Key), List<PendingWrite>> _outstanding = new();
    private readonly long _window = (long)(Stopwatch.Frequency * window.TotalSeconds);
    private readonly Func<long> _clock = clock ?? Stopwatch.GetTimestamp;

    /// <summary>One write issued and not yet echoed back.</summary>
    internal sealed class PendingWrite(string? type, string? value, long stamp)
    {
        internal long Stamp { get; } = stamp;

        internal bool Matches(string? otherType, string? otherValue) =>
            string.Equals(value, otherValue, StringComparison.Ordinal)
            && string.Equals(type, otherType, StringComparison.Ordinal);
    }

    /// <summary>Records a write about to be issued. The token identifies it if it has to be undone.</summary>
    internal PendingWrite NoteWrite(uint subject, string key, string? type, string? value)
    {
        long now = _clock();
        var pending = new PendingWrite(type, value, now);

        // The bucket has to still be the one the dictionary holds when the write goes in. Between
        // GetOrAdd and taking the lock, a concurrent Forget or Settle can empty this key and remove
        // the bucket, and the write would then sit in an orphan nothing can find: the record is
        // lost, and the echo it was meant to suppress reads as another client's change.
        while (true)
        {
            List<PendingWrite> bucket = _outstanding.GetOrAdd((subject, key), static _ => []);

            lock (bucket)
            {
                if (_outstanding.TryGetValue((subject, key), out List<PendingWrite>? current)
                    && ReferenceEquals(current, bucket))
                {
                    bucket.RemoveAll(e => now - e.Stamp > _window);
                    bucket.Add(pending);
                    return pending;
                }
            }
        }
    }

    /// <summary>Drops a write that never landed, so it cannot suppress an echo that will not come.</summary>
    internal void Forget(uint subject, string key, PendingWrite pending)
    {
        if (!_outstanding.TryGetValue((subject, key), out List<PendingWrite>? bucket)) return;

        lock (bucket) bucket.Remove(pending);
        DropIfEmpty(subject, key, bucket);
    }

    /// <summary>What to do with a change the daemon reported.</summary>
    internal enum EchoAction
    {
        /// <summary>Somebody else's change. Apply it and tell subscribers.</summary>
        ApplyAndRaise,

        /// <summary>Our own newest write coming home. Already applied and already raised locally.</summary>
        AlreadyKnown,

        /// <summary>An echo of a write we have since superseded. Applying it would go backwards.</summary>
        Drop,
    }

    /// <summary>Classifies a change the daemon reported.</summary>
    internal EchoAction Classify(uint subject, string key, string? type, string? value)
    {
        if (!_outstanding.TryGetValue((subject, key), out List<PendingWrite>? bucket))
            return EchoAction.ApplyAndRaise;

        lock (bucket)
        {
            long now = _clock();
            bucket.RemoveAll(e => now - e.Stamp > _window);
            if (bucket.Count == 0) return EchoAction.ApplyAndRaise;

            // Our newest write coming home. The store raised it when it applied the write, so
            // raising again here would report one change twice.
            if (bucket[^1].Matches(type, value)) return EchoAction.AlreadyKnown;

            // Records are deliberately not cleared when the newest is acknowledged: clearing
            // discards the older entries, and echoes still in flight for those then match nothing
            // and read as an external change, putting a superseded value back.
            return bucket.Exists(e => e.Matches(type, value))
                ? EchoAction.Drop
                : EchoAction.ApplyAndRaise;
        }
    }

    /// <summary>Forgets everything, for a store-wide clear.</summary>
    internal void Clear() => _outstanding.Clear();

    /// <summary>How many keys are currently tracked. For tests asserting the set drains.</summary>
    internal int TrackedKeys => _outstanding.Count;

    private void DropIfEmpty(uint subject, string key, List<PendingWrite> bucket)
    {
        // Conditional and under the bucket's own lock, so a write arriving concurrently either finds
        // the bucket still present or re-adds it through GetOrAdd.
        lock (bucket)
        {
            if (bucket.Count != 0) return;
            _outstanding.TryRemove(
                new KeyValuePair<(uint, string), List<PendingWrite>>((subject, key), bucket));
        }
    }

    /// <summary>Drops the key's bucket if the write that settled it was the last one outstanding.</summary>
    internal void Settle(uint subject, string key)
    {
        if (_outstanding.TryGetValue((subject, key), out List<PendingWrite>? bucket))
            DropIfEmpty(subject, key, bucket);
    }
}
