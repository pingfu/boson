using Boson.Deploy;
using Boson.Tests.Support;
using Xunit;

namespace Boson.Tests;

public class ImageRetainerTests
{
    private const string Image = "acme/site-web";

    private static string Sha(int n) => $"{n:x8}" + new string('0', 32);

    private readonly FakeDockerCli _docker = new();
    private readonly List<string> _log = [];

    private static string Config(string reference) =>
        """{"services":{"web":{"build":{},"image":"REFERENCE"}}}""".Replace("REFERENCE", reference);

    /// <summary>Which commits may go is ComposeImages.Removable's decision; here it is handed in.</summary>
    private Task PruneAsync(string tag, params string[] removable) =>
        new ImageRetainer(_docker).PruneAsync(
            Config($"{Image}:{tag}"), tag, removable.ToHashSet(), 3, _log.Add, CancellationToken.None);

    [Fact]
    public async Task The_commits_it_is_told_may_go_are_removed()
    {
        _docker.ImageTags[Image] = [Sha(4), Sha(3), Sha(2), Sha(1)];

        await PruneAsync(Sha(4), Sha(1));

        Assert.Equal([$"{Image}:{Sha(1)}"], _docker.ImagesRemoved);
    }

    [Fact]
    public async Task The_build_just_made_is_never_removed()
    {
        _docker.ImageTags[Image] = [Sha(2), Sha(1)];

        await PruneAsync(Sha(2), Sha(2), Sha(1));

        Assert.Equal([$"{Image}:{Sha(1)}"], _docker.ImagesRemoved);
    }

    [Fact]
    public async Task A_commit_with_no_image_on_disk_is_not_asked_for()
    {
        _docker.ImageTags[Image] = [Sha(2)];

        await PruneAsync(Sha(2), Sha(1));

        Assert.Empty(_docker.ImagesRemoved);
    }

    [Fact]
    public async Task Tags_it_was_not_told_about_are_left_alone()
    {
        // A release label, a rollback marker an operator put there, or a
        // commit another branch built, none of which are this deploy's.
        _docker.ImageTags[Image] = [Sha(3), "stable", "v1.2.3", "latest", Sha(2), Sha(1)];

        await PruneAsync(Sha(3), Sha(1), "stable");

        Assert.Equal([$"{Image}:{Sha(1)}"], _docker.ImagesRemoved);
    }

    [Fact]
    public async Task An_image_this_deploy_did_not_tag_is_reported_rather_than_pruned()
    {
        // Silence would read as retention working, while every build quietly
        // leaves another untagged image on disk.
        await new ImageRetainer(_docker).PruneAsync(
            Config("someone/else:v1"), Sha(1), new HashSet<string>(), 3, _log.Add, CancellationToken.None);

        Assert.Empty(_docker.ImagesRemoved);
        Assert.Contains(_log, line => line.Contains("BOSON_COMMIT"));
    }

    [Fact]
    public async Task A_docker_failure_is_logged_and_the_deploy_carries_on()
    {
        // The containers are already running by the time retention happens.
        _docker.ImageTagsExitCode = 1;

        await PruneAsync(Sha(1));

        Assert.Empty(_docker.ImagesRemoved);
        Assert.Contains(_log, line => line.Contains("image retention"));
    }

    [Fact]
    public async Task A_removal_that_fails_is_logged_and_the_rest_continue()
    {
        _docker.ImageTags[Image] = [Sha(3), Sha(2), Sha(1)];
        _docker.ImageRemoveExitCode = 1;

        await PruneAsync(Sha(3), Sha(2), Sha(1));

        Assert.Equal(2, _docker.ImagesRemoved.Count);
        Assert.Contains(_log, line => line.Contains("image retention"));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef01234567", true)]
    [InlineData("0123456789ABCDEF0123456789abcdef01234567", true)]
    [InlineData("0123456789abcdef", false)]
    [InlineData("latest", false)]
    [InlineData("v1.2.3", false)]
    public void A_commit_tag_is_forty_hex_characters(string tag, bool expected)
    {
        Assert.Equal(expected, ImageRetainer.IsFullShaTag(tag));
    }
}
