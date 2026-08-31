using Boson.Deploy;
using Xunit;

namespace Boson.Tests;

public class ProjectLocksTests
{
    [Fact]
    public void TryEnter_is_exclusive_per_repo()
    {
        var locks = new ProjectLocks();
        Assert.True(locks.TryEnter("acme/site"));
        Assert.False(locks.TryEnter("acme/site"));
        Assert.True(locks.TryEnter("acme/other"));

        Assert.True(locks.IsHeld("acme/site"));
        locks.Exit("acme/site");
        Assert.False(locks.IsHeld("acme/site"));
        Assert.True(locks.TryEnter("acme/site"));
    }

    [Fact]
    public void Exit_of_unheld_lock_is_harmless()
    {
        var locks = new ProjectLocks();
        locks.Exit("acme/site");
        Assert.True(locks.TryEnter("acme/site"));
    }
}
