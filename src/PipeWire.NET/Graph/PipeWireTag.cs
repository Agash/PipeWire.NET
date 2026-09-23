using System.Collections.Immutable;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Graph;

/// <summary>Metadata travelling with a stream, per direction.</summary>
/// <remarks>
/// A tag is how a producer says what the audio is - track title, station name, the media role - to
/// whatever is downstream, without any of it being part of the format. It rides the graph alongside
/// the data rather than through a side channel, so it survives being routed.
/// </remarks>
/// <param name="Direction">Which side of the object this describes.</param>
/// <param name="Info">The key and value pairs, in the order the producer wrote them.</param>
public sealed record PipeWireTag(
    SpaDirection Direction,
    ImmutableArray<KeyValuePair<string, string>> Info
)
{
    /// <summary>Reads one out of a <c>SPA_PARAM_Tag</c> object, or null if it is not one.</summary>
    /// <remarks>
    /// The info is a Struct of a count followed by that many key and value strings
    /// (<c>spa/param/tag.h:23-27</c>). The count is the producer's word, so the pairs actually
    /// present are what is read; a count disagreeing with them is ignored rather than trusted.
    /// </remarks>
    public static PipeWireTag? From(SpaObject? param)
    {
        if (param is null || param.ObjectType != SpaType.ObjectParamTag)
            return null;

        var pairs = ImmutableArray.CreateBuilder<KeyValuePair<string, string>>();

        if (param[(uint)SpaParamTag.Info] is SpaStruct info)
        {
            // Skips the leading count and reads pairs until they run out.
            int start = !info.Fields.IsDefaultOrEmpty && info.Fields[0] is SpaInt ? 1 : 0;
            for (int i = start; i + 1 < info.Fields.Length; i += 2)
            {
                if (info.Fields[i] is SpaString key && info.Fields[i + 1] is SpaString value)
                    pairs.Add(new KeyValuePair<string, string>(key.Value, value.Value));
            }
        }

        return new PipeWireTag(
            (SpaDirection)(param[(uint)SpaParamTag.Direction] is SpaId d ? d.Value : 0),
            pairs.ToImmutable()
        );
    }

    /// <summary>This tag as the parameter object the daemon expects.</summary>
    public SpaObject ToParameter()
    {
        var fields = ImmutableArray.CreateBuilder<SpaValue>((Info.Length * 2) + 1);
        fields.Add(new SpaInt(Info.Length));
        foreach (KeyValuePair<string, string> pair in Info)
        {
            fields.Add(new SpaString(pair.Key));
            fields.Add(new SpaString(pair.Value));
        }

        return new SpaObject(
            SpaType.ObjectParamTag,
            SpaParamType.Tag,
            [
                new SpaPodProperty((uint)SpaParamTag.Direction, 0, new SpaId((uint)Direction)),
                new SpaPodProperty(
                    (uint)SpaParamTag.Info,
                    0,
                    new SpaStruct(fields.MoveToImmutable())
                ),
            ]
        );
    }
}
