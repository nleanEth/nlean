using Lean.SpecTests.Runners;
using NUnit.Framework;

namespace Lean.SpecTests;

[TestFixture]
[Category("SpecTest")]
[Parallelizable(ParallelScope.Children)]
public sealed class SszSpecTests
{
    private static readonly SszRunner Runner = new();

    public static IEnumerable<TestCaseData> LoadTests() =>
        FixtureDiscovery.DiscoverTests("ssz");

    [TestCaseSource(nameof(LoadTests))]
    public void RunSszTest(string testId, string testJson)
    {
        Runner.Run(testId, testJson);
    }
}
