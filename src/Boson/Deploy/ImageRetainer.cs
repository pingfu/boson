using Boson.Util;

namespace Boson.Deploy;

public interface IImageRetainer
{
    Task PruneAsync(
        string composeConfigJson, string tag, int keep, Action<string> log, CancellationToken ct);
}

public sealed class ImageRetainer(IDockerCli docker) : IImageRetainer
{
    /// <summary>
    /// Keeps the last few Boson-built tags for each image this deployment
    /// produces. Other tags on the same image name may be release labels or
    /// operator rollback markers, so retention leaves them alone.
    /// </summary>
    public async Task PruneAsync(
        string composeConfigJson, string tag, int keep, Action<string> log, CancellationToken ct)
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

            var listed = tags.StdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(IsFullShaTag)
                .ToList();

            foreach (var stale in ComposeImages.Stale(listed, tag, keep))
            {
                var removed = await docker.ImageRemoveAsync($"{image.Name}:{stale}", ct);

                log(removed.Ok
                    ? $"removed {image.Name}:{stale}"
                    : $"image retention: {image.Name}:{stale}: {removed.StdErr.Trim()}");
            }
        }
    }

    internal static bool IsFullShaTag(string tag) =>
        tag.Length == 40 && tag.All(c =>
            c is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F');
}
