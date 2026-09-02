using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using PipeWire.NET.Interop;

namespace PipeWire.NET.Tests;

/// <summary>Builds a native <c>spa_dict</c> from managed pairs, freed when the test ends.</summary>
/// <remarks>
/// Shared by every test that feeds the parser a dictionary a daemon might send, including the ones
/// that send a dictionary no daemon would.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed unsafe class NativeDict : IDisposable
{
    private readonly List<nint> _allocations = [];
    private readonly spa_dict_item* _items;

    public NativeDict(params (string Key, string? Value)[] pairs)
    {
        _items = (spa_dict_item*)NativeMemory.AllocZeroed(
            (nuint)(sizeof(spa_dict_item) * Math.Max(pairs.Length, 1)));

        for (int i = 0; i < pairs.Length; i++)
        {
            _items[i].key = (sbyte*)Utf8(pairs[i].Key);
            // A null value is legal in a spa_dict and must not be dereferenced.
            _items[i].value = pairs[i].Value is null ? null : (sbyte*)Utf8(pairs[i].Value!);
        }

        Dict = new spa_dict { flags = 0, n_items = (uint)pairs.Length, items = _items };
    }

    public spa_dict Dict;

    private nint Utf8(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        nint p = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        ((byte*)p)[bytes.Length] = 0;
        _allocations.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (nint p in _allocations) Marshal.FreeHGlobal(p);
        NativeMemory.Free(_items);
    }
}
