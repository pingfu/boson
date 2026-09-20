using Boson.Deploy;
using Xunit;

namespace Boson.Tests;

public class DeploymentLocksTests
{
    [Fact]
    public void TryEnter_is_exclusive_per_key()
    {
        var locks = new DeploymentLocks();
        Assert.True(locks.TryEnter("acme/site#main"));
        Assert.False(locks.TryEnter("acme/site#main"));
        Assert.True(locks.TryEnter("acme/other#main"));

        Assert.True(locks.IsHeld("acme/site#main"));
        locks.Exit("acme/site#main");
        Assert.False(locks.IsHeld("acme/site#main"));
        Assert.True(locks.TryEnter("acme/site#main"));
    }

    [Fact]
    public void Two_branches_of_one_repository_lock_separately()
    {
        var locks = new DeploymentLocks();

        Assert.True(locks.TryEnter(Deployer.LockKey("acme/site", "main")));
        Assert.True(locks.TryEnter(Deployer.LockKey("acme/site", "develop")));
    }

    [Fact]
    public void Exit_of_unheld_lock_is_harmless()
    {
        var locks = new DeploymentLocks();
        locks.Exit("acme/site#main");
        Assert.True(locks.TryEnter("acme/site#main"));
    }
}
