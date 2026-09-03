using System.Collections.Concurrent;


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
internal sealed class MetadataReconciler(TimeSpan window, TimeProvider? time = null)
{
    private readonly ConcurrentDictionary<(uint Subject, string Key), List<PendingWrite>> _outstanding = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _windowSpan = window;

    // Ticks of whatever provider is in use, so a test clock and the system clock are compared in
    // their own units rather than through a conversion that rounds.
    private long _window => (long)(_time.TimestampFrequency * _windowSpan.TotalSeconds);

    private long Now() => _time.GetTimestamp();

    /// <summary>One write issued and not yet echoed back.</summary>
    /// <remarks>
    /// The age that matters runs from acknowledgement, not from issue. Until the daemon has
    /// processed the request there is no echo yet to be late, and expiring the record on elapsed
    /// time alone means a daemon slower than the window puts the superseded value back - the client
    /// forgets it wrote, and reads its own echo as somebody else's change.
    /// </remarks>
    internal sealed class PendingWrite(string? type, string? value)
    {
        private long _stamp = NotAcknowledged;

        internal const long NotAcknowledged = -1;

        /// <summary>When the daemon acknowledged it, or <see cref="NotAcknowledged"/>.</summary>
        internal long Stamp => Volatile.Read(ref _stamp);

        /// <summary>Starts the clock. The first acknowledgement wins; later ones do not extend it.</summary>
        internal void Acknowledge(long now) =>
            Interlocked.CompareExchange(ref _stamp, now, NotAcknowledged);

        internal bool Expired(long now, long window) =>
            Stamp != NotAcknowledged && now - Stamp > window;

        internal bool Matches(string? otherType, string? otherValue) =>
            string.Equals(value, otherValue, StringComparison.Ordinal)
            && string.Equals(type, otherType, StringComparison.Ordinal);
    }

    /// <summary>Records a write about to be issued. The token identifies it if it has to be undone.</summary>
    internal PendingWrite NoteWrite(uint subject, string key, string? type, string? value)
    {
        long now = Now();
        var pending = new PendingWrite(type, value);

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
                    bucket.RemoveAll(e => e.Expired(now, _window));
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
            long now = Now();
            bucket.RemoveAll(e => e.Expired(now, _window));
            if (bucket.Count == 0)
            {
                // Dropped here as well as in Forget and Settle. Expiry is the only path that empties
                // a bucket without anyone asking, and leaving the empty list in the dictionary roots
                // one entry per key ever written for the life of the store.
                DropIfEmpty(subject, key, bucket);
                return EchoAction.ApplyAndRaise;
            }

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

    /// <summary>Tidies the key's bucket once a write has been acknowledged by the daemon.</summary>
    /// <remarks>
    /// Deliberately does <em>not</em> remove the write that settled, which is worth saying because
    /// the obvious reading of the name is that it does. The round trip proves the daemon processed
    /// the request; it does not mean the echo has been dispatched, and recognising that echo is the
    /// only reason a record exists. Removing it here would make the store's own write come back
    /// looking like another client's change and be raised a second time.
    /// <para>
    /// What it does do is start the clock: a record ages from the acknowledgement that proves the
    /// daemon dealt with it, so a daemon slower than the window cannot expire a write whose echo has
    /// not been dispatched yet.
    /// </para>
    /// <para>
    /// It drops a bucket only when it is already empty, which happens once every record in it
    /// has expired. That is not nothing - it is what keeps the dictionary from holding one entry per
    /// key ever written - but it is the extent of it.
    /// </para>
    /// </remarks>
    internal void Settle(uint subject, string key)
    {
        if (!_outstanding.TryGetValue((subject, key), out List<PendingWrite>? bucket)) return;

        // The round trip proves every request issued before it was processed, so this is where the
        // records in this bucket start ageing. Anything still unacknowledged after it was never
        // issued, and Forget deals with those.
        long now = Now();
        lock (bucket)
        {
            foreach (PendingWrite entry in bucket) entry.Acknowledge(now);
        }

        DropIfEmpty(subject, key, bucket);
    }
}
