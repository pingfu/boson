using Boson.Deploy;
using Xunit;

namespace Boson.Tests;

public class BranchSlugTests
{
    [Fact]
    public void A_branch_that_is_already_safe_is_left_alone()
    {
        Assert.Equal("main", BranchSlug.From("main"));
        Assert.Equal("release-2", BranchSlug.From("release-2"));
    }

    [Theory]
    [InlineData("feature/add-search")]
    [InlineData("Feature/Add-Search")]
    [InlineData("feature_add.search")]
    public void Anything_else_is_reduced_to_a_dns_label(string branch)
    {
        var slug = BranchSlug.From(branch);

        Assert.Matches("^[a-z0-9-]+$", slug);
        Assert.StartsWith("feature-add-search-", slug);
    }

    [Fact]
    public void Branches_that_reduce_alike_still_get_their_own_slug()
    {
        // The lossy part: `/` and `-` and case all collapse, so the hash of the
        // true name is what keeps two branches from one directory and one
        // hostname.
        Assert.NotEqual(BranchSlug.From("feature/search"), BranchSlug.From("feature-search"));
        Assert.NotEqual(BranchSlug.From("Feature/Search"), BranchSlug.From("feature/search"));
    }

    [Fact]
    public void The_same_branch_always_gets_the_same_slug()
    {
        Assert.Equal(BranchSlug.From("feature/x"), BranchSlug.From("feature/x"));
    }

    [Fact]
    public void A_long_branch_fits_in_a_dns_label()
    {
        var slug = BranchSlug.From(new string('a', 200));

        Assert.True(slug.Length <= BranchSlug.MaxLength);
        Assert.Matches("^[a-z0-9-]+$", slug);
    }

    [Fact]
    public void A_branch_with_nothing_usable_in_it_still_gets_a_slug()
    {
        var slug = BranchSlug.From("日本語");

        Assert.Matches("^[a-z0-9]+$", slug);
    }
}
