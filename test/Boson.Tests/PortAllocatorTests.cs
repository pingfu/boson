using Boson.Github;
using Boson.Platform;
using Xunit;

namespace Boson.Tests;

public class PortAllocatorTests
{
    [Fact]
    public void An_empty_host_gets_the_first_port_in_the_range()
    {
        Assert.Equal(PortAllocator.First, PortAllocator.Allocate([], _ => true));
    }

    [Fact]
    public void Ports_active_deployments_hold_are_skipped()
    {
        var claimed = new[] { PortAllocator.First, PortAllocator.First + 1 };

        Assert.Equal(PortAllocator.First + 2, PortAllocator.Allocate(claimed, _ => true));
    }

    [Fact]
    public void Ports_something_else_on_the_host_is_listening_on_are_skipped()
    {
        // The database knows only about boson's deployments; anything else on
        // the box is invisible to it and would collide at container start.
        var port = PortAllocator.Allocate([], p => p != PortAllocator.First);

        Assert.Equal(PortAllocator.First + 1, port);
    }

    [Fact]
    public void A_full_range_fails_with_a_message_rather_than_a_bad_port()
    {
        var e = Assert.Throws<BosonValidationException>(
            () => PortAllocator.Allocate([], _ => false));

        Assert.Contains(PortAllocator.First.ToString(), e.Message);
        Assert.Contains(PortAllocator.Last.ToString(), e.Message);
    }

    [Fact]
    public void The_range_sits_below_the_ephemeral_ports_the_kernel_hands_out()
    {
        Assert.True(PortAllocator.Last < 32768);
    }
}
