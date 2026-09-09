using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// Holds the property names in <see cref="PipeWireNames"/> to what PipeWire actually defines.
/// </summary>
/// <remarks>
/// The names are hand-written, and a wrong one costs nothing at compile time and reads as an absent
/// property at run time, which is invisible. That is how <c>port.exclusive</c> came to be read from
/// a dictionary the daemon never puts it in. The list this checks against is generated from the
/// pinned upstream clone and committed, so the check runs without it.
/// </remarks>
[TestClass]
public sealed class UpstreamKeyDriftTests : PipeWireTestBase
{
    private static string SnapshotPath()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PipeWire.NET.slnx")))
            dir = dir.Parent;

        Assert.IsNotNull(dir, "could not find the repository root from the test output directory");
        return Path.Combine(dir!.FullName, "tests", "PipeWire.NET.Tests", "upstream-keys.txt");
    }

    private static Dictionary<string, string> Upstream()
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(SnapshotPath()))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] parts = line.Split(' ', 2);
            if (parts.Length == 2) keys[parts[1]] = parts[0];
        }
        return keys;
    }

    [TestMethod]
    public void EveryNameWeSpell_IsAKeyPipeWireDefines()
    {
        Dictionary<string, string> upstream = Upstream();
        Assert.IsGreaterThan(100, upstream.Count, "the upstream key snapshot did not load");

        // Keys PipeWire does not define in keys.h, with the reason each is legitimate. Anything
        // else that is not upstream is a typo or a key that was renamed out from under us.
        var elsewhere = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["object.serial"] = "set on every global by pw_global_new, not declared in keys.h",
            ["metadata.name"] = "module-metadata's own key",
            ["factory.type.name"] = "written by impl-factory from the interface type",
            ["factory.type.version"] = "written by impl-factory from the interface version",
            ["port.direction"] = "written by impl-port from the port's own direction",
            ["port.id"] = "written by impl-port from the port's index",
            ["node.id"] = "written by impl-port to name the owning node",
            ["core.name"] = "written by impl-core",
            ["core.version"] = "written by impl-core",
            ["module.filename"] = "a pw_module_info field, surfaced as a property by this library",
            ["module.args"] = "a pw_module_info field, surfaced as a property by this library",
        };

        List<string> unknown = [];
        foreach (FieldInfo field in typeof(PipeWireNames).GetFields(
                     BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetRawConstantValue() is not string value) continue;
            if (upstream.ContainsKey(value) || elsewhere.ContainsKey(value)) continue;
            unknown.Add($"{field.Name} = \"{value}\"");
        }

        Assert.AreEqual(0, unknown.Count,
            "these names are not keys PipeWire defines, so they read as absent forever: "
            + string.Join(", ", unknown));
    }

    [TestMethod]
    public void TheKeysWeRelyOnMostHaveNotBeenRenamed()
    {
        // A spot check with the upstream constant named, so a rename shows up as this test rather
        // than as a property that quietly stopped being populated.
        Dictionary<string, string> upstream = Upstream();

        (string Name, string Key)[] expected =
        [
            ("PW_KEY_DEVICE_ID", PipeWireNames.DeviceId),
            ("PW_KEY_NODE_NAME", PipeWireNames.NodeName),
            ("PW_KEY_MEDIA_CLASS", PipeWireNames.MediaClass),
            ("PW_KEY_PORT_CONTROL", PipeWireNames.PortControl),
            ("PW_KEY_PORT_MONITOR", PipeWireNames.PortMonitor),
            ("PW_KEY_LINK_OUTPUT_NODE", PipeWireNames.LinkOutputNode),
            ("PW_KEY_PRIORITY_SESSION", PipeWireNames.PrioritySession),
            ("PW_KEY_SEC_APP_ID", PipeWireNames.SecurityAppId),
        ];

        foreach ((string name, string key) in expected)
        {
            Assert.IsTrue(upstream.TryGetValue(key, out string? actual),
                $"\"{key}\" is not defined upstream at all");
            Assert.AreEqual(name, actual, $"\"{key}\" is upstream, but as {actual} rather than {name}");
        }
    }
}
