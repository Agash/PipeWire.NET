using System.Collections;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Text;

namespace PipeWire.NET.Graph;

/// <summary>
/// The properties the daemon sent for an object, as it sent them.
/// </summary>
/// <remarks>
/// <para>
/// The typed fields on each graph object cover the properties this library has an opinion about.
/// This is everything else: PipeWire's property set is open, modules and session managers add their
/// own keys, and a new daemon version can start sending one before this library models it. Reading
/// from here is how a caller uses such a key without waiting for a release.
/// </para>
/// <para>
/// A registry global carries a filtered subset of an object's properties. Binding the object
/// delivers the rest, and the registry replaces the object with one whose properties include them,
/// so what is here depends on whether the object is bound.
/// </para>
/// <para>
/// A property whose value the daemon sent as null is treated as absent, matching
/// <c>spa_dict_lookup</c>. A property that is present and empty is kept, because an empty value and
/// no value are different facts.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class PipeWireProperties : IReadOnlyDictionary<string, string>
{
    private readonly FrozenDictionary<string, string> _items;

    private PipeWireProperties(FrozenDictionary<string, string> items) => _items = items;

    /// <summary>An object the daemon sent no properties for.</summary>
    public static PipeWireProperties Empty { get; } =
        new(FrozenDictionary<string, string>.Empty);

    /// <inheritdoc/>
    public string this[string key] => _items[key];

    /// <inheritdoc/>
    public IEnumerable<string> Keys => _items.Keys;

    /// <inheritdoc/>
    public IEnumerable<string> Values => _items.Values;

    /// <inheritdoc/>
    public int Count => _items.Count;

    /// <inheritdoc/>
    public bool ContainsKey(string key) => _items.ContainsKey(key);

    /// <inheritdoc/>
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value) =>
        _items.TryGetValue(key, out value);

    /// <summary>The value of <paramref name="key"/>, or null when the daemon did not send it.</summary>
    public string? GetValueOrDefault(string key) => _items.GetValueOrDefault(key);

    /// <summary>The value of <paramref name="key"/>, or null when the daemon did not send it.</summary>
    internal string? Text(string key) => _items.GetValueOrDefault(key);

    /// <summary>Reads a property that names another object, or null when it is absent or unusable.</summary>
    internal uint? Id(string key) =>
        _items.TryGetValue(key, out string? raw) && uint.TryParse(raw, out uint value) ? value : null;

    /// <inheritdoc cref="Id"/>
    internal int? Int(string key) =>
        _items.TryGetValue(key, out string? raw) && int.TryParse(raw, out int value) ? value : null;

    /// <summary>Reads a boolean property, matching <c>spa_atob</c>: only "true" and "1" are true.</summary>
    internal bool Flag(string key) =>
        _items.TryGetValue(key, out string? raw) && (raw is "true" or "1");

    /// <summary>Reads <c>object.serial</c>, which every object carries.</summary>
    internal ulong? Serial =>
        _items.TryGetValue("object.serial", out string? raw) && ulong.TryParse(raw, out ulong serial)
            ? serial
            : null;

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Reads a whole <c>spa_dict</c>, on the loop thread, without trusting any of it.
    /// </summary>
    /// <remarks>
    /// A repeated key keeps its first value, which is what <c>spa_dict_lookup</c> would have
    /// returned; taking the last would make this disagree with every other read of the same dict.
    /// </remarks>
    internal static unsafe PipeWireProperties From(spa_dict* dict)
    {
        if (dict is null) return Empty;

        int count = (int)dict->n_items;
        if (count <= 0 || dict->items is null) return Empty;

        var items = new Dictionary<string, string>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            spa_dict_item* item = dict->items + i;
            if (item->key is null || item->value is null) continue;

            string key = Encoding.UTF8.GetString(DaemonText.Bytes(item->key));
            if (items.ContainsKey(key)) continue;
            items[key] = Encoding.UTF8.GetString(DaemonText.Bytes(item->value));
        }

        return items.Count == 0 ? Empty : new PipeWireProperties(items.ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <summary>Wraps properties this library assembled rather than read off the wire.</summary>
    internal static PipeWireProperties FromItems(IReadOnlyDictionary<string, string> items) =>
        items.Count == 0
            ? Empty
            : new PipeWireProperties(items.ToFrozenDictionary(StringComparer.Ordinal));

    /// <summary>
    /// Combines these properties with the ones an <c>info</c> event delivered.
    /// </summary>
    /// <remarks>
    /// The bound object's own view wins on a conflict: the registry global is a filtered copy taken
    /// when the object was announced, and the info event is the object answering for itself.
    /// </remarks>
    internal PipeWireProperties MergedWith(PipeWireProperties other)
    {
        if (other.Count == 0) return this;
        if (Count == 0) return other;

        var items = new Dictionary<string, string>(_items, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> pair in other._items) items[pair.Key] = pair.Value;
        return new PipeWireProperties(items.ToFrozenDictionary(StringComparer.Ordinal));
    }
}
