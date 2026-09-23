using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;
using PipeWire.NET.Interop;
using PipeWire.NET.Media;

namespace PipeWire.NET.Tests;

/// <summary>
/// The hand-written libc half of <c>NativeConstants</c>, and the errno behaviour it exists for.
/// </summary>
/// <remarks>
/// <para>
/// These six imports are hand-written rather than generated because this assembly sets
/// <c>DisableRuntimeMarshalling</c>, under which ClangSharp cannot emit a working
/// <c>SetLastError</c>: a <c>DllImport</c> carrying it is CA1420, and its
/// <c>[SetsLastSystemError]</c> is a <c>[Conditional("DEBUG")]</c> marker that suppresses
/// <c>SetLastError</c> without replacing it. Only <c>[LibraryImport]</c> does both, and ClangSharp
/// cannot emit that.
/// </para>
/// <para>
/// The failure that reasoning guards against is silent. If errno stopped being captured,
/// <c>Marshal.GetLastPInvokeError()</c> would read zero rather than <c>EINTR</c>, and the retry
/// loops around <c>read</c> and <c>write</c> would abandon an interrupted call instead of retrying
/// it - dropping a timeline signal, which strands whatever was waiting on it forever. Nothing
/// throws; a consumer simply stops receiving frames.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[SupportedOSPlatform("linux")]
public sealed class NativeLibcTests
{
    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("These are libc entry points.");
    }

    /// <summary>An eventfd timeline signals and is taken, which is the whole explicit-sync pairing.</summary>
    [TestMethod]
    public void AnEventfdTimeline_SignalsAndIsTaken()
    {
        RequireLinux();

        int fd = Descriptors.CreateEventfd();
        Assert.IsTrue(fd >= 0, "eventfd returned no descriptor");

        try
        {
            // Signal before waiting: the counter is already non-zero, so the wait returns rather
            // than blocking the test thread forever. That ordering is the producer/consumer shape
            // explicit sync uses.
            Assert.AreEqual(0, Descriptors.SignalEventfd(fd), "the signal was refused");
            Assert.AreEqual(
                SyncWaitOutcome.Reached,
                Descriptors.WaitEventfd(fd, TimeSpan.FromSeconds(1)).Outcome
            );

            // And again, to show the counter was actually taken down rather than left set.
            Assert.AreEqual(0, Descriptors.SignalEventfd(fd), "the second signal was refused");
            Assert.AreEqual(
                SyncWaitOutcome.Reached,
                Descriptors.WaitEventfd(fd, TimeSpan.FromSeconds(1)).Outcome
            );

            // Taken down to zero, so a third wait has nothing to take and must time out.
            Assert.AreEqual(
                SyncWaitOutcome.TimedOut,
                Descriptors.WaitEventfd(fd, TimeSpan.FromMilliseconds(50)).Outcome
            );
        }
        finally
        {
            Descriptors.CloseDescriptor(fd);
        }
    }

    /// <summary>
    /// A failed call sets errno, which is the property the hand-written imports exist to preserve.
    /// </summary>
    /// <remarks>
    /// <c>read</c> on a closed descriptor fails with <c>EBADF</c>. If <c>SetLastError</c> were
    /// dropped - which is what generating these would have done - the call would still return -1
    /// and this would read 0 instead.
    /// </remarks>
    [TestMethod]
    public unsafe void AFailedLibcCall_ReportsItsErrno()
    {
        RequireLinux();

        const int EBADF = 9;

        int fd = Descriptors.CreateEventfd();
        Descriptors.CloseDescriptor(fd);

        ulong scratch;
        nint result = NativeLibc.read(fd, &scratch, 8);

        Assert.IsTrue(result < 0, "reading a closed descriptor unexpectedly succeeded");
        Assert.AreEqual(
            EBADF,
            Marshal.GetLastPInvokeError(),
            "errno was not captured - SetLastError is not in effect on the libc imports"
        );
    }

    /// <summary>The EINTR the retry loops compare against is the one this system defines.</summary>
    /// <remarks>
    /// Read out of the installed header rather than asserted against a literal, which would only
    /// restate the declaration. A wrong value does not fail loudly: the loop simply never
    /// recognises an interrupted call, and a signal delivered at the wrong moment is lost.
    /// </remarks>
    [TestMethod]
    public void TheEintrConstant_MatchesThisSystemsErrnoHeader()
    {
        RequireLinux();

        string[] candidates =
        [
            "/usr/include/asm-generic/errno-base.h",
            "/usr/include/errno.h",
            "/usr/include/sys/errno.h",
        ];

        string? header = candidates.FirstOrDefault(File.Exists);
        if (header is null)
            Assert.Inconclusive("no errno header installed to check against.");

        Match match = Regex.Match(
            File.ReadAllText(header!),
            @"^#define\s+EINTR\s+(\d+)",
            RegexOptions.Multiline
        );

        if (!match.Success)
            Assert.Inconclusive($"EINTR is not defined directly in {header}.");

        Assert.AreEqual(
            NativeLibc.EINTR,
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            $"{header} defines a different EINTR than the retry loops compare against"
        );
    }

    /// <summary>A duplicated descriptor is a new one, is usable, and carries close-on-exec.</summary>
    /// <remarks>
    /// The close-on-exec flag is the point of the duplication rather than an extra: a descriptor
    /// that crossed a portal boundary must not leak into child processes. This reads the flag back
    /// through <c>fcntl(F_GETFD)</c> rather than trusting that the request was honoured.
    /// </remarks>
    [TestMethod]
    public void ADuplicatedDescriptor_IsDistinctAndCloseOnExec()
    {
        RequireLinux();

        const int FGetfd = 1;
        const int FdCloexec = 1;

        int original = Descriptors.CreateEventfd();
        try
        {
            using SafeFileHandle duplicate = FdInterop.DuplicateWithCloseOnExec(original);

            int copy = (int)duplicate.DangerousGetHandle();
            Assert.AreNotEqual(original, copy, "the duplicate reused the original's number");
            Assert.IsTrue(copy >= 3, "the duplicate landed on a stdio descriptor");

            int flags = NativeLibc.fcntl(copy, FGetfd, 0);
            Assert.IsTrue(flags >= 0, "F_GETFD failed on the duplicate");
            Assert.AreEqual(
                FdCloexec,
                flags & FdCloexec,
                "the duplicate does not carry close-on-exec, so it leaks into child processes"
            );

            // Both still name the same object: signalling one is taken through the other.
            Assert.AreEqual(0, Descriptors.SignalEventfd(original));
            Assert.AreEqual(
                SyncWaitOutcome.Reached,
                Descriptors.WaitEventfd(copy, TimeSpan.FromSeconds(1)).Outcome
            );
        }
        finally
        {
            Descriptors.CloseDescriptor(original);
        }
    }

    /// <summary>A non-socket is not a listening socket, and asking does not throw.</summary>
    /// <remarks>
    /// <c>getsockopt</c> fails with <c>ENOTSOCK</c> here. The answer wanted is "no", not an
    /// exception: this runs on the connect path against a descriptor a caller handed in, which may
    /// be anything at all.
    /// </remarks>
    [TestMethod]
    public void ANonSocket_IsNotReportedAsAListeningSocket()
    {
        RequireLinux();

        int fd = Descriptors.CreateEventfd();
        try
        {
            Assert.IsFalse(FdInterop.IsListeningSocket(fd));
        }
        finally
        {
            Descriptors.CloseDescriptor(fd);
        }
    }

    /// <summary>
    /// The generated <c>timespec</c> has the layout the kernel expects on this architecture.
    /// </summary>
    /// <remarks>
    /// It replaced a hand-written struct. Both fields are pointer-width on the platforms this
    /// library targets, and a mismatch would not throw - it would silently misread every timestamp
    /// crossing the boundary.
    /// </remarks>
    [TestMethod]
    public void TheGeneratedTimespec_IsTwoPointerWidthFields()
    {
        RequireLinux();

        Assert.AreEqual(
            2 * IntPtr.Size,
            Unsafe.SizeOf<PosixTimespec>(),
            "timespec is not two pointer-width fields, so every time conversion is misreading it"
        );
    }

    /// <summary>
    /// An eventfd wait with nothing signalled times out after its deadline, with ETIME, like a
    /// syncobj wait.
    /// </summary>
    /// <remarks>
    /// Upstream's example blocks without a deadline; a peer that never signalled would hang the loop
    /// thread for good. The elapsed time is checked too: a deadline computed wrongly returns at once,
    /// which reads as a timeout but waited for nothing.
    /// </remarks>
    [TestMethod]
    public void AnUnsignalledEventfdWait_TimesOutAfterItsDeadline()
    {
        RequireLinux();

        int fd = Descriptors.CreateEventfd();
        try
        {
            long start = Environment.TickCount64;
            SyncWait wait = Descriptors.WaitEventfd(fd, TimeSpan.FromMilliseconds(200));
            long elapsed = Environment.TickCount64 - start;

            Assert.AreEqual(SyncWaitOutcome.TimedOut, wait.Outcome);
            Assert.AreEqual(NativeLibc.ETIME, wait.Errno);
            Assert.IsTrue(
                elapsed >= 150,
                $"the wait returned after {elapsed}ms, before its 200ms deadline"
            );
        }
        finally
        {
            Descriptors.CloseDescriptor(fd);
        }
    }

    /// <summary>A wait on a descriptor that is not open fails; it is never reported as reached.</summary>
    /// <remarks>
    /// The shape of the defect this replaced: the old wait discarded every error but EINTR, so a
    /// failed read returned as though the point had been reached.
    /// </remarks>
    [TestMethod]
    public void AnEventfdWaitOnAClosedDescriptor_Fails()
    {
        RequireLinux();

        int fd = Descriptors.CreateEventfd();
        Descriptors.CloseDescriptor(fd);

        SyncWait wait = Descriptors.WaitEventfd(fd, TimeSpan.FromMilliseconds(200));

        Assert.AreEqual(SyncWaitOutcome.Failed, wait.Outcome);
        Assert.AreEqual(NativeLibc.EBADF, wait.Errno);
        Assert.AreNotEqual(
            0,
            Descriptors.SignalEventfd(fd),
            "a signal into a closed descriptor reported success"
        );
    }

    /// <summary>The kernel's own description separates an eventfd from any other descriptor.</summary>
    [TestMethod]
    public void AnEventfd_IsRecognisedAndNothingElseIs()
    {
        RequireLinux();

        int fd = Descriptors.CreateEventfd();
        using SafeFileHandle devNull = File.OpenHandle(
            "/dev/null",
            FileMode.Open,
            FileAccess.ReadWrite
        );
        try
        {
            Assert.IsTrue(Descriptors.IsEventfd(fd), "an eventfd was not recognised");
            Assert.IsFalse(
                Descriptors.IsEventfd((int)devNull.DangerousGetHandle()),
                "/dev/null was taken for an eventfd"
            );
            Assert.IsFalse(Descriptors.IsEventfd(-1));
        }
        finally
        {
            Descriptors.CloseDescriptor(fd);
        }
    }

    /// <summary>
    /// A sync descriptor that is neither a syncobj nor an eventfd fails the wait and yields no
    /// release promise.
    /// </summary>
    /// <remarks>
    /// The review finding this pins: a descriptor that did not import as a syncobj used to be read as
    /// an eventfd, the read's EINVAL was discarded, and the acquire point was reported reached - so a
    /// frame the GPU might still be writing was handed out. And a release promised on such a
    /// descriptor could never be sent, leaving the producer waiting on it.
    /// </remarks>
    [TestMethod]
    public void ASyncDescriptorOfNeitherKind_FailsTheWaitAndPromisesNoRelease()
    {
        RequireLinux();

        using SafeFileHandle devNull = File.OpenHandle(
            "/dev/null",
            FileMode.Open,
            FileAccess.ReadWrite
        );
        int fd = (int)devNull.DangerousGetHandle();

        Assert.AreEqual(
            SyncTimelineKind.Unknown,
            SyncTimeline.Classify(fd, out uint handle, out _)
        );
        Assert.AreEqual(0u, handle);

        SyncWait wait = SyncTimeline.Wait(fd, 1, TimeSpan.FromMilliseconds(200));
        Assert.AreEqual(
            SyncWaitOutcome.Failed,
            wait.Outcome,
            "a descriptor of neither kind was reported as reached"
        );
        Assert.AreNotEqual(0, wait.Errno, "the failure carries no errno to say why");

        Assert.IsFalse(
            SyncRelease.For(fd, 1).IsPending,
            "a release was promised on a descriptor nothing can signal"
        );
    }
}
