using Boson.Deploy;
using Xunit;

namespace Boson.Tests;

public class BranchLabelTests
{
    [Fact]
    public void A_branch_that_is_already_a_label_is_left_alone()
    {
        Assert.Equal("main", BranchLabel.From("main"));
        Assert.Equal("release-2", BranchLabel.From("release-2"));
    }

    [Theory]
    [InlineData("feature/add-search")]
    [InlineData("Feature/Add-Search")]
    [InlineData("feature_add.search")]
    public void Anything_else_is_reduced_to_a_dns_label(string branch)
    {
        var label = BranchLabel.From(branch);

        Assert.Matches("^[a-z0-9-]+$", label);
        Assert.StartsWith("feature-add-search-", label);
    }

    [Fact]
    public void Branches_that_reduce_alike_still_get_their_own_label()
    {
        // The lossy part: `/` and `-` and case all collapse, so the hash of the
        // true name is what keeps two branches from one directory and one
        // hostname.
        Assert.NotEqual(BranchLabel.From("feature/search"), BranchLabel.From("feature-search"));
        Assert.NotEqual(BranchLabel.From("Feature/Search"), BranchLabel.From("feature/search"));
    }

    [Fact]
    public void The_same_branch_always_gets_the_same_label()
    {
        Assert.Equal(BranchLabel.From("feature/x"), BranchLabel.From("feature/x"));
    }

    [Fact]
    public void A_long_branch_fits_in_a_dns_label()
    {
        var label = BranchLabel.From(new string('a', 200));

        Assert.True(label.Length <= BranchLabel.MaxLength);
        Assert.Matches("^[a-z0-9-]+$", label);
    }

    [Fact]
    public void A_branch_with_nothing_usable_in_it_still_gets_a_label()
    {
        var label = BranchLabel.From("日本語");

        Assert.Matches("^[a-z0-9]+$", label);
    }
}
