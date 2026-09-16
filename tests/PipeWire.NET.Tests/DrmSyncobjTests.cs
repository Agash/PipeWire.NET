using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Interop;
using PipeWire.NET.Media;

namespace PipeWire.NET.Tests;

/// <summary>
/// The DRM syncobj timeline calls behind explicit sync, exercised directly rather than only
/// through a producer and a consumer that could be wrong in the same way.
/// </summary>
/// <remarks>
/// <para>
/// The end-to-end explicit-sync tests prove that frames flow when both ends are this library. They
/// cannot tell a correct wait from one that returns at once, because either lets frames through.
/// These pin the primitives: a point that has not been signalled is waited for until the deadline
/// and reported as <c>ETIME</c>, one that has is reached, a signal made through one handle is seen
/// through another on the same timeline, and an eventfd is not taken for a syncobj.
/// </para>
/// <para>
/// The deadline is where a mistake hides. <c>drmSyncobjTimelineWait</c> takes an absolute
/// <c>CLOCK_MONOTONIC</c> time; handed a duration instead it waits until the machine's uptime
/// passes it - that is, not at all - and still reports a timeout, so only the elapsed time shows it.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresGpu")]
[SupportedOSPlatform("linux")]
public sealed class DrmSyncobjTests
{
    private static void RequireSyncobjs()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("DRM syncobjs are a Linux interface.");
        if (!DrmSyncobj.IsAvailable)
            Assert.Inconclusive("no render node whose driver supports timeline syncobjs.");
    }

    [TestMethod]
    public void ATimeline_IsReachedOnlyOnceItsPointIsSignalled()
    {
        RequireSyncobjs();

        (uint handle, int fd) = DrmSyncobj.Create();
        Assert.AreNotEqual(0u, handle, "no timeline could be created");
        Assert.IsTrue(fd >= 0, "the timeline was not exported as a descriptor");

        try
        {
            long start = Environment.TickCount64;
            SyncWait early = DrmSyncobj.Wait(handle, 5, TimeSpan.FromMilliseconds(200));
            long elapsed = Environment.TickCount64 - start;

            Assert.AreEqual(SyncWaitOutcome.TimedOut, early.Outcome, "an unsignalled point was not waited for");
            Assert.AreEqual(NativeLibc.ETIME, early.Errno);
            Assert.IsTrue(elapsed >= 150,
                $"the wait gave up after {elapsed}ms of a 200ms deadline - the deadline is not absolute CLOCK_MONOTONIC");

            Assert.IsTrue(DrmSyncobj.Signal(handle, 5), "the signal was refused");

            Assert.IsTrue(DrmSyncobj.Wait(handle, 5, TimeSpan.FromSeconds(1)).Reached, "the signalled point was not reached");
            Assert.IsTrue(DrmSyncobj.Wait(handle, 3, TimeSpan.FromSeconds(1)).Reached, "an earlier point was not reached");
            Assert.AreEqual(SyncWaitOutcome.TimedOut, DrmSyncobj.Wait(handle, 6, TimeSpan.FromMilliseconds(50)).Outcome,
                "a later point read as reached");
        }
        finally
        {
            DrmSyncobj.Destroy(handle);
            Descriptors.CloseDescriptor(fd);
        }
    }

    /// <summary>
    /// A timeline imported from its descriptor is the same timeline: what is signalled through one
    /// handle is reached through the other, which is how a producer and a consumer share one.
    /// </summary>
    [TestMethod]
    public void AnImportedTimeline_IsTheSameTimeline()
    {
        RequireSyncobjs();

        (uint created, int fd) = DrmSyncobj.Create();
        uint imported = DrmSyncobj.Import(fd, out int errno);

        try
        {
            Assert.AreNotEqual(0u, imported, $"the timeline's own descriptor did not import (errno {errno})");
            Assert.AreEqual(SyncTimelineKind.Syncobj, SyncTimeline.Classify(fd, out uint classified, out _));
            DrmSyncobj.Destroy(classified);

            Assert.IsTrue(DrmSyncobj.Signal(imported, 9));
            Assert.IsTrue(DrmSyncobj.Wait(created, 9, TimeSpan.FromSeconds(1)).Reached,
                "a point signalled through the imported handle was not seen through the original");

            Assert.IsTrue(SyncTimeline.Wait(fd, 9, TimeSpan.FromSeconds(1)).Reached,
                "the descriptor-level wait did not see the point");
        }
        finally
        {
            DrmSyncobj.Destroy(imported);
            DrmSyncobj.Destroy(created);
            Descriptors.CloseDescriptor(fd);
        }
    }

    /// <summary>A promised release on a syncobj is signalled at its point.</summary>
    [TestMethod]
    public void AReleasePromisedOnASyncobj_SignalsItsPoint()
    {
        RequireSyncobjs();

        (uint handle, int fd) = DrmSyncobj.Create();
        try
        {
            SyncRelease release = SyncRelease.For(fd, 7);
            Assert.IsTrue(release.IsPending, "no release was promised on a syncobj");
            Assert.IsTrue(release.Signal(), "the release signal was refused");

            Assert.IsTrue(DrmSyncobj.Wait(handle, 7, TimeSpan.FromSeconds(1)).Reached,
                "the release point was not signalled");
        }
        finally
        {
            DrmSyncobj.Destroy(handle);
            Descriptors.CloseDescriptor(fd);
        }
    }

    /// <summary>An eventfd does not import as a syncobj, and is classified as what it is.</summary>
    [TestMethod]
    public void AnEventfd_DoesNotImportAsASyncobj()
    {
        RequireSyncobjs();

        int fd = Descriptors.CreateEventfd();
        try
        {
            Assert.AreEqual(0u, DrmSyncobj.Import(fd, out int errno), "an eventfd imported as a syncobj");
            Assert.AreNotEqual(0, errno, "the refused import carries no errno");
            Assert.AreEqual(SyncTimelineKind.Eventfd, SyncTimeline.Classify(fd, out _, out _));
        }
        finally
        {
            Descriptors.CloseDescriptor(fd);
        }
    }
}
