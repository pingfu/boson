using Boson.Platform;
using Boson.Tests.Support;
using Boson.Util;
using Xunit;

namespace Boson.Tests;

public class SystemdUnitTests
{
    [Fact]
    public void Unit_content_carries_the_spec_directives()
    {
        var content = SystemdUnit.Content;
        Assert.Contains("Type=simple", content);
        Assert.Contains("User=boson", content);
        Assert.Contains("Group=boson", content);
        Assert.Contains("ExecStart=/usr/local/bin/boson serve", content);
        Assert.Contains("WorkingDirectory=/var/lib/boson", content);
        Assert.Contains("Restart=always", content);
        Assert.Contains("RestartSec=2", content);
        Assert.Contains("TimeoutStopSec=600", content);
        Assert.Contains("NoNewPrivileges=true", content);
        Assert.Contains("After=network-online.target docker.service", content);
        Assert.Contains("WantedBy=multi-user.target", content);
    }

    [Fact]
    public void Write_is_idempotent_and_repairs_drift()
    {
        using var dirs = new TempDirs();
        var unitPath = Path.Combine(dirs.Root, "boson.service");
        var unit = new SystemdUnit(new MapProcessRunner(), unitPath);

        Assert.True(unit.WriteIfChanged());
        Assert.Equal(SystemdUnit.Content, File.ReadAllText(unitPath));
        Assert.False(unit.WriteIfChanged());

        File.WriteAllText(unitPath, "# drifted");
        Assert.True(unit.WriteIfChanged());
        Assert.Equal(SystemdUnit.Content, File.ReadAllText(unitPath));

        unit.Remove();
        Assert.False(File.Exists(unitPath));
        unit.Remove(); // idempotent
    }

    [Fact]
    public void ExecStart_matches_the_path_init_installs_to()
    {
        // A unit naming a path that holds no executable is systemd 203/EXEC.
        Assert.Contains($"ExecStart={SystemdUnit.ExecPath} serve", SystemdUnit.Content);
    }
}
