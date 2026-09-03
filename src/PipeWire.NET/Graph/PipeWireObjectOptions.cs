using System.Collections.Immutable;
using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

/// <summary>
/// Creation-time properties that apply to any object the registry makes.
/// </summary>
/// <remarks>
/// Held as plain fields rather than an <c>spa_dict</c>: the dictionary is a <c>ref struct</c> over
/// stack memory, which cannot survive the <see langword="await"/> in <c>ExecuteAsync</c>, so the
/// options are collected first and marshalled in one synchronous step at the end.
/// </remarks>
/// <param name="Linger">The object outlives the client that created it.</param>
/// <param name="Passive">A link that does not by itself keep its nodes running.</param>
/// <param name="Properties">
/// Everything else, as the daemon takes it: a property dictionary. Applied after the library's own
/// defaults, so a caller can override any of them.
/// </param>
/// <remarks>
/// Properties are a list rather than named fields because the set is PipeWire's, not ours: node
/// creation alone takes <c>node.nick</c>, <c>node.virtual</c>, <c>priority.driver</c>,
/// <c>node.group</c>, <c>target.object</c>, the audio format keys and more, and each release adds
/// to them. A fixed list of fields would be out of date the first time that happened, and a caller
/// needing a key we had not thought of would have no way to send it.
/// </remarks>
internal readonly record struct PipeWireObjectOptions(
    bool Linger,
    bool Passive,
    ImmutableArray<KeyValuePair<string, string>> Properties = default)
{
    /// <summary>Keys that say what an object is rather than how it is configured.</summary>
    /// <remarks>
    /// The factory decides which kind of object the daemon builds, and a link's endpoints decide
    /// what it connects. Both are written by the library from arguments the caller already passed,
    /// and a duplicate key is not an override: spa_dict_lookup returns the first match, so a
    /// caller-supplied value placed ahead of the library's silently wins and routes the link
    /// elsewhere or hands the request to another factory.
    /// </remarks>
    private static readonly string[] ReservedForEverything = ["factory.name"];

    private static readonly string[] ReservedForLinks =
    [
        "link.output.node", "link.output.port", "link.input.node", "link.input.port",
    ];

    /// <summary>Refuses a key the caller does not get to set.</summary>
    /// <param name="key">The property name.</param>
    /// <param name="forLink">Whether the object being created is a link.</param>
    /// <exception cref="ArgumentException">The key is reserved.</exception>
    internal static void ThrowIfReserved(string key, bool forLink)
    {
        if (Reserved(key, forLink))
        {
            throw new ArgumentException(
                $"'{key}' is decided by the creation call itself and cannot be set as a property.",
                nameof(key));
        }
    }

    private static bool Reserved(string key, bool forLink)
    {
        foreach (string reserved in ReservedForEverything)
        {
            if (string.Equals(key, reserved, StringComparison.Ordinal)) return true;
        }

        if (!forLink) return false;

        foreach (string reserved in ReservedForLinks)
        {
            if (string.Equals(key, reserved, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}
