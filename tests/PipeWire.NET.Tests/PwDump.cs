using System.Runtime.Versioning;
using System.Text.Json;

namespace PipeWire.NET.Tests;

/// <summary>
/// The graph as <c>pw-dump</c> reports it, as an independent structural oracle.
/// </summary>
/// <remarks>
/// pw-dump emits the whole graph as JSON, which is what makes it usable as an oracle: our snapshot
/// can be diffed against it wholesale rather than object by object. The alternative in use before
/// this was scraping <c>pw-link</c>'s human-readable output, which is formatted for people - its
/// column alignment changes with id width, and reading it wrongly already produced one confident
/// but false diagnosis in this repo.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed record PwDump(IReadOnlyList<PwDump.Entry> Entries)
{
    /// <summary>One global, reduced to what a test compares on.</summary>
    internal sealed record Entry(uint Id, string Type, IReadOnlyDictionary<string, string> Props)
    {
        /// <summary>The interface's short name, such as <c>Node</c> or <c>Link</c>.</summary>
        public string Kind => Type.Split(':').LastOrDefault() ?? Type;

        public string? Prop(string key) => Props.GetValueOrDefault(key);
    }

    public IEnumerable<Entry> OfKind(string kind) =>
        Entries.Where(e => string.Equals(e.Kind, kind, StringComparison.Ordinal));

    public IEnumerable<uint> IdsOfKind(string kind) => OfKind(kind).Select(e => e.Id);

    public Entry? ById(uint id) => Entries.FirstOrDefault(e => e.Id == id);

    /// <summary>Runs pw-dump and parses it.</summary>
    public static async Task<PwDump> CaptureAsync(CancellationToken cancellationToken)
    {
        CliTool tool = CliTool.Require("pw-dump");
        (int exit, string stdout, string stderr) = await tool.RunAsync([], cancellationToken);

        if (exit != 0)
            throw new InvalidOperationException($"pw-dump exited {exit}: {stderr}");

        return Parse(stdout);
    }

    internal static PwDump Parse(string json)
    {
        var entries = new List<Entry>();

        using JsonDocument doc = JsonDocument.Parse(json);
        foreach (JsonElement element in doc.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("id", out JsonElement idElement)) continue;
            if (!element.TryGetProperty("type", out JsonElement typeElement)) continue;

            var props = new Dictionary<string, string>(StringComparer.Ordinal);
            if (element.TryGetProperty("info", out JsonElement info)
                && info.TryGetProperty("props", out JsonElement propsElement)
                && propsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in propsElement.EnumerateObject())
                {
                    props[p.Name] = p.Value.ValueKind == JsonValueKind.String
                        ? p.Value.GetString() ?? string.Empty
                        : p.Value.ToString();
                }
            }

            entries.Add(new Entry(idElement.GetUInt32(), typeElement.GetString() ?? string.Empty, props));
        }

        return new PwDump(entries);
    }
}
