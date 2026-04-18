using Lean.SpecTests.Runners;
using NUnit.Framework;

namespace Lean.SpecTests;

[TestFixture]
[Category("SpecTest")]
[Parallelizable(ParallelScope.Children)]
public sealed class JustifiabilitySpecTests
{
    private static readonly JustifiabilityRunner Runner = new();

    public static IEnumerable<TestCaseData> LoadTests() =>
        FixtureDiscovery.DiscoverTests("justifiability");

    [TestCaseSource(nameof(LoadTests))]
    public void RunJustifiabilityTest(string testId, string testJson)
    {
        Runner.Run(testId, testJson);
    }
}
