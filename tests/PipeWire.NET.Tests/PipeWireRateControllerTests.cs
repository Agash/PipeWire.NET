using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Media;

namespace PipeWire.NET.Tests;

/// <summary>
/// The rate controller is arithmetic, so it is tested as arithmetic - no daemon, no graph. What
/// matters is that it converges rather than oscillates, because a transport that overcorrects
/// sounds worse than one that does not correct at all.
/// </summary>
[TestClass]
public sealed class PipeWireRateControllerTests
{
    private static PipeWireRateController Tuned(double bandwidth = PipeWireRateController.MinBandwidth)
    {
        var dll = new PipeWireRateController();
        dll.SetBandwidth(bandwidth, period: 1024, rate: 48000);
        return dll;
    }

    [TestMethod]
    public void AFreshController_AsksForNoCorrection()
    {
        // Nothing has been observed yet, so the only honest answer is "carry on".
        Assert.AreEqual(1.0, Tuned().Update(0.0), 1e-12);
    }

    [TestMethod]
    public void AQueueThatIsNeverWrong_IsNeverCorrected()
    {
        PipeWireRateController dll = Tuned();
        for (int i = 0; i < 1000; i++)
            Assert.AreEqual(1.0, dll.Update(0.0), 1e-12, $"drifted at cycle {i} with nothing to correct");
    }

    /// <summary>
    /// Closed loop, the way a receiver actually runs it: the correction is fed back into the rate
    /// samples are consumed at, so the queue it is measuring moves in response. Modelled on
    /// module-rtp's audio path, which computes <c>error = target - avail</c>, calls the DLL, and
    /// applies the result with <c>pw_stream_set_rate</c>.
    /// </summary>
    [TestMethod]
    public void AQueueThatStartsShort_IsDrivenBackToTarget()
    {
        PipeWireRateController dll = Tuned();

        const double target = 4800.0;
        const double period = 480.0;
        double avail = 4000.0;      // starts short by more than a period
        double correction = 1.0;

        for (int i = 0; i < 20000; i++)
        {
            correction = dll.Update(target - avail);

            // Produced at the nominal rate, consumed at the corrected one. A correction below 1
            // consumes less than arrives, so a short queue refills.
            avail += period - (period * correction);
        }

        Assert.AreEqual(target, avail, 1.0, "the queue never reached its target");
        Assert.AreEqual(1.0, correction, 1e-3, "the loop settled on a standing rate correction");
    }

    /// <summary>
    /// The same loop from the other side: a queue that starts overfull is drained back down. Both
    /// directions matter, because a sign error converges from one side and diverges from the other.
    /// </summary>
    [TestMethod]
    public void AQueueThatStartsLong_IsDrainedBackToTarget()
    {
        PipeWireRateController dll = Tuned();

        const double target = 4800.0;
        const double period = 480.0;
        double avail = 5600.0;

        for (int i = 0; i < 20000; i++)
        {
            avail += period - (period * dll.Update(target - avail));
        }

        Assert.AreEqual(target, avail, 1.0, "the queue never came back down to its target");
    }

    /// <summary>
    /// Open loop, a constant error forever, which is not a scenario the DLL is meant to settle in:
    /// its third stage is an integrator, so an error that never responds to the correction winds it
    /// up without bound. Asserting convergence here would be asserting the filter is broken. What is
    /// worth pinning is that it winds up smoothly in one direction rather than ringing.
    /// </summary>
    [TestMethod]
    public void ASustainedUncorrectedError_WindsUpMonotonicallyWithoutRinging()
    {
        PipeWireRateController dll = Tuned();

        double previous = dll.Update(64.0);
        double biggestStep = 0.0;

        for (int i = 0; i < 2000; i++)
        {
            double next = dll.Update(64.0);
            Assert.IsTrue(next <= previous, $"the correction reversed direction at cycle {i}");
            biggestStep = Math.Max(biggestStep, Math.Abs(next - previous));
            previous = next;
        }

        Assert.IsTrue(biggestStep < 1.0, $"a single cycle moved the correction by {biggestStep}");
    }

    [TestMethod]
    public void TheCorrectionOpposesTheError()
    {
        // The sign is the whole point: a queue holding too much must be drained, not filled.
        PipeWireRateController tooFull = Tuned();
        PipeWireRateController tooEmpty = Tuned();

        double full = 1.0, empty = 1.0;
        for (int i = 0; i < 500; i++)
        {
            full = tooFull.Update(64.0);
            empty = tooEmpty.Update(-64.0);
        }

        Assert.AreNotEqual(1.0, full, 1e-9, "a queue that is too long must produce a correction");
        Assert.IsTrue(
            (full < 1.0 && empty > 1.0) || (full > 1.0 && empty < 1.0),
            $"opposite errors must correct in opposite directions, got {full} and {empty}");
    }

    [TestMethod]
    public void AWiderBandwidth_ConvergesFaster()
    {
        // The reason both constants exist: wide follows the error quickly, narrow ignores noise.
        PipeWireRateController narrow = Tuned(PipeWireRateController.MinBandwidth);
        PipeWireRateController wide = Tuned(PipeWireRateController.MaxBandwidth);

        double narrowMoved = 0.0, wideMoved = 0.0;
        for (int i = 0; i < 50; i++)
        {
            narrowMoved = Math.Abs(1.0 - narrow.Update(64.0));
            wideMoved = Math.Abs(1.0 - wide.Update(64.0));
        }

        Assert.IsTrue(wideMoved > narrowMoved,
            $"the wider bandwidth should have moved further after 50 cycles: {wideMoved} vs {narrowMoved}");
    }

    [TestMethod]
    public void Reset_ForgetsWhatItLearned()
    {
        PipeWireRateController dll = Tuned();
        for (int i = 0; i < 200; i++) dll.Update(64.0);

        Assert.AreNotEqual(1.0, dll.Update(64.0), 1e-9);

        dll.Reset();
        Assert.AreEqual(1.0, dll.Update(0.0), 1e-12, "a reset controller must start from nothing again");
    }

    [TestMethod]
    public void APeriodOrRateOfZero_IsRefused()
    {
        var dll = new PipeWireRateController();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => dll.SetBandwidth(0.05, 0, 48000));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => dll.SetBandwidth(0.05, 1024, 0));
    }
}
