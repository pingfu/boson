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
    /// The commits whose images this deployment may delete: every commit it
    /// built, less the ones it keeps and the ones another deployment keeps.
    /// Each of the others arrives already cut down to what its own
    /// `images.keep` says, since two branches can declare different numbers.
    /// A torn-down deployment keeps none of its own.
    ///
    /// Worked out from deploy history rather than from the tags on disk. Two
    /// branches building one image name share its tags, and a tag does not say
    /// which branch built it, so counting tags lets a preview branch's deploys
    /// delete the images production would roll back to.
    /// </summary>
    public static HashSet<string> Removable(
        IEnumerable<string> ownCommits,
        IEnumerable<string> keptOwn,
        IEnumerable<IEnumerable<string>> keptByOthers)
    {
        var removable = ownCommits.ToHashSet();

        removable.ExceptWith(keptOwn);

        foreach (var kept in keptByOthers)
            removable.ExceptWith(kept);

        return removable;
    }
}
