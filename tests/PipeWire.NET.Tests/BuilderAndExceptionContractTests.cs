using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;

namespace PipeWire.NET.Tests;

/// <summary>
/// The builder surfaces, asserted on directly, and the exception contract a caller branches on.
/// </summary>
/// <remarks>
/// <para>
/// The audit put <c>StreamProperties</c>, <c>PipeWireNodeBuilder</c> and
/// <c>PipeWireLinkBuilder</c> at or near zero strongly-asserted members. They were not unused -
/// tests build objects with them constantly - but every assertion was made against the resulting
/// graph rather than against the builder. A <c>With...</c> that silently dropped its argument would
/// pass all of them, because the property it failed to set was never the property being checked.
/// </para>
/// <para>
/// Same for the exception types: four of them had a constructor listed in the public surface and no
/// test touching them. A caller branches on <c>Result</c> and <c>IsPermissionDenied</c> to decide
/// whether a refusal is fatal, and nothing checked those carried what was passed in.
/// </para>
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class BuilderAndExceptionContractTests
{
    // - StreamProperties -

    /// <summary>Each builder call lands under the property key PipeWire actually reads.</summary>
    /// <remarks>
    /// Checked against the generated key constants rather than literals, since those are what the
    /// builder writes; the round trip through the daemon is covered separately.
    /// </remarks>
    [TestMethod]
    public void EveryStreamPropertyBuilder_SetsTheKeyItNames()
    {
        IReadOnlyDictionary<string, string> props = new StreamProperties(
            StreamMediaType.Video,
            StreamCategory.Capture
        )
            .WithRole("Production")
            .WithTargetObject("some-node")
            .WithNodeName("my-name")
            .WithNodeDescription("my description")
            .With("custom.key", "custom-value")
            .Values;

        Assert.AreEqual("Production", props[PipeWireKeys.PW_KEY_MEDIA_ROLE]);
        Assert.AreEqual("some-node", props[PipeWireKeys.PW_KEY_TARGET_OBJECT]);
        Assert.AreEqual("my-name", props[PipeWireKeys.PW_KEY_NODE_NAME]);
        Assert.AreEqual("my description", props[PipeWireKeys.PW_KEY_NODE_DESCRIPTION]);
        Assert.AreEqual("custom-value", props["custom.key"]);

        // The constructor's own two, which nothing asserted either.
        Assert.AreEqual("Video", props[PipeWireKeys.PW_KEY_MEDIA_TYPE]);
        Assert.AreEqual("Capture", props[PipeWireKeys.PW_KEY_MEDIA_CATEGORY]);
    }

    /// <summary>The media type and category are the two the constructor fixes, in both spellings.</summary>
    [TestMethod]
    public void TheMediaTypeAndCategory_ReflectTheConstructorsArguments()
    {
        IReadOnlyDictionary<string, string> audio = new StreamProperties(
            StreamMediaType.Audio,
            StreamCategory.Playback
        ).Values;

        Assert.AreEqual("Audio", audio[PipeWireKeys.PW_KEY_MEDIA_TYPE]);
        Assert.AreEqual("Playback", audio[PipeWireKeys.PW_KEY_MEDIA_CATEGORY]);
    }

    /// <summary>A later call replaces an earlier one rather than accumulating duplicates.</summary>
    /// <remarks>
    /// A dictionary cannot hold a duplicate key, so the failure this guards against is the opposite:
    /// a builder that threw on the second call, which would make a caller reusing one instance
    /// crash rather than override.
    /// </remarks>
    [TestMethod]
    public void SettingAPropertyTwice_KeepsTheLastValue()
    {
        IReadOnlyDictionary<string, string> props = new StreamProperties(
            StreamMediaType.Audio,
            StreamCategory.Capture
        )
            .WithNodeName("first")
            .WithNodeName("second")
            .Values;

        Assert.AreEqual("second", props[PipeWireKeys.PW_KEY_NODE_NAME]);
    }

    // - Exceptions -

    /// <summary>
    /// Every exception type carries the result, object and operation it was given.
    /// </summary>
    /// <remarks>
    /// These are what a caller branches on. <c>-13</c> is <c>EACCES</c>, a refusal it may be able to
    /// recover from; a transport errno is not. An exception that dropped its result would present
    /// every failure as the same unrecoverable one.
    /// </remarks>
    [TestMethod]
    public void EveryPipeWireException_CarriesItsResultObjectAndOperation()
    {
        const int eacces = -13;
        const uint objectId = 42;

        foreach (
            PipeWireException ex in new PipeWireException[]
            {
                new PipeWireConnectFailedException("connect", eacces, objectId, "denied"),
                new PipeWireConnectionClosedException("closed", eacces, objectId, "denied"),
                new PipeWireInteropException("interop", eacces, objectId, "denied"),
                new PipeWireRequestRefusedException("refused", eacces, objectId, "denied"),
            }
        )
        {
            string which = ex.GetType().Name;

            Assert.AreEqual(eacces, ex.Result, $"{which} dropped its result");
            Assert.AreEqual(objectId, ex.ObjectId, $"{which} dropped its object id");
            Assert.AreEqual("denied", ex.DaemonMessage, $"{which} dropped the daemon's message");
            Assert.IsTrue(ex.IsPermissionDenied, $"{which} did not classify EACCES as a refusal");
            Assert.IsFalse(ex.IsDisconnected, $"{which} classified EACCES as a disconnection");
        }
    }

    /// <summary>The classifiers separate a refusal from a disconnection from a stale object.</summary>
    /// <remarks>
    /// The three a caller acts on differently: retry, reconnect, or forget the object. Getting one
    /// wrong turns a recoverable refusal into a teardown or the reverse.
    /// </remarks>
    [TestMethod]
    public void TheErrnoClassifiers_SeparateTheCasesACallerActsOnDifferently()
    {
        const int eacces = -13; // permission denied
        const int enoent = -2; // no such object
        const int epipe = -32; // the connection went away

        var refused = new PipeWireRequestRefusedException("op", eacces, null, null);
        Assert.IsTrue(refused.IsPermissionDenied);
        Assert.IsFalse(refused.IsObjectGone);
        Assert.IsFalse(refused.IsDisconnected);

        var gone = new PipeWireRequestRefusedException("op", enoent, null, null);
        Assert.IsTrue(gone.IsObjectGone);
        Assert.IsFalse(gone.IsPermissionDenied);

        var closed = new PipeWireConnectionClosedException("op", epipe, null, null);
        Assert.IsTrue(closed.IsDisconnected);
        Assert.IsFalse(closed.IsPermissionDenied);
    }

    /// <summary>The parameterless and message-only constructors do not throw or lose the message.</summary>
    /// <remarks>
    /// They exist because the exception design guidelines ask for them. Nothing constructed them,
    /// so nothing would have noticed one throwing on a null result it did not expect.
    /// </remarks>
    [TestMethod]
    public void TheConventionalConstructors_Work()
    {
        Assert.AreEqual("boom", new PipeWireInteropException("boom").Message);

        var inner = new InvalidOperationException("inner");
        var wrapped = new PipeWireConnectFailedException("outer", inner);
        Assert.AreSame(inner, wrapped.InnerException);

        // A default-constructed one has no result to report, and must not pretend otherwise.
        Assert.IsFalse(new PipeWireRequestRefusedException().IsPermissionDenied);
    }
}
