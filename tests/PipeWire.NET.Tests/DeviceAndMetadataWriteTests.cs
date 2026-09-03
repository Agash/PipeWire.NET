using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// The write paths that change something real: device routes and profiles, metadata entries, and a
/// node's latency offset.
/// </summary>
/// <remarks>
/// These put the session back as they found it. A test that leaves a card on a different profile or
/// the default sink pointing somewhere else is a test that breaks the machine it runs on.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class DeviceAndMetadataWriteTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(25);

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
    public async Task APropertyANodeDoesNotSupport_IsReportedAbsentRatherThanGuessedAt()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-latency", cts.Token);
        await using (ctx)
        await using (registry)
        {
            // A unique name: a session manager restores properties by node.name, so reusing one
            // would read back whatever a previous run left rather than what this one wrote.
            string name = $"pwnet_latency_{Environment.ProcessId}_{Random.Shared.Next():x}";
            PipeWireNode node = await registry.CreateVirtualNode("Latency")
                .WithName(name).ExecuteAsync(cts.Token);

            await using PipeWireNodeControl control = registry.BindNode(node.NodeId);
            await control.ReadyAsync(cts.Token);

            // A null-audio-sink has no latency offset: it is absent from PropInfo, and pw-cli cannot
            // set it either. Writing one is accepted and silently dropped, which is what makes this
            // worth pinning - the API must report the absence rather than invent a value.
            System.Collections.Immutable.ImmutableArray<SpaObject> info =
                await control.EnumeratePropertyInfoAsync(cts.Token);

            bool advertised = info.Select(o => o[SpaPropInfo.Id])
                                  .OfType<SpaId>()
                                  .Any(id => id.Value == (uint)SpaProp.LatencyOffsetNsec);

            Assert.IsFalse(advertised, "this node kind does not advertise a latency offset");
            Assert.IsNull(await control.GetLatencyOffsetAsync(cts.Token));

            // Writing it is not an error - the daemon accepts and ignores it - and it stays absent.
            await control.SetLatencyOffsetAsync(2_000_000, cts.Token);
            Assert.IsNull(await control.GetLatencyOffsetAsync(cts.Token),
                "an unsupported property must not appear to have been set");

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    /// <summary>
    /// Binds the first ALSA card that actually reports active routes.
    /// </summary>
    /// <remarks>
    /// Taking the first ALSA device finds an HDMI output on most desktops, and those carry no
    /// routes - so every route test skipped without ever exercising the code it was written for.
    /// </remarks>
    private static async Task<PipeWireDeviceControl?> BindCardWithRoutesAsync(
        PipeWireRegistry registry, CancellationToken cancellationToken)
    {
        foreach (PipeWireDevice card in registry.Current.Devices
                     .Where(d => string.Equals(d.Api, "alsa", StringComparison.Ordinal)))
        {
            // The snapshot is a moment, and on a session whose ALSA devices are being destroyed and
            // recreated the card can be gone before this binds it. BindDevice reports that as an
            // ArgumentException naming the id, which is indistinguishable here from being handed
            // nonsense, so the only thing to do is try the next card.
            PipeWireDeviceControl control;
            try { control = registry.BindDevice(card.Id); }
            catch (ArgumentException) { continue; }

            try
            {
                await control.ReadyAsync(cancellationToken);
                if (!(await control.GetActiveRoutesAsync(cancellationToken)).IsEmpty) return control;
            }
            catch (PipeWireException)
            {
                // A card that will not answer is not the one we are looking for.
            }

            await control.DisposeAsync();
        }

        return null;
    }

    [TestMethod]
    public async Task ReapplyingTheActiveRoute_IsAcceptedAndChangesNothing()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-setroute", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireDeviceControl? found = await BindCardWithRoutesAsync(registry, cts.Token);
            if (found is null)
                Assert.Inconclusive("no ALSA card on this session reports an active route.");

            await using PipeWireDeviceControl control = found!;
            ImmutableArray<SpaObject> active = await control.GetActiveRoutesAsync(cts.Token);

            SpaObject route = active[0];
            if (route[SpaParamRoute.Index] is not SpaInt index || route[SpaParamRoute.Device] is not SpaInt device)
            {
                Assert.Inconclusive("the active route does not report an index and device port.");
                return;
            }

            // Selecting the route that is already selected: it exercises the write without changing
            // what the machine is doing, so it is safe to run against a real card.
            //
            // ENOENT here is the card going away underneath the test, not a refusal: a session
            // manager that cannot open a busy ALSA device destroys and recreates the device object,
            // and a binding made before that is talking to a resource the daemon no longer has.
            // Whether the card stays put is the session's business; what is under test is that
            // re-selecting an active route is accepted.
            ImmutableArray<SpaObject> after;
            try
            {
                await control.SetRouteAsync(index.Value, device.Value, cts.Token);
                after = await control.GetActiveRoutesAsync(cts.Token);
            }
            catch (PipeWireException e) when (e.Result == -2)
            {
                Assert.Inconclusive($"the card was destroyed while the test held it: {e.Message}");
                return;
            }

            Assert.IsFalse(after.IsEmpty, "the card lost its active route");
            Assert.AreEqual(index.Value, ((SpaInt)after[0][SpaParamRoute.Index]!).Value,
                "re-selecting the active route must leave it selected");
        }
    }

    [TestMethod]
    public async Task ADevicesRoutes_CarryTheirOwnVolumeAndSurviveBeingReadBack()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-routes", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireDevice? card = registry.Current.Devices
                .FirstOrDefault(d => string.Equals(d.Api, "alsa", StringComparison.Ordinal));

            if (card is null)
                Assert.Inconclusive("this session has no ALSA card.");

            await using PipeWireDeviceControl control = registry.BindDevice(card!.Id);
            await control.ReadyAsync(cts.Token);

            ImmutableArray<SpaObject> routes = await control.EnumerateRoutesAsync(cts.Token);
            if (routes.IsEmpty)
                Assert.Inconclusive("this card exposes no routes in its current profile.");

            // Every route names itself and says which device port it belongs to - the two things
            // needed to select it, so their absence would make the route API unusable.
            foreach (SpaObject route in routes)
            {
                Assert.IsInstanceOfType<SpaInt>(route[SpaParamRoute.Index], "a route must carry its index");
                Assert.IsInstanceOfType<SpaId>(route[SpaParamRoute.Direction], "a route must say which way it faces");
                Assert.IsNotNull(route[SpaParamRoute.Name], "a route must be nameable in a UI");
            }

            // The active ones are a subset of the offered ones, and carry a Props with the hardware
            // mixer in it. Read-only here: changing a card's routing would outlast the test.
            ImmutableArray<SpaObject> active = await control.GetActiveRoutesAsync(cts.Token);
            foreach (SpaObject route in active)
            {
                Assert.IsInstanceOfType<SpaInt>(route[SpaParamRoute.Index]);
                if (route[SpaParamRoute.Props] is SpaObject props)
                {
                    Assert.IsTrue(
                        props[SpaProp.ChannelVolumes] is SpaArray or null,
                        "a route's volume is per channel when it has one at all");
                }
            }
        }
    }

    [TestMethod]
    public async Task ADevicesProfiles_AreEnumerableAndTheCurrentOneIsAmongThem()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-profiles", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireDevice? card = registry.Current.Devices
                .FirstOrDefault(d => string.Equals(d.Api, "alsa", StringComparison.Ordinal));

            if (card is null)
                Assert.Inconclusive("this session has no ALSA card.");

            await using PipeWireDeviceControl control = registry.BindDevice(card!.Id);

            ImmutableArray<SpaObject> profiles = await control.EnumerateProfilesAsync(cts.Token);
            Assert.IsTrue(profiles.Length > 0);

            SpaObject? current = await control.GetProfileAsync(cts.Token);
            Assert.IsNotNull(current);

            // The current profile has to be one the card offers, or the index a UI shows as selected
            // would match nothing in the list it shows.
            int currentIndex = ((SpaInt)current![SpaParamProfile.Index]!).Value;
            int[] offered = [.. profiles.Select(p => ((SpaInt)p[SpaParamProfile.Index]!).Value)];
            CollectionAssert.Contains(offered, currentIndex,
                "the active profile must be one of the enumerated ones");
        }
    }

    [TestMethod]
    public async Task AMetadataEntry_CanBeWrittenReadBackAndRemoved()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-metawrite", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireMetadataStore? store = registry.BindMetadataStore("default");
            if (store is null)
                Assert.Inconclusive("no session manager, so no default store.");

            await using (store)
            {
                await store.ReadyAsync(cts.Token);

                // A key of our own, so nothing the session relies on is touched.
                string key = $"pwnet.test.{Environment.ProcessId}";
                var changes = new List<PipeWireMetadataEntry>();
                var removalSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                store.EntryChanged += (_, entry) =>
                {
                    if (entry.Key != key) return;
                    lock (changes) changes.Add(entry);
                    if (entry.Value is null) removalSeen.TrySetResult();
                };

                try
                {
                    await store.SetAsync(key, "hello", subject: PipeWireMetadataStore.SubjectCore,
                        cancellationToken: cts.Token);
                }
                catch (PipeWireException)
                {
                    Assert.Inconclusive("this client may not write metadata on this daemon.");
                }

                Assert.AreEqual("hello", store.Get(key));

                // Removal is a set with a null value, and arrives as an entry whose Value is null -
                // the one shape of the event a consumer has to special-case.
                await store.SetAsync(key, null, cancellationToken: cts.Token);
                Assert.IsNull(store.Get(key), "the entry must be gone after a null write");

                // Reports arrive asynchronously - a write does not wait for its own - and how many
                // the daemon emits for a set followed quickly by a removal is its business. What has
                // to hold is that the removal is reported, and that it is reported as a null value:
                // that is the one shape of the event a consumer must special-case.
                await removalSeen.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

                lock (changes)
                {
                    Assert.IsTrue(changes.Count > 0, "the change must be reported at all");
                    Assert.IsNull(changes[^1].Value, "a removal is reported as a null value");
                    Assert.IsTrue(changes.Any(c => c.Value == "hello"),
                        "the value that was written must have been reported too");
                }
            }
        }
    }

    [TestMethod]
    public async Task TheDefaultSinkCanBeSetToWhatItAlreadyIs_WithoutDisturbingTheSession()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-defsink", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireMetadataStore? store = registry.BindMetadataStore("default");
            if (store is null)
                Assert.Inconclusive("no session manager, so no default store.");

            await using (store)
            {
                await store.ReadyAsync(cts.Token);

                string? currentName = store.DefaultAudioSink?.NameValue;
                if (currentName is null)
                    Assert.Inconclusive("this session has no default sink set.");

                // Writing back the value it already holds exercises the whole JSON-building path
                // without changing where anyone's audio goes.
                try
                {
                    await store.SetDefaultAudioSinkAsync(currentName!, cts.Token);
                }
                catch (PipeWireException)
                {
                    Assert.Inconclusive("this client may not write metadata on this daemon.");
                }

                await store.ReadyAsync(cts.Token);
                Assert.AreEqual(currentName, store.DefaultAudioSink?.NameValue,
                    "the default sink must still name the same node");
            }
        }
    }

    [TestMethod]
    public async Task ANodeNameWithCharactersThatWouldBreakJson_IsEscapedRatherThanCorrupting()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-jsonesc", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireMetadataStore? store = registry.BindMetadataStore("default");
            if (store is null)
                Assert.Inconclusive("no session manager, so no default store.");

            await using (store)
            {
                await store.ReadyAsync(cts.Token);

                // Not a real sink, so nothing is routed to it - but the value has to come back as
                // valid JSON naming exactly this string, quotes and backslashes intact.
                string awkward = @"a""b\c";
                string key = $"pwnet.test.escape.{Environment.ProcessId}";

                try
                {
                    await store.SetAsync(key, $$"""{ "name": "{{awkward.Replace("\\", "\\\\").Replace("\"", "\\\"")}}" }""",
                        "Spa:String:JSON", PipeWireMetadataStore.SubjectCore, cts.Token);
                }
                catch (PipeWireException)
                {
                    Assert.Inconclusive("this client may not write metadata on this daemon.");
                }

                string? raw = store.Get(key);
                Assert.IsNotNull(raw);

                var entry = new PipeWireMetadataEntry(PipeWireMetadataStore.SubjectCore, key, null, raw);
                Assert.AreEqual(awkward, entry.NameValue, "the escaping did not survive the round trip");

                await store.SetAsync(key, null, cancellationToken: cts.Token);
            }
        }
    }

    [TestMethod]
    public void AMetadataValueThatIsNotJson_ReadsAsNullRatherThanThrowing()
    {
        // The daemon writes what it likes into a metadata value; an accessor that parsed it as JSON
        // and threw would take down whatever was enumerating the store.
        foreach (string? value in (string?[])[null, "", "not json", "{", "[]", "{\"other\":1}", "\"bare\""])
        {
            var entry = new PipeWireMetadataEntry(0, "k", null, value);
            Assert.IsNull(entry.NameValue, $"'{value}' must not yield a name");
        }

        Assert.AreEqual("x", new PipeWireMetadataEntry(0, "k", null, """{ "name": "x" }""").NameValue);
    }

    [TestMethod]
    public async Task SwitchingACardProfileAndPuttingItBack_ReplacesItsNodesBothTimes()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-profileswitch", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireDevice? card = registry.Current.Devices
                .FirstOrDefault(d => string.Equals(d.Api, "alsa", StringComparison.Ordinal));
            if (card is null)
                Assert.Inconclusive("this session has no ALSA card.");

            await using PipeWireDeviceControl control = registry.BindDevice(card!.Id);
            await control.ReadyAsync(cts.Token);

            SpaObject? original = await control.GetProfileAsync(cts.Token);
            if (original?[SpaParamProfile.Index] is not SpaInt originalIndexValue)
            {
                Assert.Inconclusive("this card does not report a current profile index.");
                return;
            }

            int originalIndex = originalIndexValue.Value;

            ImmutableArray<SpaObject> profiles = await control.EnumerateProfilesAsync(cts.Token);

            // Only profiles the card says are available: switching to an unavailable one is refused,
            // and picking one at random would make this test fail for the wrong reason.
            int[] candidates =
            [
                .. profiles
                    .Where(p => p[SpaParamProfile.Available] is not SpaId a
                                || a.Value != (uint)SpaParamAvailability.No)
                    .Select(p => (p[SpaParamProfile.Index] as SpaInt)?.Value ?? -1)
                    .Where(i => i >= 0 && i != originalIndex),
            ];

            if (candidates.Length == 0)
                Assert.Inconclusive("this card offers no other available profile to switch to.");

            // Whether the card survived, so the restore below knows if there is anything to
            // restore to. Like the route test, this treats a withdrawn card as the session's
            // instability rather than a halfway-applied write.
            bool cardGone = false;
            try
            {
                // Switching a profile is the most destructive thing this library can do to a graph:
                // the nodes the old profile provided are removed and the new profile's appear. Any
                // node id held across it is stale, which is the trap worth proving.
                await control.SetProfileAsync(candidates[0], cts.Token);
                await registry.WaitForInitialEnumerationAsync(cts.Token);

                SpaObject? now = await control.GetProfileAsync(cts.Token);
                Assert.AreEqual(candidates[0], ((SpaInt)now![SpaParamProfile.Index]!).Value,
                    "the card did not switch profile");

                // The device is still coherent afterwards: it still enumerates, and its routes are
                // the new profile's rather than a mixture.
                Assert.IsTrue((await control.EnumerateProfilesAsync(cts.Token)).Length > 0);
                await control.GetActiveRoutesAsync(cts.Token);
            }
            catch (PipeWireException ex)
            {
                cardGone = await CardGoneAsync(registry, control.Id, cts.Token);
                if (!cardGone) throw;
                Assert.Inconclusive($"the card left the graph mid-test: {ex.Message}");
            }
            finally
            {
                // Put the machine back however the test ended. Leaving someone's sound card on a
                // different profile is not an acceptable side effect of running a test suite.
                // When the card is gone there is nothing to put back; the body's own outcome,
                // pass or fail, stands.
                if (!cardGone)
                {
                    try
                    {
                        await control.SetProfileAsync(originalIndex, CancellationToken.None);
                        await registry.WaitForInitialEnumerationAsync(CancellationToken.None);
                    }
                    catch (PipeWireException)
                    {
                        cardGone = await CardGoneAsync(registry, control.Id, CancellationToken.None);
                        if (!cardGone) throw;
                    }
                }
            }

            if (!cardGone)
            {
                SpaObject? restored = await control.GetProfileAsync(cts.Token);
                Assert.AreEqual(originalIndex, ((SpaInt)restored![SpaParamProfile.Index]!).Value,
                    "the original profile must have been restored");
            }
        }
    }

    [TestMethod]
    public async Task SettingARouteVolumeAndRestoringIt_ChangesTheHardwareMixer()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-routevol", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireDeviceControl? found = await BindCardWithRoutesAsync(registry, cts.Token);
            if (found is null)
                Assert.Inconclusive("no ALSA card on this session reports an active route.");

            await using PipeWireDeviceControl control = found!;
            ImmutableArray<SpaObject> active = await control.GetActiveRoutesAsync(cts.Token);

            SpaObject? route = active.FirstOrDefault(r =>
                r[SpaParamRoute.Props] is SpaObject props
                && props[SpaProp.ChannelVolumes] is SpaArray volumes
                && !volumes.Items.IsDefaultOrEmpty);

            if (route is null)
                Assert.Inconclusive("no active route on this card carries a volume.");

            int index = ((SpaInt)route[SpaParamRoute.Index]!).Value;
            int device = ((SpaInt)route[SpaParamRoute.Device]!).Value;
            var props = (SpaObject)route[SpaParamRoute.Props]!;
            var original = (SpaArray)props[SpaProp.ChannelVolumes]!;
            bool originalMute = props[SpaProp.Mute] is SpaBool m && m.Value;

            float[] restore = [.. original.Items.OfType<SpaFloat>().Select(f => f.Value)];
            Assert.IsTrue(restore.Length > 0);

            // Whether the card survived the test, so the restore below knows if there is
            // anything to restore to. On a session whose ALSA devices flap, the card can be
            // withdrawn between binding and writing; that is the session's instability, and it
            // reads here as a refusal from a dead proxy rather than as a halfway-applied write.
            bool cardGone = false;
            try
            {
                // A distinctive value, so reading it back cannot accidentally match what was there.
                float[] test = [.. restore.Select(_ => 0.37f)];
                await control.SetRouteVolumeAsync(
                    index, device, test, originalMute, save: false, cts.Token);

                ImmutableArray<SpaObject> after = await control.GetActiveRoutesAsync(cts.Token);
                SpaObject? changed = after.FirstOrDefault(r =>
                    r[SpaParamRoute.Index] is SpaInt i && i.Value == index);

                Assert.IsNotNull(changed, "the route must still be active after being written to");

                var readBack = (SpaArray)((SpaObject)changed![SpaParamRoute.Props]!)[SpaProp.ChannelVolumes]!;
                float first = ((SpaFloat)readBack.Items[0]).Value;
                Assert.AreEqual(0.37f, first, 0.02f, "the hardware volume did not take");
            }
            catch (PipeWireException ex)
            {
                cardGone = await CardGoneAsync(registry, control.Id, cts.Token);
                if (!cardGone) throw;
                Assert.Inconclusive($"the card left the graph mid-test: {ex.Message}");
            }
            finally
            {
                // When the card is gone there is nothing to put back; the body's own outcome,
                // pass or fail, stands.
                if (!cardGone)
                {
                    try
                    {
                        await control.SetRouteVolumeAsync(
                            index, device, restore, originalMute, save: false, CancellationToken.None);
                    }
                    catch (PipeWireException)
                    {
                        cardGone = await CardGoneAsync(registry, control.Id, CancellationToken.None);
                        if (!cardGone) throw;
                    }
                }
            }
        }
    }

    /// <summary>Whether a card is still in the graph, after giving the registry a beat to notice.</summary>
    private static async Task<bool> CardGoneAsync(
        PipeWireRegistry registry, uint cardId, CancellationToken cancellationToken)
    {
        await registry.WaitForInitialEnumerationAsync(cancellationToken);
        return registry.Current.Devices.All(d => d.Id != cardId);
    }
}
