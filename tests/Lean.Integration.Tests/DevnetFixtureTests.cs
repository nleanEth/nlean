using NUnit.Framework;

namespace Lean.Integration.Tests;

[TestFixture]
[NonParallelizable]
public sealed class DevnetFixtureTests
{
    [Test]
    public void DevnetFixture_UsesOneSecondSlots_ByDefaultOutsideCi()
    {
        var originalCi = Environment.GetEnvironmentVariable("CI");
        var originalOverride = Environment.GetEnvironmentVariable("NLEAN_INTEG_SECONDS_PER_SLOT");

        try
        {
            Environment.SetEnvironmentVariable("CI", null);
            Environment.SetEnvironmentVariable("NLEAN_INTEG_SECONDS_PER_SLOT", null);

            using var fixture = new DevnetFixture(nodeCount: 1, basePort: 21000);
            var configYaml = File.ReadAllText(Path.Combine(fixture.ConfigDir, "config.yaml"));

            Assert.That(configYaml, Does.Contain("SECONDS_PER_SLOT: 1"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CI", originalCi);
            Environment.SetEnvironmentVariable("NLEAN_INTEG_SECONDS_PER_SLOT", originalOverride);
        }
    }
}