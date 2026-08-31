using Xunit;

namespace Boson.Tests.Integration;

/// <summary>Runs only when BOSON_INTEGRATION=1 (spec §20).</summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BOSON_INTEGRATION") != "1")
            Skip = "set BOSON_INTEGRATION=1 to run integration tests";
    }
}
