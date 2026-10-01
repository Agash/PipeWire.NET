using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;

namespace PipeWire.NET.Tests;

[TestClass]
public sealed class LoggingTests
{
    [TestMethod]
    public void EventIds_AreExplicitAndUniqueAcrossTheLibrary()
    {
        List<(int Id, string Method)> ids = [];
        foreach (
            Assembly assembly in new[]
            {
                typeof(PipeWireRegistry).Assembly,
                typeof(PipeWireVideoCapture).Assembly,
            }
        )
        {
            foreach (Type type in assembly.GetTypes())
            {
                foreach (
                    MethodInfo method in type.GetMethods(
                        BindingFlags.Instance
                            | BindingFlags.Static
                            | BindingFlags.Public
                            | BindingFlags.NonPublic
                            | BindingFlags.DeclaredOnly
                    )
                )
                {
                    if (method.GetCustomAttribute<LoggerMessageAttribute>() is { } attribute)
                    {
                        string name = $"{type.FullName}.{method.Name}";
                        Assert.AreNotEqual(-1, attribute.EventId, $"{name} has no event id");
                        ids.Add((attribute.EventId, name));
                    }
                }
            }
        }

        Assert.IsGreaterThan(50, ids.Count, $"the library's log methods were found ({ids.Count})");
        string[] duplicates =
        [
            .. ids.GroupBy(static i => i.Id)
                .Where(static g => g.Count() > 1)
                .Select(static g =>
                    $"{g.Key}: {string.Join(", ", g.Select(static i => i.Method))}"
                ),
        ];
        Assert.IsEmpty(duplicates, string.Join("; ", duplicates));
    }
}
