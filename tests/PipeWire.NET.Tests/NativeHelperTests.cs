using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Interop;
using PipeWire.NET.Media;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// The hand-written half of the interop: the SPA macros and the one list operation this library
/// reimplements rather than binds.
/// </summary>
/// <remarks>
/// These are C macros and static inlines, so ClangSharp emits nothing for them and there is no
/// native symbol to call through. Getting one wrong is silent - a sequence compared against the
/// wrong mask matches nothing, and a hook unlinked wrongly corrupts a list the daemon walks.
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed unsafe class NativeHelperTests
{
    [TestMethod]
    public void AnAsyncResult_IsRecognisedAndItsSequenceExtracted()
    {
        // A method that queued its work returns SPA_ASYNC_BIT | seq; one that completed returns a
        // status. Telling them apart is how a caller knows whether to wait for an answer.
        for (int seq = 0; seq < 8; seq++)
        {
            int result = Native.SPA_ASYNC_BIT | seq;

            Assert.IsTrue(Native.SPA_RESULT_IS_ASYNC(result), $"seq {seq} must read as async");
            Assert.AreEqual(seq, Native.SPA_RESULT_ASYNC_SEQ(result));
        }

        Assert.AreEqual(
            Native.SPA_ASYNC_SEQ_MASK,
            Native.SPA_RESULT_ASYNC_SEQ(Native.SPA_ASYNC_BIT | Native.SPA_ASYNC_SEQ_MASK));
    }

    [TestMethod]
    public void ASynchronousResult_IsNotMistakenForAQueuedOne()
    {
        // Zero is the common one: destroying a global and updating permissions both return it, and
        // reading either as a queued request waits for a reply that is never sent.
        Assert.IsFalse(Native.SPA_RESULT_IS_ASYNC(0));
        Assert.IsFalse(Native.SPA_RESULT_IS_ASYNC(-13));
        Assert.IsFalse(Native.SPA_RESULT_IS_ASYNC(1));
    }

    [TestMethod]
    public void RemovingAHookFromTheMiddle_LeavesItsNeighboursJoined()
    {
        // The list is circular and the daemon walks it, so a wrong unlink either drops the entries
        // after the removed one or leaves it reachable and dispatching into freed memory.
        spa_hook* a = Alloc();
        spa_hook* b = Alloc();
        spa_hook* c = Alloc();

        try
        {
            Link(a, b);
            Link(b, c);
            Link(c, a);

            Native.spa_hook_remove(b);

            Assert.IsTrue(a->link.next == &c->link, "a must now point past the removed hook");
            Assert.IsTrue(c->link.prev == &a->link, "c must now point back past it");
            Assert.IsTrue(b->link.next is null && b->link.prev is null,
                "the removed hook must not still reference the list");
        }
        finally
        {
            NativeMemory.Free(a);
            NativeMemory.Free(b);
            NativeMemory.Free(c);
        }
    }

    [TestMethod]
    public void RemovingAHookTwice_DoesNotTouchTheListAgain()
    {
        // Upstream leaves both pointers dangling after an unlink and relies on callers not doing
        // this; nulling them makes the second call a no-op for the list instead of an unlink
        // through stale pointers.
        spa_hook* a = Alloc();
        spa_hook* b = Alloc();

        try
        {
            Link(a, b);
            Link(b, a);

            Native.spa_hook_remove(b);
            Native.spa_hook_remove(b);

            Assert.IsTrue(a->link.next == &a->link, "the survivor must be a list of one");
            Assert.IsTrue(a->link.prev == &a->link);
        }
        finally
        {
            NativeMemory.Free(a);
            NativeMemory.Free(b);
        }
    }

    [TestMethod]
    public void AHookThatWasNeverAttached_IsRemovedWithoutDereferencingAnything()
    {
        spa_hook* hook = Alloc();

        try
        {
            Native.spa_hook_remove(hook);

            Assert.IsTrue(hook->link.next is null);
            Assert.IsTrue(hook->link.prev is null);
        }
        finally
        {
            NativeMemory.Free(hook);
        }
    }

    [TestMethod]
    public void RemovingANullHook_IsANoOp() => Native.spa_hook_remove(null);

    private static spa_hook* Alloc() => (spa_hook*)NativeMemory.AllocZeroed((nuint)sizeof(spa_hook));

    private static void Link(spa_hook* from, spa_hook* to)
    {
        from->link.next = &to->link;
        to->link.prev = &from->link;
    }

    // ------------------------------------------------------------------ descriptor duplication

    [TestMethod]
    public void DuplicatingAPlaneDescriptor_GivesADistinctOneThatOutlivesTheOriginal()
    {
        // A frame's descriptors are borrowed for the handler's duration. Planes of a planar format
        // may be backed by different ones, so an importer taking ownership of each needs a copy of
        // each; the frame's own DuplicateFd covers only the first.
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("dup is a libc call, and descriptors are a Linux concept here.");

        string path = Path.Combine(Path.GetTempPath(), $"pwnet-dup-{Environment.ProcessId}");
        File.WriteAllText(path, "x");

        try
        {
            using SafeFileHandle file = File.OpenHandle(path);
            long fd = file.DangerousGetHandle();

            var plane = new VideoPlane(fd, Offset: 0, Stride: 4, Size: 1);
            int copy = plane.DuplicateFd();

            try
            {
                Assert.IsTrue(copy >= 0, "dup of a live descriptor failed");
                Assert.AreNotEqual((int)fd, copy, "dup returned the descriptor it was given");

                // Still usable after the original is gone, which is the whole point of taking one.
                file.Dispose();
                Assert.IsTrue(Fcntl(copy, FGetFd) >= 0, "the copy did not outlive the original");
            }
            finally
            {
                if (copy >= 0) Close(copy);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void DuplicatingAPlaneThatIsNotFdBacked_ReportsThatRatherThanThrowing()
    {
        // Host-memory frames carry -1, and a caller looping over planes should not have to
        // special-case them before asking.
        Assert.AreEqual(-1, new VideoPlane(-1, 0, 0, 0).DuplicateFd());
    }

    private const int FGetFd = 1;

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int fd, int cmd);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);
}
