using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// Every ordering of writes and echoes, without a daemon.
/// </summary>
/// <remarks>
/// The orderings that break this are the ones a live session produces rarely and only under load,
/// which is how each of its past defects reached a release: a stale echo overwriting a newer value,
/// then a fix that suppressed other clients' changes instead. Driving it directly makes those
/// orderings ordinary test cases.
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class MetadataReconcilerTests
{
    private const uint Subject = 0;
    private const string Key = "k";
    private const string Type = "Spa:String";

    private long _now;

    private MetadataReconciler NewReconciler(TimeSpan? window = null)
    {
        _now = 0;
        return new MetadataReconciler(window ?? TimeSpan.FromSeconds(5), () => _now);
    }

    private void Advance(TimeSpan by) => _now += (long)(System.Diagnostics.Stopwatch.Frequency * by.TotalSeconds);

    private static void Write(MetadataReconciler r, string? value) => r.NoteWrite(Subject, Key, Type, value);

    /// <summary>True when the echo reaches the cache at all, raised or not.</summary>
    private static bool Echo(MetadataReconciler r, string? value) =>
        r.Classify(Subject, Key, Type, value) != MetadataReconciler.EchoAction.Drop;

    private static MetadataReconciler.EchoAction Classify(MetadataReconciler r, string? value) =>
        r.Classify(Subject, Key, Type, value);

    [TestMethod]
    public void AnEchoOfTheOnlyWrite_IsAppliedButNotRaisedTwice()
    {
        // The store raised it when it applied the write, so raising again would report one change
        // twice to every subscriber.
        MetadataReconciler r = NewReconciler();
        Write(r, "A");

        Assert.AreEqual(MetadataReconciler.EchoAction.AlreadyKnown, Classify(r, "A"));
    }

    [TestMethod]
    public void AnEchoOfAValueNobodyHereWrote_IsRaised()
    {
        MetadataReconciler r = NewReconciler();
        Write(r, "A");

        Assert.AreEqual(MetadataReconciler.EchoAction.ApplyAndRaise, Classify(r, "external"));
    }

    [TestMethod]
    public void AnEchoOfASupersededWrite_IsSuppressed()
    {
        MetadataReconciler r = NewReconciler();
        Write(r, "A");
        Write(r, "B");

        Assert.IsFalse(Echo(r, "A"), "the older write must not be put back");
        Assert.IsTrue(Echo(r, "B"));
    }

    [TestMethod]
    public void EchoesArrivingNewestFirst_DoNotResurrectTheOlderValue()
    {
        // The ordering that a clear-on-acknowledge implementation gets wrong: acknowledging the
        // newest echo discards the older records, and the late echo then reads as external.
        MetadataReconciler r = NewReconciler();
        Write(r, "A");
        Write(r, "B");
        Write(r, "C");

        Assert.IsTrue(Echo(r, "C"));
        Assert.IsFalse(Echo(r, "A"));
        Assert.IsFalse(Echo(r, "B"));
    }

    [TestMethod]
    public void AChangeThisClientNeverWrote_IsAlwaysApplied()
    {
        MetadataReconciler r = NewReconciler();
        Write(r, "A");
        Write(r, "B");

        Assert.IsTrue(Echo(r, "external"), "another client's change must never be suppressed");
    }

    [TestMethod]
    public void TheSameTextUnderADifferentType_IsSomebodyElsesChange()
    {
        MetadataReconciler r = NewReconciler();
        r.NoteWrite(Subject, Key, "Spa:String", "42");

        Assert.AreEqual(MetadataReconciler.EchoAction.ApplyAndRaise, r.Classify(Subject, Key, "Spa:Int", "42"),
            "state is (subject, key, type, value); the same text under another type is a different change");
    }

    [TestMethod]
    public void TwoIdenticalWrites_HoldTwoRecords()
    {
        // Bookkeeping is per operation. Tracking by value alone lets one failure discard both.
        MetadataReconciler r = NewReconciler();
        MetadataReconciler.PendingWrite first = r.NoteWrite(Subject, Key, Type, "A");
        r.NoteWrite(Subject, Key, Type, "A");
        r.NoteWrite(Subject, Key, Type, "B");

        r.Forget(Subject, Key, first);

        Assert.IsFalse(Echo(r, "A"), "the second identical write is still outstanding");
    }

    [TestMethod]
    public void AWriteThatFailed_StopsSuppressingOnceForgotten()
    {
        MetadataReconciler r = NewReconciler();
        MetadataReconciler.PendingWrite failed = r.NoteWrite(Subject, Key, Type, "A");
        r.NoteWrite(Subject, Key, Type, "B");

        r.Forget(Subject, Key, failed);

        Assert.IsTrue(Echo(r, "A"), "a write that never landed must not suppress that value for ever");
    }

    [TestMethod]
    public void ARecordOlderThanTheWindow_StopsSuppressing()
    {
        // The daemon coalesces echoes and produces none at all for a write of the value a key
        // already holds, so records cannot rely on ever being acknowledged.
        MetadataReconciler r = NewReconciler(TimeSpan.FromSeconds(5));
        Write(r, "A");
        Write(r, "B");

        Assert.IsFalse(Echo(r, "A"));

        Advance(TimeSpan.FromSeconds(6));

        Assert.IsTrue(Echo(r, "A"), "an echo cannot still be in flight after the window");
    }

    [TestMethod]
    public void AClear_ForgetsEverythingOutstanding()
    {
        MetadataReconciler r = NewReconciler();
        Write(r, "A");
        Write(r, "B");

        r.Clear();

        Assert.IsTrue(Echo(r, "A"), "a cleared store has nothing outstanding to suppress");
        Assert.AreEqual(0, r.TrackedKeys);
    }

    [TestMethod]
    public void WritingManyDistinctKeys_DoesNotGrowTheTrackedSetForEver()
    {
        // One bucket per key was retained for the life of the process, so a patchbay writing
        // generated keys grew without bound.
        MetadataReconciler r = NewReconciler();

        for (int i = 0; i < 1000; i++)
        {
            MetadataReconciler.PendingWrite w = r.NoteWrite(Subject, $"key-{i}", Type, "v");
            r.Forget(Subject, $"key-{i}", w);
        }

        Assert.AreEqual(0, r.TrackedKeys, "settled keys must not be retained");
    }

    [TestMethod]
    public void ARemovalFollowedByALateEchoOfAnEarlierValue_StaysRemoved()
    {
        // A burst then a removal, with one older echo still in flight. Applying it would bring a
        // deleted entry back.
        MetadataReconciler r = NewReconciler();
        for (int i = 0; i < 10; i++) Write(r, $"value-{i}");
        Write(r, null);

        Assert.IsTrue(Echo(r, null), "the removal itself is the newest write");
        Assert.IsFalse(Echo(r, "value-9"), "a late echo must not undo the removal");
    }

    [TestMethod]
    public void AFuzzOfInterleavedWritesAndEchoes_NeverSuppressesAnUnwrittenValue()
    {
        // The one invariant that must hold for every ordering: a value this client never wrote is
        // somebody else's change and is always applied.
        var random = new Random(20260902);
        MetadataReconciler r = NewReconciler();
        var written = new HashSet<string>(StringComparer.Ordinal);

        for (int step = 0; step < 20_000; step++)
        {
            if (random.Next(2) == 0)
            {
                string value = $"v{random.Next(50)}";
                written.Add(value);
                Write(r, value);
            }
            else
            {
                string value = $"v{random.Next(80)}";
                bool applied = Echo(r, value);

                if (!written.Contains(value))
                    Assert.IsTrue(applied, $"step {step}: value '{value}' was never written here");
            }

            if (random.Next(20) == 0) Advance(TimeSpan.FromSeconds(1));
        }
    }

    [TestMethod]
    public void WritesRacingTheBucketBeingRemoved_AreNeverLost()
    {
        // NoteWrite takes the bucket from the dictionary and locks it in two steps. A Forget in
        // between empties the key and removes the bucket, so without a re-check the write lands in
        // an orphan and the echo it was meant to suppress reads as somebody else's change.
        var reconciler = new MetadataReconciler(TimeSpan.FromSeconds(30));
        var faults = new System.Collections.Concurrent.ConcurrentQueue<string>();

        Task[] workers =
        [
            .. Enumerable.Range(0, 8).Select(w => Task.Run(() =>
            {
                for (int i = 0; i < 4000; i++)
                {
                    string k = $"key-{i % 4}";
                    MetadataReconciler.PendingWrite pending = reconciler.NoteWrite(Subject, k, Type, $"v{w}");

                    // The write must be visible to the classifier the instant it is recorded.
                    if (reconciler.Classify(Subject, k, Type, $"v{w}") == MetadataReconciler.EchoAction.ApplyAndRaise)
                        faults.Enqueue($"worker {w} step {i}: its own write was not recorded");

                    reconciler.Forget(Subject, k, pending);
                }
            })),
        ];

        Task.WaitAll(workers);
        Assert.IsTrue(faults.IsEmpty, faults.TryDequeue(out string? first) ? first : string.Empty);
    }
}
