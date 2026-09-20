using System.Text.Json;

namespace Boson.Deploy;

/// <summary>
/// The images a deployment builds, read back from `docker compose config` so
/// compose does the parsing. Only services that build are listed: a service
/// running someone else's published image has nothing for boson to retain or
/// delete.
/// </summary>
public static class ComposeImages
{
    public sealed record BuiltImage(string Service, string Name, string Tag)
    {
        public string Reference => $"{Name}:{Tag}";
    }

    public static List<BuiltImage> Read(string composeConfigJson)
    {
        var images = new List<BuiltImage>();

        using var doc = JsonDocument.Parse(composeConfigJson);

        if (!doc.RootElement.TryGetProperty("services", out var services)) return images;

        foreach (var service in services.EnumerateObject())
        {
            if (!service.Value.TryGetProperty("build", out _)) continue;
            if (!service.Value.TryGetProperty("image", out var image)) continue;

            var reference = image.GetString();

            if (string.IsNullOrWhiteSpace(reference)) continue;

            // A digest is someone else's immutable image, not something this
            // build produced.
            if (reference.Contains('@')) continue;

            var colon = reference.LastIndexOf(':');

            // A colon in the registry's host:port is not a tag separator, which
            // a slash after it gives away.
            var hasTag = colon > 0 && !reference[colon..].Contains('/');

            images.Add(hasTag
                ? new BuiltImage(service.Name, reference[..colon], reference[(colon + 1)..])
                : new BuiltImage(service.Name, reference, "latest"));
        }

        return images;
    }

    /// <summary>
    /// The tags to delete: everything but the newest <paramref name="keep"/>,
    /// and never the one just deployed. `docker images` lists newest first.
    /// </summary>
    public static List<string> Stale(IReadOnlyList<string> tagsNewestFirst, string keepTag, int keep)
    {
        var kept = 0;
        var stale = new List<string>();

        foreach (var tag in tagsNewestFirst)
        {
            if (tag == keepTag || tag == "<none>") continue;

            if (++kept >= keep) stale.Add(tag);
        }

        return stale;
    }
}
