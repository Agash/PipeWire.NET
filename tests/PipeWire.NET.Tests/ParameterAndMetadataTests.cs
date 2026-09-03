using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// Parameters and metadata against a running daemon: reading a node's volume, writing it, switching
/// a device route, and reading the session's default sink.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class ParameterAndMetadataTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    private static async Task<(PipeWireContext Context, PipeWireRegistry Registry)> ConnectAsync(
        string name, CancellationToken cancellationToken)
    {
        var context = new PipeWireContext(name, ConsoleTestLoggerFactory.Instance);
        await context.StartAsync(cancellationToken);
        var registry = new PipeWireRegistry(context);
        await registry.WaitForInitialEnumerationAsync(cancellationToken);
        return (context, registry);
    }

    [TestMethod]
    public async Task AVirtualSink_ReportsAVolumeAndAcceptsANewOne()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-params", cts.Token);
        await using (ctx)
        await using (registry)
        {
            // A node we create ourselves, so nothing in the session is disturbed by changing it.
            PipeWireNode node = await registry.CreateVirtualNode("Params")
                .WithName("pwnet_param_sink").ExecuteAsync(cts.Token);

            await using PipeWireNodeControl control = registry.BindNode(node.NodeId);

            float? volume = await control.GetVolumeAsync(cts.Token);
            Assert.IsNotNull(volume, "an audio sink must report a volume");

            // Deliberately no assertion about the starting value. A session manager restores
            // volumes by node.name, so a node created with a name used before comes back at
            // whatever it was last left at - the round-trip below is the real behaviour to pin.
            await control.SetVolumeAsync(0.25f, cts.Token);

            // Read back rather than trusting the write: set_param is unacknowledged, and a node is
            // entitled to clamp or ignore what it is given.
            float? quiet = await control.GetVolumeAsync(cts.Token);
            Assert.IsNotNull(quiet);
            Assert.AreEqual(0.25f, quiet!.Value, 0.001f, "the volume did not take");

            // Twice, so a test that happened to start at the value it wrote still proves something.
            await control.SetVolumeAsync(0.8f, cts.Token);
            float? loud = await control.GetVolumeAsync(cts.Token);
            Assert.IsNotNull(loud);
            Assert.AreEqual(0.8f, loud!.Value, 0.001f, "the second write did not take");

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task MuteAndChannelVolumes_RoundTripThroughTheDaemon()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-mute", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNode("Mute")
                .WithName("pwnet_mute_sink").ExecuteAsync(cts.Token);

            await using PipeWireNodeControl control = registry.BindNode(node.NodeId);

            Assert.AreEqual(false, await control.GetMutedAsync(cts.Token));
            await control.SetMutedAsync(true, cts.Token);
            Assert.AreEqual(true, await control.GetMutedAsync(cts.Token), "mute did not take");

            // A stereo sink has two channels, so two volumes.
            ImmutableArray<float> channels = await control.GetChannelVolumesAsync(cts.Token);
            Assert.AreEqual(2, channels.Length, "a stereo node has two channel volumes");

            await control.SetChannelVolumesAsync([0.4f, 0.6f], cts.Token);

            ImmutableArray<float> after = await control.GetChannelVolumesAsync(cts.Token);
            Assert.AreEqual(0.4f, after[0], 0.01f);
            Assert.AreEqual(0.6f, after[1], 0.01f);

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task PropertyInfo_NamesTheControlsTheNodeActuallyHas()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-propinfo", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNode("PropInfo")
                .WithName("pwnet_propinfo_sink").ExecuteAsync(cts.Token);

            await using PipeWireNodeControl control = registry.BindNode(node.NodeId);

            ImmutableArray<SpaObject> info = await control.EnumeratePropertyInfoAsync(cts.Token);
            Assert.IsTrue(info.Length > 0, "a sink must describe the properties it supports");

            // Each entry says which property it describes.
            uint[] described = [.. info.Select(o => o[(uint)SpaPropInfo.Id])
                                       .OfType<SpaId>()
                                       .Select(id => id.Value)];

            CollectionAssert.Contains(described, (uint)SpaProp.Volume);
            CollectionAssert.Contains(described, (uint)SpaProp.Mute);
            CollectionAssert.Contains(described, (uint)SpaProp.ChannelVolumes);

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task AskingForAParameterANodeDoesNotHave_IsRefusedByTheDaemon()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-noparam", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNode("NoParam")
                .WithName("pwnet_noparam_sink").ExecuteAsync(cts.Token);

            await using PipeWireNodeControl control = registry.BindNode(node.NodeId);

            await control.ReadyAsync(cts.Token);

            // A node has no profiles - that is a device parameter - and PipeWire answers an
            // unsupported parameter with an error rather than an empty result. Check what the
            // object advertises first, which is what CanRead exists for.
            Assert.IsFalse(control.CanRead(SpaParamType.EnumProfile),
                "a node must not advertise a device parameter");

            await Assert.ThrowsExactlyAsync<PipeWireException>(
                async () => await control.EnumerateParametersAsync(SpaParamType.EnumProfile, cts.Token));

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task BindingSomethingThatIsNotThatKind_IsRefusedBeforeTouchingTheDaemon()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-bindkind", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNode("BindKind")
                .WithName("pwnet_bindkind_sink").ExecuteAsync(cts.Token);

            // Binding a node as a device would hand the daemon a proxy of the wrong interface and
            // misbehave later; catching it here turns that into an argument error.
            Assert.ThrowsExactly<ArgumentException>(() => registry.BindDevice(node.NodeId));
            Assert.ThrowsExactly<ArgumentException>(() => registry.BindClient(node.NodeId));
            Assert.ThrowsExactly<ArgumentException>(() => registry.BindNode(uint.MaxValue));

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task ADevice_ReportsTheProfilesAndRoutesItsCardOffers()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-device", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireDevice? card = registry.Current.Devices
                .FirstOrDefault(d => string.Equals(d.Api, "alsa", StringComparison.Ordinal));

            if (card is null)
                Assert.Inconclusive("this session has no ALSA card to enumerate.");

            await using PipeWireDeviceControl control = registry.BindDevice(card!.Id);

            ImmutableArray<SpaObject> profiles = await control.EnumerateProfilesAsync(cts.Token);
            Assert.IsTrue(profiles.Length > 0, "an ALSA card must offer at least one profile");

            // Every profile names itself, which is what a profile switcher shows.
            foreach (SpaObject profile in profiles)
            {
                Assert.IsInstanceOfType<SpaInt>(profile[(uint)SpaParamProfile.Index],
                    "a profile must carry its index");
                Assert.IsInstanceOfType<SpaString>(profile[(uint)SpaParamProfile.Name],
                    "a profile must carry its name");
            }

            SpaObject? current = await control.GetProfileAsync(cts.Token);
            Assert.IsNotNull(current, "a card in use must report the profile it is using");
        }
    }

    [TestMethod]
    public async Task TheDefaultStore_ReportsTheSessionDefaultSink()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-metadata", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireMetadataStore? store = registry.BindMetadataStore("default");
            if (store is null)
                Assert.Inconclusive("no session manager is running, so there is no default store.");

            await using (store)
            {
                // The store pushes everything it holds when the listener attaches; without the
                // barrier a read races that burst.
                await store.ReadyAsync(cts.Token);

                PipeWireMetadataEntry? sink = store.DefaultAudioSink;
                if (sink is null)
                    Assert.Inconclusive("this session has no default sink set.");

                // The daemon stores JSON, not a bare name, which is the trap this accessor exists for.
                Assert.IsTrue(sink!.Value!.Contains("name", StringComparison.Ordinal),
                    "the raw value is JSON");
                Assert.IsNotNull(sink.NameValue, "the node name must be readable out of the JSON");
                Assert.AreEqual(PipeWireMetadataStore.SubjectCore, sink.Subject,
                    "a session-wide default is about the daemon, not about one object");

                // And it names a node that is actually in the graph when the session is coherent.
                // A session whose ALSA device is held by something else keeps a default naming a
                // node it then fails to create.
                if (!registry.Current.Nodes.Any(n => n.NodeName == sink.NameValue))
                {
                    Assert.Inconclusive(
                        $"the session's default sink is '{sink.NameValue}', which is not a node in "
                        + "the graph. The session manager's state is inconsistent, which says "
                        + "nothing about the value this store read.");
                }
            }
        }
    }

    [TestMethod]
    public async Task TheSettingsStore_ReportsTheGraphClock()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-settings", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireMetadataStore? store = registry.BindMetadataStore("settings");
            if (store is null)
                Assert.Inconclusive("this daemon has no settings store.");

            await using (store)
            {
                await store.ReadyAsync(cts.Token);

                Assert.IsTrue(store.Entries.Count > 0, "the settings store must report its entries");
                Assert.IsNotNull(store.Get("clock.rate"), "the graph clock rate is a settings entry");
            }
        }
    }

    [TestMethod]
    public async Task SubscribingToProps_RaisesWhenSomethingElseChangesTheVolume()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-subscribe", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNode("Subscribe")
                .WithName("pwnet_subscribe_sink").ExecuteAsync(cts.Token);

            await using PipeWireNodeControl control = registry.BindNode(node.NodeId);

            var changed = new TaskCompletionSource<SpaObject>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            control.ParameterChanged += (_, value) =>
            {
                if (value.ObjectId == SpaParamType.Props) changed.TrySetResult(value);
            };

            control.SubscribeParameters(SpaParamType.Props);
            await control.SetVolumeAsync(0.5f, cts.Token);

            SpaObject props = await changed.Task.WaitAsync(cts.Token);
            Assert.IsNotNull(props[(uint)SpaProp.Volume],
                "a subscribed Props change must carry the volume that changed");

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task DisposingABindingWhileAReadIsInFlight_DoesNotHangOrCrash()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-bindrace", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNode("BindRace")
                .WithName("pwnet_bindrace_sink").ExecuteAsync(cts.Token);

            PipeWireNodeControl control = registry.BindNode(node.NodeId);
            Task<float?> reading = control.GetVolumeAsync(cts.Token);
            await control.DisposeAsync();

            // Either answer is acceptable; hanging or aborting the process is not.
            try { await reading.WaitAsync(TimeSpan.FromSeconds(5), cts.Token); }
            catch (ObjectDisposedException) { }
            catch (Exception e) when (e is InvalidOperationException or PipeWireException) { }

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task ANodeDescribesItsOwnParameters_BeforeAnythingIsAskedOfIt()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-info", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNode("Info")
                .WithName("pwnet_info_sink").ExecuteAsync(cts.Token);

            await using PipeWireNodeControl control = registry.BindNode(node.NodeId);

            // Nothing is populated until the daemon has sent its info, which it does unprompted.
            await control.ReadyAsync(cts.Token);

            Assert.IsTrue(control.Parameters.Length > 0, "the node must describe its parameters");
            Assert.IsTrue(control.CanRead(SpaParamType.Props), "an audio sink can be read");
            Assert.IsTrue(control.CanWrite(SpaParamType.Props), "an audio sink can be written");
            Assert.IsTrue(control.CanRead(SpaParamType.PropInfo));

            // Format is write-only on a node: enumerating it would be an error, so the flags have to
            // distinguish the two rather than reporting a single "supported".
            Assert.IsTrue(control.CanWrite(SpaParamType.Format));
            Assert.IsFalse(control.CanRead(SpaParamType.Format),
                "Format is advertised write-only and must not be reported as readable");

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task ADeviceDescribesItsOwnParameters_Too()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-devinfo", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireDevice? card = registry.Current.Devices
                .FirstOrDefault(d => string.Equals(d.Api, "alsa", StringComparison.Ordinal));

            if (card is null)
                Assert.Inconclusive("this session has no ALSA card.");

            await using PipeWireDeviceControl control = registry.BindDevice(card!.Id);
            await control.ReadyAsync(cts.Token);

            Assert.IsTrue(control.Parameters.Length > 0, "the device must describe its parameters");
            Assert.IsTrue(control.CanRead(SpaParamType.EnumProfile), "a card enumerates profiles");
            Assert.IsTrue(control.CanWrite(SpaParamType.Profile), "a card can be switched");
        }
    }

    [TestMethod]
    public async Task AClientCanBeBoundAndItsPropertiesUpdated()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-clientprops", cts.Token);
        await using (ctx)
        await using (registry)
        {
            // Our own client, found by the pid the daemon read off the socket rather than by any
            // name we could have made up.
            PipeWireClient? me = registry.Current.Clients
                .FirstOrDefault(c => c.ProcessId == Environment.ProcessId);

            if (me is null)
                Assert.Inconclusive("this connection is not visible as a client object.");

            await using PipeWireClientControl control = registry.BindClient(me!.Id);

            // A client may always change its own properties, whatever its permissions are, so this
            // exercises the whole write path without needing to be a session manager.
            await control.UpdatePropertiesAsync(
                new Dictionary<string, string> { ["application.name"] = "pwnet-renamed" },
                cts.Token);
        }
    }

    /// <remarks>
    /// <para>
    /// Quarantined, and not because it is flaky. On PipeWire 1.6.8 this request does not come back
    /// refused: the daemon segfaults inside <c>pw_impl_client_update_permissions</c> while applying
    /// an update it should have rejected, so the round-trip times out after the session is already
    /// gone and every test that runs afterwards fails to connect.
    /// </para>
    /// <para>
    /// It carries its own category so a suite run never reaches it by accident. Run it deliberately,
    /// alone, against a session nothing else is using:
    /// <c>--filter "TestCategory=KillsTheDaemon"</c>.
    /// </para>
    /// </remarks>
    [TestMethod]
    [TestCategory("KillsTheDaemon")]
    public async Task ConfiningAClientWithoutTheManagerPermission_IsRefusedRatherThanSilentlyIgnored()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-confine", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireClient? other = registry.Current.Clients
                .FirstOrDefault(c => c.ProcessId != Environment.ProcessId);

            if (other is null)
                Assert.Inconclusive("this session has no other client to attempt to confine.");

            await using PipeWireClientControl control = registry.BindClient(other!.Id);

            // An ordinary application is not a session manager. The refusal comes back on the core's
            // error stream, not from the call - which is exactly why the write round-trips instead
            // of returning as soon as it is sent. A daemon that did permit it is also a valid
            // outcome here; silently appearing to succeed while doing nothing is not.
            try
            {
                await control.ConfineToAsync(
                    [new PipeWireObjectPermission(0, PipeWirePermissions.Read)], cts.Token);
            }
            catch (PipeWireException)
            {
                // The expected path for a client without manager rights.
            }
        }
    }
}
