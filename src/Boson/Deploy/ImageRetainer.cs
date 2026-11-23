using Boson.Util;

namespace Boson.Deploy;

public interface IImageRetainer
{
    /// <summary>
    /// <paramref name="removable"/> is the commits whose images may go, as
    /// <see cref="ComposeImages.Removable"/> works them out.
    /// </summary>
    Task PruneAsync(
        string composeConfigJson, string tag, IReadOnlySet<string> removable, int keep,
        Action<string> log, CancellationToken ct);
}

public sealed class ImageRetainer(IDockerCli docker) : IImageRetainer
{
    /// <summary>
    /// Removes the images of commits this deployment no longer keeps, for each
    /// image it builds. Any other tag on the same image name is left alone: a
    /// release label, an operator's rollback marker, or another branch's build.
    /// </summary>
    public async Task PruneAsync(
        string composeConfigJson, string tag, IReadOnlySet<string> removable, int keep,
        Action<string> log, CancellationToken ct)
    {
        foreach (var image in ComposeImages.Read(composeConfigJson))
        {
            if (image.Tag != tag)
            {
                log($"image retention: {image.Service} builds {image.Reference}, " +
                    $"so nothing is retained; tag it :${ComposeVariables.CommitVariable} to keep the last {keep}");
                continue;
            }

            var tags = await docker.ImageTagsAsync(image.Name, ct);

            if (!tags.Ok)
            {
                log($"image retention: docker images {image.Name}: {tags.StdErr.Trim()}");
                continue;
            }

            var stale = tags.StdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(listed => IsFullShaTag(listed) && listed != tag && removable.Contains(listed))
                .ToList();

            foreach (var old in stale)
            {
                var removed = await docker.ImageRemoveAsync($"{image.Name}:{old}", ct);

                log(removed.Ok
                    ? $"removed {image.Name}:{old}"
                    : $"image retention: {image.Name}:{old}: {removed.StdErr.Trim()}");
            }
        }
    }

    internal static bool IsFullShaTag(string tag) =>
        tag.Length == 40 && tag.All(c =>
            c is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F');
}
