using System.Text.Json;

namespace PipeWire.NET.Graph;

/// <summary>
/// One entry in a metadata store.
/// </summary>
/// <param name="Subject">
/// Which object the entry is about. <see cref="PipeWireMetadataStore.SubjectCore"/> means the
/// daemon itself, which is where settings that belong to no single object live.
/// </param>
/// <param name="Key">The entry key, such as <c>default.audio.sink</c>.</param>
/// <param name="Type">
/// What the value is, such as <c>Spa:String:JSON</c>. The daemon may leave this unset.
/// </param>
/// <param name="Value">
/// The value, or <see langword="null"/> when the entry is being removed.
/// </param>
public sealed record PipeWireMetadataEntry(uint Subject, string Key, string? Type, string? Value)
{
    /// <summary>
    /// The <c>name</c> field of a JSON value, which is how the default sink and source are stored.
    /// </summary>
    /// <remarks>
    /// The daemon writes <c>{ "name": "alsa_output..." }</c> rather than a bare string, so reading
    /// <see cref="Value"/> directly gives the JSON and not the node name. Returns
    /// <see langword="null"/> when the value is absent, is not JSON, or has no <c>name</c> - all of
    /// which are things a daemon may legitimately do, so none of them throw.
    /// </remarks>
    public string? NameValue
    {
        get
        {
            if (string.IsNullOrEmpty(Value)) return null;

            try
            {
                using var document = JsonDocument.Parse(Value);
                return document.RootElement.ValueKind == JsonValueKind.Object
                       && document.RootElement.TryGetProperty("name", out JsonElement name)
                       && name.ValueKind == JsonValueKind.String
                    ? name.GetString()
                    : null;
            }
            catch (JsonException)
            {
                // Deliberately not logged: the value belongs to whatever wrote it, this is a
                // read-only accessor with no operation to name, and the caller sees the raw Value.
                return null;
            }
        }
    }
}
