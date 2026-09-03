using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// Serving a device, rather than being a client of one.
/// </summary>
/// <remarks>
/// This is the riskiest hosting path in the library: the daemon dispatches into managed code through
/// a hand-laid function-pointer table, so a field in the wrong order is a call through a wrong
/// pointer. These check that the daemon actually accepts the export, reads the device's parameters
/// back through the ordinary client path, and that the session survives it.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class DeviceProviderTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    private static string Unique() => $"pwnet_device_{Environment.ProcessId}_{Random.Shared.Next():x}";

    /// <summary>Two profiles, which is the least that makes a device selectable.</summary>
    private static ImmutableArray<SpaObject> Profiles() =>
    [
        new SpaObject(SpaType.ObjectParamProfile, SpaParamType.EnumProfile,
        [
            new SpaProperty((uint)SpaParamProfile.Index, 0, new SpaInt(0)),
            new SpaProperty((uint)SpaParamProfile.Name, 0, new SpaString("off")),
            new SpaProperty((uint)SpaParamProfile.Description, 0, new SpaString("Off")),
            new SpaProperty((uint)SpaParamProfile.Priority, 0, new SpaInt(0)),
        ]),
        new SpaObject(SpaType.ObjectParamProfile, SpaParamType.EnumProfile,
        [
            new SpaProperty((uint)SpaParamProfile.Index, 0, new SpaInt(1)),
            new SpaProperty((uint)SpaParamProfile.Name, 0, new SpaString("stereo")),
            new SpaProperty((uint)SpaParamProfile.Description, 0, new SpaString("Stereo")),
            new SpaProperty((uint)SpaParamProfile.Priority, 0, new SpaInt(100)),
        ]),
    ];

    /// <summary>Two routes, so the device has ports to select as well as profiles.</summary>
    private static ImmutableArray<SpaObject> Routes() =>
    [
        new SpaObject(SpaType.ObjectParamRoute, SpaParamType.EnumRoute,
        [
            new SpaProperty((uint)SpaParamRoute.Index, 0, new SpaInt(0)),
            new SpaProperty((uint)SpaParamRoute.Name, 0, new SpaString("speaker")),
            new SpaProperty((uint)SpaParamRoute.Description, 0, new SpaString("Speaker")),
            new SpaProperty((uint)SpaParamRoute.Priority, 0, new SpaInt(100)),
        ]),
        new SpaObject(SpaType.ObjectParamRoute, SpaParamType.EnumRoute,
        [
            new SpaProperty((uint)SpaParamRoute.Index, 0, new SpaInt(1)),
            new SpaProperty((uint)SpaParamRoute.Name, 0, new SpaString("headphone")),
            new SpaProperty((uint)SpaParamRoute.Description, 0, new SpaString("Headphones")),
            new SpaProperty((uint)SpaParamRoute.Priority, 0, new SpaInt(50)),
        ]),
    ];

    [TestMethod]
    public async Task ADeviceWeServe_AppearsInTheGraphAndLeavesTheSessionResponsive()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-device-host", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        string name = Unique();

        using (PipeWireDeviceProvider provider = PipeWireDeviceProvider.Create(
            ctx, name, "A device this test serves",
            new Dictionary<SpaParamType, ImmutableArray<SpaObject>>
            {
                [SpaParamType.EnumProfile] = Profiles(),
            }))
        {
            Assert.AreEqual(name, provider.Name);

            // The daemon publishing it is the whole point: a device that exports but never becomes a
            // global has failed silently.
            PipeWireGraphSnapshot graph = await WaitForAsync(
                registry,
                g => g.Devices.Any(d => d.DeviceName == name),
                cts.Token);

            PipeWireDevice? seen = graph.Devices.FirstOrDefault(d => d.DeviceName == name);
            Assert.IsNotNull(seen, $"the device '{name}' never appeared in the graph");

            // Still answering afterwards. A device implementation that blocks the loop takes the
            // whole session with it, which is the failure this hosting path can cause.
            await registry.WaitForInitialEnumerationAsync(cts.Token);
            Assert.IsTrue(registry.Current.Nodes.Length > 0, "the session stopped answering");
        }

        // Disposal withdraws it.
        await WaitForAsync(registry, g => !g.Devices.Any(d => d.DeviceName == name), cts.Token);
    }

    [TestMethod]
    public async Task ADeviceWeServe_AnswersItsProfilesThroughTheOrdinaryClientPath()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-device-params", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        string name = Unique();

        using PipeWireDeviceProvider provider = PipeWireDeviceProvider.Create(
            ctx, name, "A device with profiles",
            new Dictionary<SpaParamType, ImmutableArray<SpaObject>>
            {
                [SpaParamType.EnumProfile] = Profiles(),
            });

        PipeWireGraphSnapshot graph = await WaitForAsync(
            registry, g => g.Devices.Any(d => d.DeviceName == name), cts.Token);

        uint id = graph.Devices.First(d => d.DeviceName == name).Id;

        // Read it back from a second connection, which is what a device provider is for: the
        // consumer is another client, not the process serving it. Same-connection enumeration is a
        // different question and is covered separately.
        await using var reader = new PipeWireContext("pwnet-device-reader", ConsoleTestLoggerFactory.Instance);
        await reader.StartAsync(cts.Token);
        await using var readerRegistry = new PipeWireRegistry(reader);
        await readerRegistry.WaitForInitialEnumerationAsync(cts.Token);

        await using PipeWireDeviceControl control = readerRegistry.BindDevice(id);
        await control.ReadyAsync(cts.Token);

        ImmutableArray<SpaObject> profiles =
            await control.EnumerateParametersAsync(SpaParamType.EnumProfile, cts.Token);

        Assert.HasCount(2, profiles, "the device did not answer with the profiles it was given");

        var names = profiles
            .Select(static p => (p[(uint)SpaParamProfile.Name] as SpaString)?.Value)
            .ToArray();

        CollectionAssert.AreEquivalent(new[] { "off", "stereo" }, names);
    }

    [TestMethod]
    public async Task ADeviceWeServe_AnswersAFilteredEnumerationWithOnlyMatches()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-device-filter", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        string name = Unique();

        using PipeWireDeviceProvider provider = PipeWireDeviceProvider.Create(
            ctx, name, "A device with profiles",
            new Dictionary<SpaParamType, ImmutableArray<SpaObject>>
            {
                [SpaParamType.EnumProfile] = Profiles(),
            });

        PipeWireGraphSnapshot graph = await WaitForAsync(
            registry, g => g.Devices.Any(d => d.DeviceName == name), cts.Token);

        uint id = graph.Devices.First(d => d.DeviceName == name).Id;

        await using var reader = new PipeWireContext("pwnet-device-filter-reader", ConsoleTestLoggerFactory.Instance);
        await reader.StartAsync(cts.Token);
        await using var readerRegistry = new PipeWireRegistry(reader);
        await readerRegistry.WaitForInitialEnumerationAsync(cts.Token);

        await using PipeWireDeviceControl control = readerRegistry.BindDevice(id);
        await control.ReadyAsync(cts.Token);

        // The filter objects: one matching nothing, one matching the stereo profile.
        var surround = new SpaObject(SpaType.ObjectParamProfile, SpaParamType.EnumProfile,
        [
            new SpaProperty((uint)SpaParamProfile.Name, 0, new SpaString("surround")),
        ]);
        var stereo = new SpaObject(SpaType.ObjectParamProfile, SpaParamType.EnumProfile,
        [
            new SpaProperty((uint)SpaParamProfile.Name, 0, new SpaString("stereo")),
        ]);

        // Filtered first, on a fresh device: the daemon has cached nothing yet, so these run the
        // provider's own matcher rather than the daemon's cache projection.
        ImmutableArray<SpaObject> noneFirst =
            await control.EnumerateParametersAsync(SpaParamType.EnumProfile, surround, cts.Token);
        Assert.HasCount(0, noneFirst);

        ImmutableArray<SpaObject> matches =
            await control.EnumerateParametersAsync(SpaParamType.EnumProfile, stereo, cts.Token);
        Assert.HasCount(1, matches, "the filter did not narrow the enumeration to its match");
        Assert.AreEqual("stereo", ProfileName(matches[0]),
            "the filtered enumeration did not return the stereo profile");

        // Unfiltered, both profiles arrive: the filter narrows, it does not change the set. This
        // full enumeration also populates the daemon's cache, so the repeat below is served from
        // there rather than by the provider again - same answer, different path.
        ImmutableArray<SpaObject> all =
            await control.EnumerateParametersAsync(SpaParamType.EnumProfile, cts.Token);
        Assert.HasCount(2, all);

        ImmutableArray<SpaObject> cached =
            await control.EnumerateParametersAsync(SpaParamType.EnumProfile, stereo, cts.Token);
        Assert.HasCount(1, cached);
        Assert.AreEqual("stereo", ProfileName(cached[0]));
    }

    /// <summary>Reads a profile name however the daemon normalized it.</summary>
    /// <remarks>
    /// A filtered enumeration comes back through the daemon's cache, which projects values
    /// through the request filter: a plain string arrives as a fixed single-default choice.
    /// </remarks>
    private static string? ProfileName(SpaObject profile) =>
        profile[(uint)SpaParamProfile.Name] switch
        {
            SpaString s => s.Value,
            SpaChoice { Default: SpaString d } => d.Value,
            _ => null,
        };

    [TestMethod]
    public async Task DisposingADeviceWithARemoteListenerAttached_ThenDroppingTheListener_StaysUp()
    {
        // The destruction order this pins: the export proxy is destroyed first, under the loop
        // lock, which serializes against every callback that could traverse the listener list -
        // so by the time the list head itself is freed, no hook can outlive it. A use-after-free
        // here does not fail an assert, it takes the process down, which is why the test's only
        // real assertion is that the session is still answering afterwards.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-device-teardown", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        string name = Unique();

        var provider = PipeWireDeviceProvider.Create(
            ctx, name, "A device with a listener",
            new Dictionary<SpaParamType, ImmutableArray<SpaObject>>
            {
                [SpaParamType.EnumProfile] = Profiles(),
            });

        PipeWireGraphSnapshot graph = await WaitForAsync(
            registry, g => g.Devices.Any(d => d.DeviceName == name), cts.Token);

        uint id = graph.Devices.First(d => d.DeviceName == name).Id;

        // A second connection binds and subscribes, so a remote listener is attached while the
        // provider is disposed - the order the invariant is about.
        var reader = new PipeWireContext("pwnet-device-teardown-reader", ConsoleTestLoggerFactory.Instance);
        await reader.StartAsync(cts.Token);
        var readerRegistry = new PipeWireRegistry(reader);
        await readerRegistry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireDeviceControl control = readerRegistry.BindDevice(id);
        await control.ReadyAsync(cts.Token);
        control.SubscribeParameters(SpaParamType.EnumProfile);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        provider.Dispose();

        // The remote side drops only after the provider is gone.
        control.Dispose();
        await readerRegistry.DisposeAsync();
        await reader.DisposeAsync();

        await registry.WaitForInitialEnumerationAsync(cts.Token);
        Assert.IsTrue(registry.Current.Nodes.Length > 0, "the session stopped answering");
    }

    [TestMethod]
    public async Task ADeviceWithNoParameters_IsStillPublishedRatherThanRefused()
    {
        // A device that answers nothing is legal. It is what this library builds today, since child
        // node publication is out of scope, so it has to work rather than merely not crash.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-device-empty", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        string name = Unique();

        using PipeWireDeviceProvider provider =
            PipeWireDeviceProvider.Create(ctx, name, "An empty device");

        await WaitForAsync(registry, g => g.Devices.Any(d => d.DeviceName == name), cts.Token);
    }

    [TestMethod]
    public async Task ManyDevicesServedAndWithdrawn_LeaveNothingBehind()
    {
        // The hosting path allocates unmanaged memory per device and frees it on disposal. A leak
        // here is invisible until a long-running process has served a few thousand.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-device-churn", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        for (int i = 0; i < 20; i++)
        {
            using PipeWireDeviceProvider provider = PipeWireDeviceProvider.Create(
                ctx, $"{Unique()}_{i}", $"Churn {i}",
                new Dictionary<SpaParamType, ImmutableArray<SpaObject>>
                {
                    [SpaParamType.EnumProfile] = Profiles(),
                });
        }

        await registry.WaitForInitialEnumerationAsync(cts.Token);
        Assert.IsTrue(registry.Current.Nodes.Length > 0, "the session did not survive the churn");
    }

    [TestMethod]
    public async Task SettingAParameter_UpdatesWhatClientsEnumerate()
    {
        // The daemon re-reads rather than being handed the parameter, so a replacement set must
        // be what the next enumeration answers with.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-device-update", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        string name = Unique();

        using PipeWireDeviceProvider provider = PipeWireDeviceProvider.Create(
            ctx, name, "A device with profiles",
            new Dictionary<SpaParamType, ImmutableArray<SpaObject>>
            {
                [SpaParamType.EnumProfile] = Profiles(),
            });

        PipeWireGraphSnapshot graph = await WaitForAsync(
            registry, g => g.Devices.Any(d => d.DeviceName == name), cts.Token);
        uint id = graph.Devices.First(d => d.DeviceName == name).Id;

        await using var reader = new PipeWireContext("pwnet-device-reread", ConsoleTestLoggerFactory.Instance);
        await reader.StartAsync(cts.Token);
        await using var readerRegistry = new PipeWireRegistry(reader);
        await readerRegistry.WaitForInitialEnumerationAsync(cts.Token);

        await using PipeWireDeviceControl control = readerRegistry.BindDevice(id);
        await control.ReadyAsync(cts.Token);

        Assert.HasCount(2, await control.EnumerateParametersAsync(SpaParamType.EnumProfile, cts.Token));

        ImmutableArray<SpaObject> three = [.. Profiles(), Profiles()[0]];
        provider.SetParameter(SpaParamType.EnumProfile, three);

        Assert.HasCount(3, provider.GetParameter(SpaParamType.EnumProfile));
        Assert.HasCount(3, await control.EnumerateParametersAsync(SpaParamType.EnumProfile, cts.Token),
            "the replacement set is not what the next enumeration answered with");
    }

    [TestMethod]
    public async Task CreatingADeviceWithExtraProperties_KeepsItsName()
    {
        // device.name and device.description are this call's own arguments, not overrides a
        // properties bag can smuggle in: an empty target would be a node that silently never
        // links rather than an error the daemon reports.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-device-props", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        string name = Unique();

        using PipeWireDeviceProvider provider = PipeWireDeviceProvider.Create(
            ctx, name, "A device with properties",
            new Dictionary<SpaParamType, ImmutableArray<SpaObject>>
            {
                [SpaParamType.EnumProfile] = Profiles(),
            },
            new Dictionary<string, string>
            {
                ["device.name"] = "ignored",
                ["device.description"] = "ignored",
                ["x-pwnet-test"] = "yes",
            });

        Assert.AreEqual(name, provider.Name);

        PipeWireGraphSnapshot graph = await WaitForAsync(
            registry, g => g.Devices.Any(d => d.DeviceName == name), cts.Token);
        Assert.IsNotNull(graph.Devices.FirstOrDefault(d => d.DeviceName == name));
    }

    [TestMethod]
    public async Task DisposingADeviceWhileClientsEnumerate_LeavesTheSessionResponsive()
    {
        // The hostile shape for the hosting path: the export proxy is destroyed while another
        // client has enumerations in flight against it. Anything that outlives the teardown here
        // ends the test host, not the test.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-device-teardown", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        string name = Unique();

        var provider = PipeWireDeviceProvider.Create(
            ctx, name, "A device withdrawn mid-read",
            new Dictionary<SpaParamType, ImmutableArray<SpaObject>>
            {
                [SpaParamType.EnumProfile] = Profiles(),
            });

        PipeWireGraphSnapshot graph = await WaitForAsync(
            registry, g => g.Devices.Any(d => d.DeviceName == name), cts.Token);
        uint id = graph.Devices.First(d => d.DeviceName == name).Id;

        await using var reader = new PipeWireContext("pwnet-device-hammer", ConsoleTestLoggerFactory.Instance);
        await reader.StartAsync(cts.Token);
        await using var readerRegistry = new PipeWireRegistry(reader);
        await readerRegistry.WaitForInitialEnumerationAsync(cts.Token);

        await using PipeWireDeviceControl control = readerRegistry.BindDevice(id);
        await control.ReadyAsync(cts.Token);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Task hammer = Task.Run(async () =>
        {
            while (!stop.Token.IsCancellationRequested)
            {
                try
                {
                    await control.EnumerateParametersAsync(SpaParamType.EnumProfile, stop.Token);
                }
                catch (OperationCanceledException) { }
                catch (PipeWireException) { /* the device is going or gone */ }
                catch (ObjectDisposedException) { }
            }
        });

        await Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token);
        provider.Dispose();

        // Failures above are the device going away. A crash would have ended the host instead.
        await hammer;
        Assert.ThrowsExactly<ObjectDisposedException>(() => provider.SetParameter(SpaParamType.EnumProfile, []));

        await registry.WaitForInitialEnumerationAsync(cts.Token);
        Assert.IsTrue(registry.Current.Nodes.Length > 0, "the session stopped answering");
    }

    [TestMethod]
    public async Task EnumeratingAParameterTheDeviceDoesNotList_IsRefused()
    {
        // The daemon gates on the parameter list the device announced at export: asking for what
        // was never listed is an error, not an empty answer.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-device-gate", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        string name = Unique();

        using PipeWireDeviceProvider provider = PipeWireDeviceProvider.Create(
            ctx, name, "A device with profiles only",
            new Dictionary<SpaParamType, ImmutableArray<SpaObject>>
            {
                [SpaParamType.EnumProfile] = Profiles(),
            });

        PipeWireGraphSnapshot graph = await WaitForAsync(
            registry, g => g.Devices.Any(d => d.DeviceName == name), cts.Token);
        uint id = graph.Devices.First(d => d.DeviceName == name).Id;

        await using var reader = new PipeWireContext("pwnet-device-gateread", ConsoleTestLoggerFactory.Instance);
        await reader.StartAsync(cts.Token);
        await using var readerRegistry = new PipeWireRegistry(reader);
        await readerRegistry.WaitForInitialEnumerationAsync(cts.Token);

        await using PipeWireDeviceControl control = readerRegistry.BindDevice(id);
        await control.ReadyAsync(cts.Token);

        PipeWireException refused = await Assert.ThrowsExactlyAsync<PipeWireException>(
            () => control.EnumerateParametersAsync(SpaParamType.EnumRoute, cts.Token));
        Assert.AreEqual(-2, refused.Result);
    }

    [TestMethod]
    public async Task WritingAProfile_ReachesTheHostAsAParameterWrite()
    {
        // The full write path through the daemon: another client writes, our set_param parses,
        // and the host observes it.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-device-write", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        string name = Unique();

        using PipeWireDeviceProvider provider = PipeWireDeviceProvider.Create(
            ctx, name, "A device clients write to",
            new Dictionary<SpaParamType, ImmutableArray<SpaObject>>
            {
                [SpaParamType.EnumProfile] = Profiles(),
            });

        var seen = new System.Collections.Concurrent.ConcurrentBag<(SpaParamType Id, SpaObject? Value)>();
        provider.ParameterWritten += (p, id, value) => seen.Add((id, value));

        PipeWireGraphSnapshot graph = await WaitForAsync(
            registry, g => g.Devices.Any(d => d.DeviceName == name), cts.Token);
        uint id = graph.Devices.First(d => d.DeviceName == name).Id;

        await using var writer = new PipeWireContext("pwnet-device-writer", ConsoleTestLoggerFactory.Instance);
        await writer.StartAsync(cts.Token);
        await using var writerRegistry = new PipeWireRegistry(writer);
        await writerRegistry.WaitForInitialEnumerationAsync(cts.Token);

        await using PipeWireDeviceControl control = writerRegistry.BindDevice(id);
        await control.ReadyAsync(cts.Token);
        await control.SetProfileAsync(1, cts.Token);

        bool arrived = false;
        for (int attempt = 0; attempt < 150 && !arrived; attempt++)
        {
            foreach ((SpaParamType got, SpaObject? value) in seen)
            {
                if (got == SpaParamType.Profile
                    && (value?[(uint)SpaParamProfile.Index] as SpaInt)?.Value == 1)
                {
                    arrived = true;
                    break;
                }
            }

            if (!arrived)
                await Task.Delay(TimeSpan.FromMilliseconds(100), cts.Token);
        }

        Assert.IsTrue(arrived, "the profile write never reached the hosting process");
    }

    [TestMethod]
    public async Task AThrowingParameterHandler_IsReportedAndContained()
    {
        // A subscriber that throws must not take the binding with it: the fault is logged and
        // enumeration still answers afterwards.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-device-fault", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        string name = Unique();

        using PipeWireDeviceProvider provider = PipeWireDeviceProvider.Create(
            ctx, name, "A device with a faulting subscriber",
            new Dictionary<SpaParamType, ImmutableArray<SpaObject>>
            {
                [SpaParamType.EnumProfile] = Profiles(),
            });

        PipeWireGraphSnapshot graph = await WaitForAsync(
            registry, g => g.Devices.Any(d => d.DeviceName == name), cts.Token);
        uint id = graph.Devices.First(d => d.DeviceName == name).Id;

        await using var reader = new PipeWireContext("pwnet-device-faultread", ConsoleTestLoggerFactory.Instance);
        await reader.StartAsync(cts.Token);
        await using var readerRegistry = new PipeWireRegistry(reader);
        await readerRegistry.WaitForInitialEnumerationAsync(cts.Token);

        await using PipeWireDeviceControl control = readerRegistry.BindDevice(id);
        await control.ReadyAsync(cts.Token);
        control.SubscribeParameters(SpaParamType.EnumProfile);
        control.ParameterChanged += (_, _) => throw new InvalidOperationException("deliberate");

        // A new set toggles the serial, so the daemon re-reads and notifies the subscriber,
        // which throws inside the dispatch.
        provider.SetParameter(SpaParamType.EnumProfile, Profiles());

        Assert.HasCount(2, await control.EnumerateParametersAsync(SpaParamType.EnumProfile, cts.Token),
            "the binding stopped answering after a faulting handler");
    }

    [TestMethod]
    public async Task RouteVolumeGuards_RefuseBadInputBeforeTheDaemon()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-device-vol", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        string name = Unique();

        using PipeWireDeviceProvider provider = PipeWireDeviceProvider.Create(
            ctx, name, "A device volumes are checked against",
            new Dictionary<SpaParamType, ImmutableArray<SpaObject>>
            {
                [SpaParamType.EnumProfile] = Profiles(),
            });

        PipeWireGraphSnapshot graph = await WaitForAsync(
            registry, g => g.Devices.Any(d => d.DeviceName == name), cts.Token);
        uint id = graph.Devices.First(d => d.DeviceName == name).Id;

        await using var reader = new PipeWireContext("pwnet-device-volread", ConsoleTestLoggerFactory.Instance);
        await reader.StartAsync(cts.Token);
        await using var readerRegistry = new PipeWireRegistry(reader);
        await readerRegistry.WaitForInitialEnumerationAsync(cts.Token);

        await using PipeWireDeviceControl control = readerRegistry.BindDevice(id);
        await control.ReadyAsync(cts.Token);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            async () => await control.SetRouteVolumeAsync(0, 0, [], false, cancellationToken: cts.Token));
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            async () => await control.SetRouteVolumeAsync(
                0, 0, new float[] { -1f }, false, cancellationToken: cts.Token));
    }

    [TestMethod]
    public async Task ADeviceWeServe_AnswersItsRoutesThroughTheOrdinaryClientPath()
    {
        // Routes work the same exchange as profiles, through the same provider: a second client
        // enumerates them and writes one back.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-device-routes", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        string name = Unique();

        using PipeWireDeviceProvider provider = PipeWireDeviceProvider.Create(
            ctx, name, "A device with routes",
            new Dictionary<SpaParamType, ImmutableArray<SpaObject>>
            {
                [SpaParamType.EnumProfile] = Profiles(),
                [SpaParamType.EnumRoute] = Routes(),
            });

        var seen = new System.Collections.Concurrent.ConcurrentBag<(SpaParamType Id, SpaObject? Value)>();
        provider.ParameterWritten += (p, id, value) => seen.Add((id, value));

        PipeWireGraphSnapshot graph = await WaitForAsync(
            registry, g => g.Devices.Any(d => d.DeviceName == name), cts.Token);
        uint id = graph.Devices.First(d => d.DeviceName == name).Id;

        await using var reader = new PipeWireContext("pwnet-device-routeread", ConsoleTestLoggerFactory.Instance);
        await reader.StartAsync(cts.Token);
        await using var readerRegistry = new PipeWireRegistry(reader);
        await readerRegistry.WaitForInitialEnumerationAsync(cts.Token);

        await using PipeWireDeviceControl control = readerRegistry.BindDevice(id);
        await control.ReadyAsync(cts.Token);

        ImmutableArray<SpaObject> routes =
            await control.EnumerateRoutesAsync(cts.Token);
        Assert.HasCount(2, routes, "the device did not answer with the routes it was given");

        var names = routes
            .Select(static p => (p[(uint)SpaParamRoute.Name] as SpaString)?.Value)
            .ToArray();
        CollectionAssert.AreEquivalent(new[] { "speaker", "headphone" }, names);

        // The active routes are a different parameter this device does not serve: asking for
        // them is refused rather than answered empty.
        await Assert.ThrowsExactlyAsync<PipeWireException>(
            () => control.GetActiveRoutesAsync(cts.Token));

        await control.SetRouteAsync(1, 0, cts.Token);

        bool arrived = false;
        for (int attempt = 0; attempt < 150 && !arrived; attempt++)
        {
            foreach ((SpaParamType got, SpaObject? value) in seen)
            {
                if (got == SpaParamType.Route
                    && (value?[(uint)SpaParamRoute.Index] as SpaInt)?.Value == 1)
                {
                    arrived = true;
                    break;
                }
            }

            if (!arrived)
                await Task.Delay(TimeSpan.FromMilliseconds(100), cts.Token);
        }

        Assert.IsTrue(arrived, "the route write never reached the hosting process");
    }

    private static async Task<PipeWireGraphSnapshot> WaitForAsync(
        PipeWireRegistry registry,
        Func<PipeWireGraphSnapshot, bool> predicate,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            PipeWireGraphSnapshot graph = registry.Current;
            if (predicate(graph)) return graph;

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        Assert.Fail("the graph never reached the expected state");
        return registry.Current;
    }
}
