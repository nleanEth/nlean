using Lean.SpecTests.Runners;
using NUnit.Framework;

namespace Lean.SpecTests;

[TestFixture]
[Category("SpecTest")]
[Parallelizable(ParallelScope.Children)]
public sealed class NetworkingCodecSpecTests
{
    private static readonly NetworkingCodecRunner Runner = new();

    public static IEnumerable<TestCaseData> LoadTests() =>
        FixtureDiscovery.DiscoverTests("networking_codec");

    [TestCaseSource(nameof(LoadTests))]
    public void RunNetworkingCodecTest(string testId, string testJson)
    {
        Runner.Run(testId, testJson);
    }
}
