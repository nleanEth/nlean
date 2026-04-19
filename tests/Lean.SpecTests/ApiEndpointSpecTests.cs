using Lean.SpecTests.Runners;
using NUnit.Framework;

namespace Lean.SpecTests;

[TestFixture]
[Category("SpecTest")]
[Parallelizable(ParallelScope.None)] // HttpListener ports / aggregator state kept per-fixture
public sealed class ApiEndpointSpecTests
{
    private static readonly ApiEndpointRunner Runner = new();

    public static IEnumerable<TestCaseData> LoadTests() =>
        FixtureDiscovery.DiscoverTests("api_endpoint");

    [TestCaseSource(nameof(LoadTests))]
    public void RunApiEndpointTest(string testId, string testJson)
    {
        Runner.Run(testId, testJson);
    }
}
