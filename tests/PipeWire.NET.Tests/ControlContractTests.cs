using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// The parts of the control-plane contract that hold without a daemon: argument validation, the
/// shape of the pods the typed helpers build, and how the models answer when asked for something
/// that is not there.
/// </summary>
/// <remarks>
/// Building the pod is separable from sending it, and it is the half that can be wrong silently -
/// a volume written under the wrong key, or into the wrong object type, is accepted by the daemon
/// and does nothing. These pin the bytes rather than the outcome.
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class ControlContractTests
{
    // ------------------------------------------------------------------ the pods the helpers build

    private static SpaObject Props(params SpaProperty[] properties) =>
        new(SpaType.ObjectProps, SpaParamType.Props, [.. properties]);

    [TestMethod]
    public void AVolumePod_IsAPropsObjectUnderTheVolumeKey()
    {
        // What SetVolumeAsync sends. Under a different object type or key the daemon accepts it and
        // silently does nothing, so the shape is the thing worth pinning.
        SpaObject pod = Props(new SpaProperty(SpaProp.Volume, 0, new SpaFloat(0.5f)));

        Assert.IsTrue(SpaPod.TryParse(SpaPod.ToBytes(pod), out SpaValue? read));
        var parsed = (SpaObject)read!;

        Assert.AreEqual(SpaType.ObjectProps, parsed.ObjectType);
        Assert.AreEqual(SpaParamType.Props, parsed.ObjectId);
        Assert.AreEqual(new SpaFloat(0.5f), parsed[SpaProp.Volume]);
    }

    [TestMethod]
    public void AChannelVolumePod_IsAnArrayOfFloatsNotAStructOfThem()
    {
        // The array's child type is what says how the daemon reads the values that follow. Declared
        // as anything else, the bytes are the same length and mean something different.
        var pod = Props(new SpaProperty(SpaProp.ChannelVolumes, 0,
            new SpaArray(SpaType.Float, [new SpaFloat(0.25f), new SpaFloat(0.75f)])));

        Assert.IsTrue(SpaPod.TryParse(SpaPod.ToBytes(pod), out SpaValue? read));
        var array = (SpaArray)((SpaObject)read!)[SpaProp.ChannelVolumes]!;

        Assert.AreEqual(SpaType.Float, array.ChildType);
        Assert.AreEqual(2, array.Items.Length);
        Assert.AreEqual(new SpaFloat(0.25f), array.Items[0]);
    }

    [TestMethod]
    public void ARoutePod_CarriesItsVolumeNestedInsideRatherThanBesideIt()
    {
        // A route says both which jack is selected and what its mixer is set to, and the mixer is a
        // Props object nested in the Route. Flattened, the daemon reads the volume as a route field
        // it does not have.
        var props = Props(
            new SpaProperty(SpaProp.Mute, 0, new SpaBool(false)),
            new SpaProperty(SpaProp.ChannelVolumes, 0, new SpaArray(SpaType.Float, [new SpaFloat(0.4f)])));

        var route = new SpaObject(SpaType.ObjectParamRoute, SpaParamType.Route,
        [
            new SpaProperty(SpaParamRoute.Index, 0, new SpaInt(2)),
            new SpaProperty(SpaParamRoute.Device, 0, new SpaInt(1)),
            new SpaProperty(SpaParamRoute.Props, 0, props),
            new SpaProperty(SpaParamRoute.Save, 0, new SpaBool(true)),
        ]);

        Assert.IsTrue(SpaPod.TryParse(SpaPod.ToBytes(route), out SpaValue? read));
        var parsed = (SpaObject)read!;

        Assert.AreEqual(SpaType.ObjectParamRoute, parsed.ObjectType);
        Assert.AreEqual(new SpaInt(2), parsed[SpaParamRoute.Index]);

        var nested = (SpaObject)parsed[SpaParamRoute.Props]!;
        Assert.AreEqual(SpaType.ObjectProps, nested.ObjectType,
            "the nested mixer must still be a Props object");
        Assert.AreEqual(new SpaBool(false), nested[SpaProp.Mute]);
    }

    [TestMethod]
    public void AProfilePod_CarriesOnlyTheIndex()
    {
        // Switching profile is one field. Sending more risks the daemon matching on something else.
        var pod = new SpaObject(SpaType.ObjectParamProfile, SpaParamType.Profile,
            [new SpaProperty(SpaParamProfile.Index, 0, new SpaInt(3))]);

        Assert.IsTrue(SpaPod.TryParse(SpaPod.ToBytes(pod), out SpaValue? read));
        var parsed = (SpaObject)read!;

        Assert.AreEqual(1, parsed.Properties.Length);
        Assert.AreEqual(new SpaInt(3), parsed[SpaParamProfile.Index]);
    }

    // ------------------------------------------------------------------ what the models answer

    [TestMethod]
    public void ParameterInfo_DistinguishesReadableFromWritable()
    {
        // A node advertises Props as both and Format as write-only. Collapsing the two into
        // "supported" would make enumerating Format look legal, and it is an error.
        var readable = new PipeWireParameterInfo(SpaParamType.Props, CanRead: true, CanWrite: true);
        var writeOnly = new PipeWireParameterInfo(SpaParamType.Format, CanRead: false, CanWrite: true);

        Assert.IsTrue(readable.CanRead && readable.CanWrite);
        Assert.IsFalse(writeOnly.CanRead);
        Assert.IsTrue(writeOnly.CanWrite);
        Assert.AreNotEqual(readable, writeOnly);
    }

    [TestMethod]
    public void ParameterInfoFlags_MatchWhatSpaDefines()
    {
        // Read is bit 1 and Write is bit 2, with bit 0 taken by Serial. Getting these confused would
        // report every parameter as readable, since Serial is set on many of them. Compared through
        // a table so the values are read at runtime rather than folded away by the compiler.
        (string Name, uint Actual, uint Expected)[] flags =
        [
            ("Serial", SpaParamInfoFlags.Serial, 1u << 0),
            ("Read", SpaParamInfoFlags.Read, 1u << 1),
            ("Write", SpaParamInfoFlags.Write, 1u << 2),
        ];

        foreach ((string name, uint actual, uint expected) in flags)
            Assert.AreEqual(expected, actual, $"SPA_PARAM_INFO_{name} is 0x{expected:x}");

        // And they are distinct bits, so a parameter marked Serial does not read as readable.
        uint combined = flags.Aggregate(0u, (acc, f) => acc | f.Actual);
        Assert.AreEqual(7u, combined, "the three flags must occupy three separate bits");
    }

    [TestMethod]
    public void APermissionEntry_IsAbsoluteAndCarriesTheObjectItIsAbout()
    {
        var confine = new PipeWireObjectPermission(PipeWireClientControl.AnyObject, PipeWirePermissions.None);
        var grant = new PipeWireObjectPermission(42, PipeWirePermissions.Read | PipeWirePermissions.Execute);

        Assert.AreEqual(uint.MaxValue, confine.ObjectId, "the catch-all id is the wildcard");
        Assert.AreEqual(PipeWirePermissions.None, confine.Permissions);
        Assert.IsTrue(grant.Permissions.HasFlag(PipeWirePermissions.Read));
        Assert.IsFalse(grant.Permissions.HasFlag(PipeWirePermissions.Write),
            "a permission set is exactly what was asked for, not a superset");
    }

    [TestMethod]
    public void PermissionBits_MatchPipeWiresOctalDefinitions()
    {
        // Upstream writes these in octal; a transcription slip here silently grants the wrong thing.
        // Through a table, so the comparison happens at run time rather than being folded away.
        (PipeWirePermissions Permission, int Expected)[] bits =
        [
            (PipeWirePermissions.Metadata, 0x008),
            (PipeWirePermissions.Link, 0x010),
            (PipeWirePermissions.Execute, 0x040),
            (PipeWirePermissions.Write, 0x080),
            (PipeWirePermissions.Read, 0x100),
        ];

        foreach ((PipeWirePermissions permission, int expected) in bits)
            Assert.AreEqual(expected, (int)permission, $"{permission} must be 0x{expected:x}");

        // Every one is a distinct bit, so combining them loses nothing.
        int all = bits.Aggregate(0, (acc, b) => acc | (int)b.Permission);
        Assert.AreEqual(bits.Sum(b => b.Expected), all, "the permission bits must not overlap");
    }

    // ------------------------------------------------------------------ metadata value handling

    [TestMethod]
    public void AMetadataEntryReportsRemovalAsANullValue_AndKeepsItsSubject()
    {
        var removal = new PipeWireMetadataEntry(PipeWireMetadataStore.SubjectCore, "default.audio.sink", null, null);

        Assert.IsNull(removal.Value, "a removal is a null value, not an empty string");
        Assert.IsNull(removal.NameValue);
        Assert.AreEqual(0u, removal.Subject, "the daemon-wide subject is zero");
    }

    [TestMethod]
    public void AJsonValueYieldsItsName_AndAnythingElseYieldsNothing()
    {
        // The daemon writes { "name": "..." } rather than a bare string, and writes whatever it likes
        // into other keys. Parsing must not throw on either.
        Assert.AreEqual("alsa_output.x",
            new PipeWireMetadataEntry(0, "k", "Spa:String:JSON", """{ "name": "alsa_output.x" }""").NameValue);

        foreach (string? junk in (string?[])
                 [null, "", " ", "not json", "{", "}", "[]", "[1,2]", "\"bare\"", "{\"other\":1}",
                  "{\"name\":42}", "{\"name\":null}", "{\"name\":{}}"])
        {
            Assert.IsNull(new PipeWireMetadataEntry(0, "k", null, junk).NameValue,
                $"'{junk ?? "null"}' must not yield a name");
        }
    }

    [TestMethod]
    public void ANameWithQuotesAndBackslashes_SurvivesTheJsonItIsWrappedIn()
    {
        // Node names are not sanitised anywhere, so a name containing the characters JSON uses to
        // delimit strings has to survive being written into one.
        foreach (string awkward in (string[])["a\"b", @"a\b", "a\"b\\c", "\\", "\"\"\""])
        {
            string json = $$"""{ "name": "{{awkward.Replace("\\", "\\\\", StringComparison.Ordinal)
                                                   .Replace("\"", "\\\"", StringComparison.Ordinal)}}" }""";

            Assert.AreEqual(awkward, new PipeWireMetadataEntry(0, "k", null, json).NameValue,
                $"'{awkward}' did not survive");
        }
    }

    // ------------------------------------------------------------------ snapshot answers

    [TestMethod]
    public void AnEmptySnapshot_AnswersEveryNewQueryWithoutThrowing()
    {
        // The graph before anything has been enumerated. Every accessor added for the new object
        // kinds has to cope with there being none of them.
        PipeWireGraphSnapshot graph = PipeWireGraphSnapshot.Empty;

        Assert.AreEqual(0, graph.Objects.Length);
        Assert.AreEqual(0, graph.Devices.Length);
        Assert.AreEqual(0, graph.Clients.Length);
        Assert.AreEqual(0, graph.Factories.Length);
        Assert.AreEqual(0, graph.Modules.Length);
        Assert.AreEqual(0, graph.MetadataStores.Length);
        Assert.IsNull(graph.Core);
        Assert.IsNull(graph.Profiler);
        Assert.IsNull(graph.SecurityContext);
        Assert.IsNull(graph.GetDevice(1));
        Assert.IsNull(graph.GetClient(1));
        Assert.IsNull(graph.GetFactory(1));
        Assert.IsNull(graph.GetModule(1));
        Assert.IsNull(graph.GetMetadataStore("default"));
        Assert.IsFalse(graph.TryGetObject(1, out _));
    }

    [TestMethod]
    public void TheTypedCollections_AreBuiltOnceAndReturnTheSameArray()
    {
        // Filtered on first read and kept, like the id indexes. Rebuilding per call would make a UI
        // that reads them each frame allocate for nothing.
        var device = new PipeWireDevice(10, PipeWirePermissions.None, 3,
            "card", null, null, "alsa", null, null, null, null);
        var graph = new PipeWireGraphSnapshot(1, [], [], [], [device]);

        ImmutableArray<PipeWireDevice> first = graph.Devices;
        ImmutableArray<PipeWireDevice> second = graph.Devices;

        Assert.AreEqual(1, first.Length);
        Assert.IsTrue(
            System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(first)
            == System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(second),
            "the filtered collection must be built once and kept");
    }

    [TestMethod]
    public void ObjectKindsAreDistinct_SoDispatchingOnThemCannotCollide()
    {
        // Each object reports its own kind, and no two share one - the whole point of Kind is
        // dispatching without a type test.
        IPipeWireObject[] objects =
        [
            new PipeWireDevice(1, PipeWirePermissions.None, 3, null, null, null, null, null, null, null, null),
            new PipeWireClient(2, PipeWirePermissions.None, 3, null, null, null, null, null, null, null),
            new PipeWireFactory(3, PipeWirePermissions.None, 3, null, null, null, null),
            new PipeWireModule(4, PipeWirePermissions.None, 3, null, null, null, null),
            new PipeWireMetadataObject(5, PipeWirePermissions.None, 3, null),
            new PipeWireProfiler(6, PipeWirePermissions.None, 3),
            new PipeWireSecurityContext(7, PipeWirePermissions.None, 3),
            new PipeWireCoreObject(8, PipeWirePermissions.None, 4, null, null, null, null),
        ];

        PipeWireObjectKind[] kinds = [.. objects.Select(o => o.Kind)];
        CollectionAssert.AllItemsAreUnique(kinds);

        foreach (IPipeWireObject o in objects)
            Assert.AreNotEqual(PipeWireObjectKind.Node, o.Kind, "none of these is a node");
    }

    [TestMethod]
    public void ManyThreadsReadingATypedCollectionAtOnce_AllSeeTheWholeThing()
    {
        // The lazy collections were guarded by "lock (gate ??= new object())", which is two
        // operations: two threads arriving together each made their own lock object and each locked
        // it, so both entered the section at once and could publish a half-filled array.
        var devices = Enumerable.Range(0, 50).Select(i =>
            new PipeWireDevice((uint)i, PipeWirePermissions.None, 3,
                $"dev{i}", null, null, "alsa", null, null, null, null));

        var snapshot = new PipeWireGraphSnapshot(1, [], [], [], devices);

        var faults = new System.Collections.Concurrent.ConcurrentQueue<string>();
        Parallel.For(0, 64, _ =>
        {
            ImmutableArray<PipeWireDevice> read = snapshot.Devices;
            if (read.Length != 50) faults.Enqueue($"saw {read.Length} devices");

            ImmutableArray<PipeWireClient> clients = snapshot.Clients;
            if (clients.Length != 0) faults.Enqueue($"saw {clients.Length} clients");
        });

        Assert.IsTrue(faults.IsEmpty, string.Join("; ", faults));
    }
}
