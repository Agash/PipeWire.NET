using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Media.Streams;

namespace PipeWire.NET.Tests;

/// <summary>
/// The threading premise every stream built on <c>PipeWireStreamCore</c> rests on.
/// </summary>
/// <remarks>
/// The video output's free-index stack and plane-layout table, and the capture's retained-frame
/// slots, are plain collections because <c>process</c> runs on the same loop thread as
/// <c>add_buffer</c> and <c>remove_buffer</c>. <c>PW_STREAM_FLAG_RT_PROCESS</c> would move
/// <c>process</c> to the data loop and turn every one of them into a race, so the core refuses it.
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class StreamCoreThreadingTests
{
    [TestMethod]
    public void RealtimeProcessing_IsRefusedForStreams()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => PipeWireStreamCore.RequireLoopThreadProcess(PipeWireStreamFlags.RtProcess | PipeWireStreamFlags.MapBuffers));
    }

    [TestMethod]
    public void TheFlagsTheStreamsUse_AreAccepted()
    {
        PipeWireStreamCore.RequireLoopThreadProcess(PipeWireStreamFlags.MapBuffers | PipeWireStreamFlags.Autoconnect);
        PipeWireStreamCore.RequireLoopThreadProcess(
            PipeWireStreamFlags.Autoconnect | PipeWireStreamFlags.MapBuffers | PipeWireStreamFlags.DontReconnect | PipeWireStreamFlags.Driver);
        PipeWireStreamCore.RequireLoopThreadProcess(PipeWireStreamFlags.Inactive | PipeWireStreamFlags.AllocBuffers);
    }
}
