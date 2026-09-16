using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PipeWire.NET.Tests;

/// <summary>
/// Every method native code calls into is one the runtime will let it call.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="UnmanagedCallersOnlyAttribute"/> method must have a blittable signature, and whether
/// C's <c>bool</c> is blittable depends on the assembly: only under
/// <see cref="DisableRuntimeMarshallingAttribute"/>. A method that breaks the rule compiles, and the
/// runtime refuses it only when native code calls it - before its body runs, so its own try/catch
/// cannot help. The exception then unwinds through the native frame that called it, skipping that
/// frame's cleanup.
/// </para>
/// <para>
/// That is not hypothetical here. <c>PipeWireStreamCore.DoPublishDriverClock</c> takes
/// <c>spa_invoke_func_t</c>'s <c>bool async</c>, and the media assembly lacked the attribute.
/// Called through <c>pw_loop_locked</c>, the skipped cleanup was the loop mutex's unlock: the thread
/// kept the loop lock, and the next context dispose deadlocked joining the loop thread. It only
/// surfaced once a stream actually drove the graph, which is why a check of the signatures, not a
/// live test of each path, is what guards it.
/// </para>
/// </remarks>
[TestClass]
public sealed class NativeCallbackSignatureTests
{
    private static readonly Assembly[] Shipped =
    [
        typeof(Graph.PipeWireRegistry).Assembly,
        typeof(Media.Streams.PipeWireAudioCapture).Assembly,
    ];

    [TestMethod]
    public void EveryShippedAssembly_DisablesRuntimeMarshalling()
    {
        foreach (Assembly assembly in Shipped)
        {
            Assert.IsNotNull(assembly.GetCustomAttribute<DisableRuntimeMarshallingAttribute>(),
                $"{assembly.GetName().Name} does not disable runtime marshalling, so C's bool in the "
                + "native callback signatures it shares with the core is not blittable there");
        }
    }

    [TestMethod]
    public void EveryNativeCallback_HasASignatureTheRuntimeAccepts()
    {
        var offenders = new List<string>();

        foreach (Assembly assembly in Shipped)
        {
            bool relaxed = assembly.GetCustomAttribute<DisableRuntimeMarshallingAttribute>() is not null;

            foreach (Type type in assembly.GetTypes())
            {
                foreach (MethodInfo method in type.GetMethods(
                             BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (method.GetCustomAttribute<UnmanagedCallersOnlyAttribute>() is null) continue;

                    IEnumerable<Type> types = method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType);
                    foreach (Type t in types)
                    {
                        if (!Acceptable(t, relaxed))
                            offenders.Add($"{type.FullName}.{method.Name}: {t.Name}");
                    }
                }
            }
        }

        Assert.AreEqual(0, offenders.Count,
            "these native callbacks would be refused when called, unwinding through the native frame "
            + "that called them:\n" + string.Join("\n", offenders));
    }

    private static bool Acceptable(Type t, bool relaxed)
    {
        if (t == typeof(void) || t.IsPointer || t.IsFunctionPointer || t.IsUnmanagedFunctionPointer) return true;
        if (t == typeof(bool) || t == typeof(char)) return relaxed;
        if (t.IsPrimitive || t.IsEnum) return true;
        if (t.IsValueType) return IsUnmanagedStruct(t);
        return false;
    }

    private static bool IsUnmanagedStruct(Type t) =>
        t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .All(f => f.FieldType.IsPointer || f.FieldType.IsPrimitive || f.FieldType.IsEnum
                      || (f.FieldType.IsValueType && IsUnmanagedStruct(f.FieldType)));
}
